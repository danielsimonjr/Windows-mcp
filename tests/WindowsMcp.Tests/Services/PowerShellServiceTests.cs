using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class PowerShellServiceTests
{
    [Fact]
    public async Task RunAsync_executes_simple_echo_and_captures_stdout()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("'hello from PS'");
        result.Success.Should().BeTrue();
        result.Stdout.Trim().Should().Be("hello from PS");
    }

    // REGRESSION: `powershell -Command -` with the script piped to stdin evaluates input
    // LINE BY LINE as separate statements, so any multi-line construct is silently mangled and
    // the process still exits 0 with EMPTY stdout. This made disk_inspect mode:reclaimable
    // return nothing on exit 0. The script must be parsed as a single unit.
    [Fact]
    public async Task RunAsync_multiline_hashtable_literal_produces_output()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var script = "[PSCustomObject]@{\n    Alpha = 1\n    Beta  = 2\n} | ConvertTo-Json";
        var result = await svc.RunAsync(script);
        result.Stdout.Should().NotBeNullOrWhiteSpace("a multi-line script must not silently produce nothing");
        result.Stdout.Should().Contain("Alpha").And.Contain("Beta");
    }

    [Fact]
    public async Task RunAsync_multiline_try_catch_executes_as_one_unit()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var script = "try {\n    $v = 6 * 7\n    Write-Output $v\n} catch {\n    Write-Output 'failed'\n}";
        var result = await svc.RunAsync(script);
        result.Stdout.Trim().Should().Be("42");
    }

    [Fact]
    public async Task RunAsync_multiline_foreach_accumulates_across_lines()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var script = "$total = 0\nforeach ($i in 1..4) {\n    $total += $i\n}\nWrite-Output $total";
        var result = await svc.RunAsync(script);
        result.Stdout.Trim().Should().Be("10");
    }

    // Guards the temp-file fallback: stdin had no length limit, but a command line does
    // (~32767 chars), so a large script must still run rather than regress.
    [Fact]
    public async Task RunAsync_very_large_script_still_executes()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var padding = string.Join("\n", Enumerable.Range(0, 1500).Select(i => $"# filler comment line {i} ----------"));
        var script = padding + "\n[PSCustomObject]@{\n    Big = 'yes'\n} | ConvertTo-Json";
        script.Length.Should().BeGreaterThan(12_000, "the test must actually exceed the EncodedCommand budget");
        var result = await svc.RunAsync(script);
        result.Stdout.Should().Contain("Big");
    }

    // UTF-16LE encoding correctness: non-ASCII must survive the round trip.
    [Fact]
    public async Task RunAsync_preserves_non_ascii_characters()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("Write-Output 'em—dash café ✓'");
        result.Stdout.Should().Contain("em—dash").And.Contain("café").And.Contain("✓");
    }

    // The paired negative control: filtering CLIXML must NOT make failures look green.
    // NOTE: this service invokes via -EncodedCommand, where a non-terminating failure (unknown
    // cmdlet) still EXITS 0 and announces itself only on stderr. So the exit code alone cannot
    // carry Success - a first attempt at this fix keyed on it and made the invalid-command test
    // pass a genuinely failed command.
    [Fact]
    public async Task RunAsync_still_reports_failure_when_stderr_carries_a_real_diagnostic()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("Get-DoesNotExistCommand");
        result.Success.Should().BeFalse("a real diagnostic on stderr is still a failure");
        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().NotContain(e => e.StartsWith("#< CLIXML"), "scaffolding must be filtered even on failures");
    }

    // CLIXML is TRANSPORT framing, but one <Objs> document carries BOTH progress and error
    // records. Dropping the whole line therefore discards real diagnostics - that was tried and
    // it made a genuinely failed command report success. Extract the Error payloads instead.
    [Fact]
    public void ParseErrors_extracts_error_records_and_drops_progress_only_documents()
    {
        var lf = ((char)10).ToString();
        var progressOnly = "#< CLIXML" + lf +
            """<Objs Version="1.1.0.1"><Obj S="progress" RefId="0"><MS><PR N="Record"><AV>Preparing modules for first use.</AV></PR></MS></Obj></Objs>""";
        PowerShellService.ParseErrors(progressOnly).Should().BeEmpty("a progress-only document is pure noise");

        var withError =
            """<Objs Version="1.1.0.1"><Obj S="progress" RefId="0"><MS><PR N="Record"><AV>Preparing modules.</AV></PR></MS></Obj><S S="Error">Get-Nope : not recognized_x000D__x000A_</S></Objs>""";
        var errors = PowerShellService.ParseErrors(withError);
        errors.Should().ContainSingle();
        errors[0].Should().Contain("Get-Nope").And.Contain("not recognized");
        errors[0].Should().NotContain("_x000D_", "CR/LF placeholders must be decoded away");
        errors[0].Should().NotContain("<S S=", "the caller should never see transport markup");
    }

    // Plain (non-CLIXML) stderr must pass through untouched.
    [Fact]
    public void ParseErrors_passes_plain_stderr_through()
    {
        var lf2 = ((char)10).ToString();
        var errors = PowerShellService.ParseErrors("plain failure line" + lf2 + "second line");
        errors.Should().BeEquivalentTo(new[] { "plain failure line", "second line" });
    }

    // REGRESSION (EVO-X2, 2026-08-23): defender_status returned every field null with
    // "The JSON value could not be converted ... Path: $.FullScanEndTime". Cause: a calculated
    // property whose scriptblock emits NOTHING serializes as an empty OBJECT {}, which cannot
    // convert to DateTime? and fails the entire DTO. The machine had simply never completed a
    // full scan. This asserts the shape the fix depends on, using the same Select-Object form.
    [Fact]
    public async Task RunAsync_calculated_property_emits_json_null_not_empty_object()
    {
        using var svc = new PowerShellService(NullLogger.Instance);

        var withoutElse = await svc.RunAsync(
            "[PSCustomObject]@{X=$null} | Select-Object @{n='X';e={if($_.X){$_.X.ToString('o')}}} | ConvertTo-Json");
        withoutElse.Stdout.Should().Contain("{", "this documents the BUG shape: an emitting-nothing scriptblock yields an empty object");

        var withElse = await svc.RunAsync(
            "[PSCustomObject]@{X=$null} | Select-Object @{n='X';e={if($_.X){$_.X.ToString('o')}else{$null}}} | ConvertTo-Json");
        withElse.Stdout.Should().Contain("null", "an explicit $null must serialize as JSON null so DateTime? can bind");
    }

    [Fact]
    public async Task RunAsync_returns_error_for_invalid_command()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("Get-DoesNotExistCommand");
        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RunAsync_serialized_calls_preserve_per_caller_output()
    {
        // Fire N calls concurrently; the service's gate serializes them and each caller must get
        // back exactly its own output. The property (serialization + no cross-caller contamination)
        // is independent of N — N is only a stress knob. Kept modest on purpose: every call spawns
        // a fresh powershell.exe, and a Defender-scanned cold-start is ~15-18 s here, so a large N
        // measures antivirus scan time, not the serialization logic (and previously blew the
        // per-call backstop for queued callers — since fixed by starting the backstop after the
        // gate is acquired rather than before).
        const int N = 12;
        using var svc = new PowerShellService(NullLogger.Instance);
        var tasks = Enumerable.Range(0, N).Select(i =>
            svc.RunAsync($"'{i}'")).ToArray();
        var results = await Task.WhenAll(tasks);
        for (int i = 0; i < N; i++)
            results[i].Stdout.Trim().Should().Be(i.ToString());
    }

    [Fact]
    public async Task RunAsync_dispose_throws_object_disposed_exception()
    {
        var svc = new PowerShellService(NullLogger.Instance);
        svc.Dispose();
        Func<Task> act = () => svc.RunAsync("'never reached'");
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task RunAsync_backstop_timeout_tears_down_a_runaway_script()
    {
        // Short backstop; a 30s sleep would hang the gate forever without the timeout.
        using var svc = new PowerShellService(NullLogger.Instance, TimeSpan.FromMilliseconds(500));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Func<Task> act = () => svc.RunAsync("Start-Sleep -Seconds 30");

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task RunAsync_honors_caller_cancellation_token()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Func<Task> act = () => svc.RunAsync("Start-Sleep -Seconds 30", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }
}

internal sealed class NullLogger : Microsoft.Extensions.Logging.ILogger
{
    public static readonly NullLogger Instance = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}
