using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// F8: the execution backstop is LOUD for the callers that never asked for a clock.
/// <para>
/// Every PowerShell-backed service — disk, storage, security, firewall, network, file
/// streams, notification — calls <c>RunAsync(command, ct)</c>, the overload with no timeout
/// argument, and therefore has no <c>TimedOut</c> flag to read. When the 15-minute backstop tears
/// down a wedged script, <c>PowerShellService</c> throws a <see cref="TimeoutException"/> (proved
/// against the real process in <see cref="PowerShellServiceTests"/>); these rows prove the other
/// half — that each service lets it through to the caller instead of parsing the empty stdout of a
/// killed child into a confident, wrong answer. <c>disk_inspect mode:reclaimable</c> is the
/// standing example of what a swallowed failure costs.
/// </para>
/// <para>
/// Mocked deliberately: no real script can be made to hang for fifteen minutes in a unit suite, and
/// what is under test here is each service's own reaction, not PowerShell's.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PowerShellBackstopPropagationTests
{
    private const string Reason = "timed out after 900s (execution backstop)";

    /// <summary>A service whose no-clock overload does what the backstop does: throws, loudly.</summary>
    private static Mock<IPowerShellService> BackstopFires()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(s => s.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ThrowsAsync(new TimeoutException(Reason));
        return ps;
    }

    [Fact]
    public async Task NetworkService_ports_does_not_answer_an_empty_list_when_the_backstop_fired()
    {
        var network = new NetworkService(new Mock<ILogger<NetworkService>>().Object, BackstopFires().Object);

        Func<Task> act = () => network.ListPortsAsync();

        (await act.Should().ThrowAsync<TimeoutException>(
            "an empty stdout parses into zero ports, which says 'this machine is talking to nobody'"))
            .WithMessage($"*{Reason}*");
    }

    [Fact]
    public async Task SecurityService_audit_does_not_answer_probes_failed_when_the_backstop_fired()
    {
        var security = new SecurityService(BackstopFires().Object);

        Func<Task> act = () => security.AuditAsync();

        (await act.Should().ThrowAsync<TimeoutException>(
            "'all probes failed; likely no admin' would send the caller to elevate over a clock"))
            .WithMessage($"*{Reason}*");
    }

    [Fact]
    public async Task DiskService_reclaimable_does_not_swallow_the_backstop()
    {
        var disk = new DiskService(new Mock<IFileSystemService>().Object, BackstopFires().Object);

        Func<Task> act = () => disk.GetReclaimableAsync();

        (await act.Should().ThrowAsync<TimeoutException>(
            "the empty-output guard reports 'no output (exit ...)', which names the symptom and "
            + "hides the clock that caused it"))
            .WithMessage($"*{Reason}*");
    }

    [Fact]
    public async Task FileStreamService_does_not_answer_no_alternate_streams_when_the_backstop_fired()
    {
        var streams = new FileStreamService(BackstopFires().Object);

        Func<Task> act = () => streams.GetStreamsAsync(@"C:\Windows\System32\notepad.exe");

        (await act.Should().ThrowAsync<TimeoutException>(
            "an empty stdout parses into zero streams, which is a positive claim that the file has none"))
            .WithMessage($"*{Reason}*");
    }
}
