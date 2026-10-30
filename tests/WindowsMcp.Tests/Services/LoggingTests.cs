using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WindowsMcp;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// The console redirect is process-wide, so the tests that use it never run beside another test.
/// </summary>
[CollectionDefinition(nameof(ConsoleRedirect), DisableParallelization = true)]
public sealed class ConsoleRedirect { }

/// <summary>
/// A stderr log line is only useful when it tells when it happened, and the stamp must keep its
/// form on every machine. stdout carries JSON-RPC, so no log line may go there.
/// </summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleRedirect))]
public class LoggingTests
{
    const string StampedProbeLine = @"(?m)^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} info: Probe\[0\]\r?$";

    /// <summary>Logs one information entry through <see cref="Program.ConfigureLogging"/> and returns both streams.</summary>
    static (string Out, string Err) LogOnce(string message, string culture = "en-US")
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalCulture = CultureInfo.CurrentCulture;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        // The formatter renders the stamp on the calling thread, in that thread's culture.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging(Program.ConfigureLogging);
            // Disposing the provider drains the console logger's queue, so every line is written.
            using (var provider = services.BuildServiceProvider())
                provider.GetRequiredService<ILoggerFactory>().CreateLogger("Probe").LogInformation("{Message}", message);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            CultureInfo.CurrentCulture = originalCulture;
        }
        return (stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void A_log_entry_opens_with_the_local_date_and_time_to_the_millisecond()
    {
        var (_, err) = LogOnce("stamp probe");

        err.Should().MatchRegex(StampedProbeLine,
            "the simple formatter must be named, or the provider drops the timestamp");
        err.Should().Contain("stamp probe");
    }

    [Fact]
    public void The_stamp_keeps_its_colons_in_a_culture_whose_time_separator_is_not_a_colon()
    {
        CultureInfo.GetCultureInfo("fi-FI").DateTimeFormat.TimeSeparator.Should().Be(".",
            "the test needs a culture that would change an unquoted ':'");

        var (_, err) = LogOnce("culture probe", "fi-FI");

        err.Should().MatchRegex(StampedProbeLine, "a log line must read the same on every machine");
    }

    [Fact]
    public void A_log_entry_goes_to_stderr_and_never_to_stdout()
    {
        var probe = $"probe-{Guid.NewGuid():N}";

        var (stdout, stderr) = LogOnce(probe);

        stderr.Should().Contain(probe);
        stdout.Should().NotContain(probe, "stdout is the JSON-RPC channel");
    }
}
