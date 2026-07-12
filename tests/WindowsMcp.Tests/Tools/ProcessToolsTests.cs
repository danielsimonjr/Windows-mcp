using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class ProcessToolsTests
{
    private static ProcessTools MakeTools(
        IProcessService? process = null,
        IServiceControlService? service = null,
        ITaskSchedulerService? scheduler = null,
        IEventLogService? eventLog = null)
    {
        return new ProcessTools(
            process   ?? new Mock<IProcessService>().Object,
            service   ?? new Mock<IServiceControlService>().Object,
            scheduler ?? new Mock<ITaskSchedulerService>().Object,
            eventLog  ?? new Mock<IEventLogService>().Object);
    }

    private static ProcessTools Make(IProcessService process) => MakeTools(process: process);

    [Fact]
    public async Task Process_kill_requires_confirm_true()
    {
        var mock = new Mock<IProcessService>();
        var tools = MakeTools(process: mock.Object);

        Func<Task> act = () => tools.Process("kill", pid: 1234, confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.KillAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Service_stop_requires_confirm_true()
    {
        var mock = new Mock<IServiceControlService>();
        var tools = MakeTools(service: mock.Object);

        Func<Task> act = () => tools.Service("stop", name: "Spooler", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.StopAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScheduledTask_delete_requires_confirm_true()
    {
        var mock = new Mock<ITaskSchedulerService>();
        var tools = MakeTools(scheduler: mock.Object);

        Func<Task> act = () => tools.ScheduledTask("delete", name: "MyTask", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Process_orphans_calls_ListLineageAsync_with_orphansOnly_true()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListLineageAsync(true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessLineageDto>());
        var tools = Make(mock.Object);
        var json = await tools.Process("orphans");
        mock.Verify(m => m.ListLineageAsync(true, null, It.IsAny<CancellationToken>()), Times.Once);
        json.Should().Be("[]");
    }

    [Fact]
    public async Task Process_list_includeLineage_calls_ListLineageAsync_false()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListLineageAsync(false, "node", It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessLineageDto>());
        var tools = Make(mock.Object);
        await tools.Process("list", name: "node", includeLineage: true);
        mock.Verify(m => m.ListLineageAsync(false, "node", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Process_list_groupByRoot_calls_GroupByRootAsync()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.GroupByRootAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessGroupDto>());
        var tools = Make(mock.Object);
        await tools.Process("list", groupByRoot: true);
        mock.Verify(m => m.GroupByRootAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // The `name` filter was silently dropped on both non-lineage list paths: a filter matching
    // nothing returned the entire process table. Verifying only that the method was *called*
    // (not that the argument arrived) is what let this ship — so assert on the argument.
    [Fact]
    public async Task Process_list_groupByRoot_forwards_name_filter()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.GroupByRootAsync("chrome", It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessGroupDto>());
        var tools = Make(mock.Object);
        await tools.Process("list", name: "chrome", groupByRoot: true);
        mock.Verify(m => m.GroupByRootAsync("chrome", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Process_list_plain_forwards_name_filter()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListAsync("chrome", It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessDto>());
        var tools = Make(mock.Object);
        await tools.Process("list", name: "chrome");
        mock.Verify(m => m.ListAsync("chrome", It.IsAny<CancellationToken>()), Times.Once);
    }

    // A name-based kill must keep matching exactly (not by substring), so it must NOT reuse the
    // list filter — otherwise `kill --name node` would also kill `node-inspector`.
    [Fact]
    public async Task Process_kill_by_name_does_not_apply_the_substring_list_filter()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ProcessDto(1, "node", null, 10),
                new ProcessDto(2, "node-inspector", null, 10),
            });
        var tools = Make(mock.Object);
        await tools.Process("kill", name: "node", confirm: true);
        mock.Verify(m => m.KillAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(m => m.KillAsync(2, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Process_kill_tree_requires_confirm_and_calls_KillTreeAsync()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.KillTreeAsync(1234, null, It.IsAny<CancellationToken>())).ReturnsAsync(3);
        var tools = Make(mock.Object);
        var noConfirm = () => tools.Process("kill", pid: 1234, tree: true);
        await noConfirm.Should().ThrowAsync<System.ArgumentException>();
        var json = await tools.Process("kill", pid: 1234, tree: true, confirm: true);
        mock.Verify(m => m.KillTreeAsync(1234, null, It.IsAny<CancellationToken>()), Times.Once);
        json.Should().Contain("3");
    }

    [Fact]
    public async Task Process_kill_with_startTime_and_no_tree_calls_KillGuardedAsync()
    {
        var expected = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.KillGuardedAsync(1234, expected, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var tools = Make(mock.Object);

        var json = await tools.Process("kill", pid: 1234, confirm: true, startTime: "2026-07-08T12:00:00Z");

        mock.Verify(m => m.KillGuardedAsync(1234, expected, It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(m => m.KillAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(m => m.KillTreeAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        json.Should().Contain("verified");
    }

    [Fact]
    public async Task Process_kill_by_name_with_tree_or_startTime_is_rejected()
    {
        var mock = new Mock<IProcessService>();
        var tools = Make(mock.Object);

        var withTree = () => tools.Process("kill", name: "foo", tree: true, confirm: true);
        await withTree.Should().ThrowAsync<ArgumentException>().WithMessage("*require*pid*");

        var withStart = () => tools.Process("kill", name: "foo", confirm: true, startTime: "2026-07-08T12:00:00Z");
        await withStart.Should().ThrowAsync<ArgumentException>().WithMessage("*require*pid*");

        // Neither branch should have killed anything.
        mock.Verify(m => m.KillAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
