using System.Net;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class PowerShellService : IPowerShellService
{
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _backstop;
    private bool _disposed;

    // Backstop so a runaway script (e.g. an accidental `while($true){}`) can't hold the
    // serialization gate forever and wedge every PowerShell-backed tool. Deliberately generous —
    // longer than any legitimate caller budget (storage_health caps its own CTS at 300s). The
    // normal cancellation path is the caller's CancellationToken; this is the last-resort teardown.
    private static readonly TimeSpan DefaultBackstop = TimeSpan.FromMinutes(10);

    // System PowerShell is guaranteed present at this path on Windows 7+.
    // Avoids the broken InitialSessionState.CreateDefault2 path in the PS NuGet
    // SDK when running under PublishSingleFile=true: Assembly.Location returns ""
    // in single-file mode, then Path.Combine chokes inside PSSnapInReader.
    // Snap-in DLLs are not bundled in the single-file image.
    private const string PowerShellExe =
        @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    public PowerShellService(ILogger<PowerShellService> log) : this((ILogger)log, null) { }

    // Test ctor accepting non-generic ILogger (+ optional shorter backstop for tests).
    public PowerShellService(ILogger log, TimeSpan? backstopTimeout = null)
    {
        _log = log;
        _backstop = backstopTimeout ?? DefaultBackstop;
    }

    public async Task<PSResult> RunAsync(string command, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PowerShellService));
        ct.ThrowIfCancellationRequested();

        string? scriptFileToDelete = null;

        // Acquire the gate under the CALLER's token only. The backstop must bound this call's
        // *execution*, not the time it spends queued behind other callers — otherwise a caller
        // deep in the queue can burn its entire "runaway-script" budget just waiting, and get
        // cancelled before its own (perfectly fine) command ever runs.
        await _gate.WaitAsync(ct);
        try
        {
            // Now that we hold the gate, start the execution backstop and link the caller's token.
            using var timeoutCts = new CancellationTokenSource(_backstop);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var token = linkedCts.Token;

            // Build the invocation. See BuildArguments for why stdin is NOT used.
            var (arguments, tempScript) = await BuildArgumentsAsync(command, token);
            scriptFileToDelete = tempScript;

            // -NoProfile: skip user profile load (faster, deterministic)
            // -NonInteractive: never prompt
            // -ExecutionPolicy Bypass: allow scripts
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellExe,
                Arguments = arguments,
                // Still redirect stdin even though we never write to it: this process is an MCP
                // STDIO server, so our own stdin is the JSON-RPC channel. An un-redirected child
                // would INHERIT that handle and could consume protocol bytes. Redirect and close.
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            // Register the kill BEFORE any await: if cancellation (caller or backstop) fires
            // early, the child must still be torn down or it orphans.
            using var ctReg = token.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            });

            // Close stdin immediately — the script is passed via the command line, and leaving
            // the pipe open would make PowerShell wait for input that never comes.
            proc.StandardInput.Close();

            // Read both streams concurrently to avoid pipe deadlock on large output.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(token);
            var stderrTask = proc.StandardError.ReadToEndAsync(token);

            await proc.WaitForExitAsync(token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var errors = ParseErrors(stderr);

            return new PSResult(
                // Success requires exit 0 AND no REAL diagnostics. The second half was already
                // here; what was wrong is that `errors` used to include CLIXML transport
                // scaffolding, so a healthy command that merely emitted a progress record
                // ("Preparing modules for first use") was reported as a failure - a real
                // `dotnet build` printing "Build succeeded. 0 Error(s)" came back Success:false.
                // ParseErrors now drops that scaffolding, so this condition means what it says.
                //
                // The exit code alone is NOT sufficient here: this service invokes via
                // -EncodedCommand, where a non-terminating failure such as an unknown cmdlet
                // still exits 0 and reports itself only on stderr. Keying Success solely on the
                // exit code was tried and made `RunAsync_returns_error_for_invalid_command` pass
                // a genuinely failed command.
                Success: proc.ExitCode == 0 && errors.Length == 0,
                Stdout: stdout,
                Stderr: stderr,
                ExitCode: proc.ExitCode,
                Errors: errors);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "PowerShell execution failed");
            return new PSResult(false, "", ex.Message, -1, new[] { ex.Message });
        }
        finally
        {
            if (scriptFileToDelete is not null)
            {
                try { File.Delete(scriptFileToDelete); }
                catch (Exception ex) { _log.LogWarning(ex, "Failed to delete temp script {Path}", scriptFileToDelete); }
            }
            _gate.Release();
        }
    }

    // Windows caps a command line at 32767 chars. -EncodedCommand base64s UTF-16LE, so the
    // encoded form is ~2.67x the script length; stay well clear of the ceiling.
    private const int MaxEncodedCommandChars = 30_000;

    /// <summary>
    /// Produces the powershell.exe arguments for <paramref name="command"/>, plus the path of a
    /// temp script file to delete afterwards (null when none was needed).
    /// </summary>
    /// <remarks>
    /// We deliberately do NOT use <c>-Command -</c> with the script piped to stdin. PowerShell
    /// reads piped stdin and evaluates it LINE BY LINE as independent statements, so every
    /// multi-line construct (hashtable literal, try/catch, foreach, function, wrapped assignment)
    /// is silently mangled — and the process still exits 0 with EMPTY stdout. That is what made
    /// <c>disk_inspect mode:reclaimable</c> return nothing on exit 0: its script ends in a
    /// multi-line <c>[PSCustomObject]@{...} | ConvertTo-Json</c>. Piping also left the input
    /// encoding at the console default, corrupting non-ASCII.
    ///
    /// <c>-EncodedCommand</c> passes the script as one base64 UTF-16LE blob: parsed as a single
    /// unit, encoding explicit, no quoting hazards. Its only limit is the command-line length —
    /// and since stdin had no such limit, an oversized script falls back to a temp <c>.ps1</c>
    /// run with <c>-File</c> so large scripts do not regress.
    /// </remarks>
    private static async Task<(string Arguments, string? TempScript)> BuildArgumentsAsync(
        string command, CancellationToken token)
    {
        const string CommonFlags = "-NoProfile -NonInteractive -ExecutionPolicy Bypass";

        // We read the child's stdout as UTF-8 (StandardOutputEncoding), but Windows PowerShell 5.1
        // WRITES stdout in the console OEM codepage, so non-ASCII arrives corrupted (café -> caf?).
        // Force the writer side to match the reader side. Kept to one line and try/caught so it can
        // never break a caller's script; `catch {}` deliberately swallows, as failing to set an
        // encoding must not fail the command.
        const string EncodingPreamble =
            "try{[Console]::OutputEncoding=[System.Text.Encoding]::UTF8}catch{}\n";

        var payload = EncodingPreamble + command;

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(payload));
        if (encoded.Length <= MaxEncodedCommandChars)
            return ($"{CommonFlags} -EncodedCommand {encoded}", null);

        // Too long for a command line: write it out and run the file instead.
        // UTF-8 *with BOM* — Windows PowerShell 5.1 assumes the ANSI codepage for a BOM-less
        // file and mangles non-ASCII (the em-dash parse trap).
        var path = Path.Combine(Path.GetTempPath(), $"winmcp-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(path, payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), token);
        return ($"{CommonFlags} -File \"{path}\"", path);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    /// <summary>
    /// Split PowerShell stderr into diagnostic lines, discarding CLIXML transport scaffolding.
    /// </summary>
    /// <remarks>
    /// With stderr redirected, powershell.exe serialises error AND PROGRESS records as CLIXML: a
    /// "#&lt; CLIXML" preamble followed by an &lt;Objs&gt; document. Routine progress ("Preparing
    /// modules for first use") therefore appears on stderr during a completely healthy run, so
    /// treating any stderr as failure mislabels healthy runs. Keeping the scaffolding in Errors
    /// also buries real messages under a wall of XML. This filters the TRANSPORT only - anything
    /// that is not scaffolding is preserved verbatim.
    /// </remarks>
    public static string[] ParseErrors(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return Array.Empty<string>();

        var results = new List<string>();
        foreach (var line in stderr.Split((char)10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The "#< CLIXML" preamble is pure transport framing.
            if (line.StartsWith("#< CLIXML", StringComparison.Ordinal)) continue;

            if (line.StartsWith("<Objs", StringComparison.Ordinal))
            {
                // CRITICAL: one <Objs> document carries BOTH progress AND error records, so the
                // line cannot be dropped wholesale - that discards the actual diagnostics. Pull
                // out the Error-typed strings and leave the progress records behind.
                var matches = ErrorRecord.Matches(line);
                if (matches.Count == 0) continue;   // progress-only document: genuinely noise

                foreach (Match m in matches)
                {
                    // PowerShell encodes CR and LF inside the payload as _x000D_ / _x000A_.
                    var text = m.Groups[1].Value
                        .Replace("_x000D_", string.Empty, StringComparison.Ordinal)
                        .Replace("_x000A_", string.Empty, StringComparison.Ordinal);
                    text = WebUtility.HtmlDecode(text).Trim();
                    if (text.Length > 0) results.Add(text);
                }
                continue;
            }

            results.Add(line);
        }

        return results.ToArray();
    }

    // Error payloads inside a CLIXML <Objs> document appear as <S S="Error">text</S>.
    private static readonly Regex ErrorRecord =
        new("<S S=\"Error\">(.*?)</S>", RegexOptions.Compiled | RegexOptions.Singleline);

}
