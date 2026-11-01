using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

// Read-only integration: a real Windows host always has registered tasks (Microsoft
// maintenance/telemetry tasks ship with the OS), so these assertions are stable.
[Trait("Category", "Integration")]
public class TaskSchedulerServiceTests
{
    [Fact]
    public async Task ListDetailed_returns_tasks_across_folders_with_paths()
    {
        var svc = new TaskSchedulerService();

        var tasks = await svc.ListDetailedAsync();

        tasks.Should().NotBeEmpty();
        tasks.Should().OnlyContain(t => !string.IsNullOrEmpty(t.Path));
        // The full tree spans sub-folders, not just the root folder.
        tasks.Should().Contain(t => t.Path.TrimStart('\\').Contains('\\'));
    }

    [Fact]
    public async Task ListDetailed_extracts_action_paths_and_triggers()
    {
        var svc = new TaskSchedulerService();

        var tasks = await svc.ListDetailedAsync();

        tasks.Should().Contain(t => t.ActionPath != null);   // exec-action extraction works
        tasks.Should().Contain(t => t.Triggers.Length > 0);  // trigger extraction works
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("onlogon")]
    [InlineData("logon")]
    [InlineData("onboot")]
    [InlineData("boot")]
    [InlineData("onidle")]
    [Trait("Category", "Unit")]
    public void ParseTrigger_accepts_named_triggers(string trigger)
    {
        var t = TaskSchedulerService.ParseTrigger(trigger);
        t.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ParseTrigger_accepts_iso_datetime()
    {
        var t = TaskSchedulerService.ParseTrigger("2026-08-30T12:00:00Z");
        t.Should().BeOfType<Microsoft.Win32.TaskScheduler.TimeTrigger>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ParseTrigger_rejects_unknown()
    {
        var act = () => TaskSchedulerService.ParseTrigger("whenever");
        act.Should().Throw<ArgumentException>().WithMessage("*trigger*");
    }
}
