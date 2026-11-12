using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
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
        mock.Verify(s => s.KillAsync(It.IsAny<int>(), It.IsAny<KillOptions>(), It.IsAny<CancellationToken>()), Times.Never);
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

    /// <summary>E-1: restart is gated like stop - "Some changes are refused without confirm: true".</summary>
    [Fact]
    public async Task Service_restart_requires_confirm_true()
    {
        var mock = new Mock<IServiceControlService>();
        var tools = MakeTools(service: mock.Object);

        Func<Task> act = () => tools.Service("restart", name: "Spooler", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.VerifyNoOtherCalls();
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

    // C-3 R4: the plain list is the one that carries the CPU column, the order and the cap, so it
    // goes through ProcessListOptions. The name filter still has to arrive.
    [Fact]
    public async Task Process_list_plain_forwards_name_filter_with_the_default_options()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListAsync(It.IsAny<ProcessListOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessDto>());
        var tools = Make(mock.Object);
        await tools.Process("list", name: "chrome");
        mock.Verify(m => m.ListAsync(
            It.Is<ProcessListOptions>(o => o.NameFilter == "chrome"
                                           && o.SortBy == ProcessSort.Memory
                                           && o.Limit == 0),
            It.IsAny<CancellationToken>()), Times.Once,
            "sort_by defaults to memory and limit 0 means every row, as it always did");
    }

    // A name-based kill must keep matching exactly (not by substring), so it must NOT reuse the
    // list filter — otherwise `kill --name node` would also kill `node-inspector`.
    [Fact]
    public async Task Process_kill_by_name_does_not_apply_the_substring_list_filter()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListAsync((string?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ProcessDto(1, "node", null, 10),
                new ProcessDto(2, "node-inspector", null, 10),
            });
        mock.Setup(m => m.KillAsync(1, It.IsAny<KillOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KillResult(1, "node", false, false, true, 0));
        var tools = Make(mock.Object);
        await tools.Process("kill", name: "node", confirm: true);
        mock.Verify(m => m.KillAsync(1, It.IsAny<KillOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(m => m.KillAsync(2, It.IsAny<KillOptions>(), It.IsAny<CancellationToken>()), Times.Never);
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

    // C-3 R5: the start-time guard is now one of the kill's options, so a guarded kill returns the
    // same JSON as any other pid kill instead of a sentence.
    [Fact]
    public async Task Process_kill_with_startTime_and_no_tree_passes_the_guard_in_the_options()
    {
        var expected = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.KillAsync(1234, It.IsAny<KillOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KillResult(1234, "notepad", false, false, true, 0));
        var tools = Make(mock.Object);

        var json = await tools.Process("kill", pid: 1234, confirm: true, startTime: "2026-07-08T12:00:00Z");

        mock.Verify(m => m.KillAsync(1234,
            It.Is<KillOptions>(o => o.ExpectedStartUtc == expected), It.IsAny<CancellationToken>()), Times.Once);
        mock.Verify(m => m.KillAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(m => m.KillTreeAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        json.Should().Contain("\"killed\"");
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

    // ---- B-11: start_process gains argv, cwd and a JSON result -------------------------------

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Mock<IProcessService> Starting(int pid = 4242)
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.StartDetachedAsync(It.IsAny<ProcessStart>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pid);
        return mock;
    }

    [Fact]
    public async Task StartProcess_with_a_command_only_keeps_the_old_call_shape_and_reports_it()
    {
        // The compatibility row: an existing caller sends `command` and nothing else. The spec the
        // service receives says "no argv list, no cwd, no shell", and the JSON says the same.
        var mock = Starting();
        var tools = Make(mock.Object);

        var root = Parse(await tools.StartProcess("notepad.exe a.txt"));

        root.GetProperty("pid").GetInt32().Should().Be(4242);
        root.GetProperty("executable").GetString().Should().Be("notepad.exe a.txt");
        root.GetProperty("args").GetArrayLength().Should().Be(0, "no argv list was given");
        root.GetProperty("cwd").ValueKind.Should().Be(JsonValueKind.Null);
        mock.Verify(m => m.StartDetachedAsync(
            It.Is<ProcessStart>(s => s.Command == "notepad.exe a.txt" && s.Args == null
                                     && s.Cwd == null && !s.UseShellExecute),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartProcess_parses_args_json_into_the_spec_and_echoes_it_back()
    {
        var mock = Starting(77);
        var tools = Make(mock.Object);

        var root = Parse(await tools.StartProcess(
            @"C:\Windows\System32\cmd.exe",
            args_json: """["/c","echo","a \"quoted\" b"]""",
            cwd: @"C:\Windows"));

        root.GetProperty("pid").GetInt32().Should().Be(77);
        root.GetProperty("executable").GetString().Should().Be(@"C:\Windows\System32\cmd.exe");
        root.GetProperty("args").EnumerateArray().Select(a => a.GetString())
            .Should().Equal("/c", "echo", "a \"quoted\" b");
        root.GetProperty("cwd").GetString().Should().Be(@"C:\Windows");
        mock.Verify(m => m.StartDetachedAsync(
            It.Is<ProcessStart>(s => s.Args!.SequenceEqual(new[] { "/c", "echo", "a \"quoted\" b" })
                                     && s.Cwd == @"C:\Windows"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartProcess_passes_use_shell_execute_through()
    {
        var mock = Starting();
        var tools = Make(mock.Object);

        await tools.StartProcess("https://example.invalid", use_shell_execute: true);

        mock.Verify(m => m.StartDetachedAsync(
            It.Is<ProcessStart>(s => s.UseShellExecute), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("\"notastring\"")]
    [InlineData("notastring")]
    [InlineData("{}")]
    [InlineData("[1,2]")]
    [InlineData("""["ok",null]""")]
    public async Task StartProcess_rejects_an_args_json_that_is_not_an_array_of_strings(string argsJson)
    {
        var mock = Starting();
        var tools = Make(mock.Object);

        var act = () => tools.StartProcess("cmd.exe", args_json: argsJson);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("args_json");
        mock.Verify(m => m.StartDetachedAsync(It.IsAny<ProcessStart>(), It.IsAny<CancellationToken>()), Times.Never,
            "a malformed argument list is caught in the tool, before anything is started");
        mock.Verify(m => m.StartDetachedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StartProcess_treats_a_blank_cwd_as_no_cwd(string? cwd)
    {
        // "" is what an MCP client sends for a parameter the model left out, and it must not
        // reach the service as a directory called nothing (which would then be refused).
        var mock = Starting();
        var tools = Make(mock.Object);

        var root = Parse(await tools.StartProcess("cmd.exe", cwd: cwd));

        root.GetProperty("cwd").ValueKind.Should().Be(JsonValueKind.Null);
        mock.Verify(m => m.StartDetachedAsync(
            It.Is<ProcessStart>(s => s.Cwd == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartProcess_lets_a_missing_cwd_surface_as_the_services_refusal()
    {
        // The directory check belongs to the service (it is the one that must not spawn); the tool
        // does not swallow or re-wrap it.
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.StartDetachedAsync(It.IsAny<ProcessStart>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DirectoryNotFoundException(@"cwd 'C:\nope' does not exist"));
        var tools = Make(mock.Object);

        var act = () => tools.StartProcess("cmd.exe", cwd: @"C:\nope");

        (await act.Should().ThrowAsync<DirectoryNotFoundException>()).Which.Message.Should().Contain(@"C:\nope");
    }

    [Fact]
    public void StartProcess_describes_args_json_and_cwd()
    {
        var method = typeof(ProcessTools).GetMethod(nameof(ProcessTools.StartProcess))!;

        method.GetCustomAttribute<DescriptionAttribute>()!.Description.Should()
            .Contain("args_json", "the model only uses a parameter the description mentions")
            .And.Contain("cwd")
            .And.NotContain("not implemented");
        foreach (var name in new[] { "args_json", "cwd", "use_shell_execute" })
            method.GetParameters().Single(p => p.Name == name)
                .GetCustomAttribute<DescriptionAttribute>().Should().NotBeNull($"'{name}' needs its own description");
    }
    // ---- C-3 R5: sort_by, limit, graceful, grace_ms and the JSON kill result -------------------

    private static Mock<IProcessService> Listing()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListAsync(It.IsAny<ProcessListOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessDto>());
        return mock;
    }

    [Theory]
    [InlineData("memory", ProcessSort.Memory)]
    [InlineData("cpu", ProcessSort.Cpu)]
    [InlineData("name", ProcessSort.Name)]
    [InlineData("pid", ProcessSort.Pid)]
    [InlineData("CPU", ProcessSort.Cpu)]
    [InlineData("", ProcessSort.Memory)]        // an empty string is "not given", not an error
    [InlineData("  cpu  ", ProcessSort.Cpu)]    // trimmed, so a padded value is not a refusal
    public async Task Process_list_forwards_each_sort_by_name(string sortBy, ProcessSort expected)
    {
        var mock = Listing();
        var tools = Make(mock.Object);

        await tools.Process("list", sort_by: sortBy, limit: 5);

        mock.Verify(m => m.ListAsync(
            It.Is<ProcessListOptions>(o => o.SortBy == expected && o.Limit == 5),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Process_list_refuses_an_unknown_sort_by_naming_the_four()
    {
        var mock = Listing();
        var tools = Make(mock.Object);

        var act = () => tools.Process("list", sort_by: "ram");

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("sort_by").And.Contain("memory").And.Contain("cpu")
            .And.Contain("name").And.Contain("pid");
        mock.Verify(m => m.ListAsync(It.IsAny<ProcessListOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Process_list_refuses_a_negative_limit_by_name()
    {
        var mock = Listing();
        var tools = Make(mock.Object);

        var act = () => tools.Process("list", limit: -1);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("limit");
        mock.Verify(m => m.ListAsync(It.IsAny<ProcessListOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// C-3: the lineage, group and orphan shapes have no CPU column and no cap, so asking for one
    /// is refused rather than silently ignored - a caller who passed limit:5 must not get 300 rows.
    /// </summary>
    [Fact]
    public async Task Process_refuses_sort_by_or_limit_on_the_shapes_that_do_not_have_them()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListLineageAsync(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessLineageDto>());
        mock.Setup(m => m.GroupByRootAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessGroupDto>());
        var tools = Make(mock.Object);

        var withLineage = () => tools.Process("list", includeLineage: true, sort_by: "cpu");
        var withGroup = () => tools.Process("list", groupByRoot: true, limit: 5);
        var withOrphans = () => tools.Process("orphans", sort_by: "cpu");

        (await withLineage.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("sort_by");
        (await withGroup.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("limit");
        (await withOrphans.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("sort_by");
        mock.Verify(m => m.ListLineageAsync(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(m => m.GroupByRootAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// PR #25 review finding: the refusal above must fire on a sort_by the caller actually gave,
    /// not on the empty string an MCP client sends for an argument it is leaving out. The plain
    /// list's own parser already reads "" and whitespace as "not given"
    /// (<see cref="Process_list_forwards_each_sort_by_name"/> pins that), so the lineage, group and
    /// orphan shapes have to read them the same way - otherwise the identical call is accepted on
    /// one path and refused on another, and the refusal names a flag the caller never set.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Process_list_includeLineage_treats_a_blank_sort_by_as_not_given(string sortBy)
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListLineageAsync(false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessLineageDto>());
        var tools = Make(mock.Object);

        var json = await tools.Process("list", includeLineage: true, sort_by: sortBy);

        mock.Verify(m => m.ListLineageAsync(false, null, It.IsAny<CancellationToken>()), Times.Once);
        json.Should().Be("[]");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Process_list_groupByRoot_treats_a_blank_sort_by_as_not_given(string sortBy)
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.GroupByRootAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessGroupDto>());
        var tools = Make(mock.Object);

        var json = await tools.Process("list", groupByRoot: true, sort_by: sortBy);

        mock.Verify(m => m.GroupByRootAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        json.Should().Be("[]");
    }

    /// <summary>
    /// C-1 R4b-7: <c>orphans</c> IS the lineage shape, so <c>includeLineage</c> and
    /// <c>groupByRoot</c> are list-only options that mean nothing here. Ignoring them silently is
    /// the same failure <c>sort_by</c>/<c>limit</c> are refused for: a caller who asked for trees
    /// (<c>groupByRoot</c>) gets a flat list and cannot tell it apart from the trees they asked for.
    /// </summary>
    [Fact]
    public async Task Process_orphans_refuses_the_list_only_shape_flags()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListLineageAsync(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessLineageDto>());
        mock.Setup(m => m.GroupByRootAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessGroupDto>());
        var tools = Make(mock.Object);

        var withLineage = () => tools.Process("orphans", includeLineage: true);
        var withGroup = () => tools.Process("orphans", groupByRoot: true);

        (await withLineage.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("includeLineage", "the refusal names the flag the caller has to drop");
        (await withGroup.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("groupByRoot");
        mock.Verify(m => m.ListLineageAsync(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(m => m.GroupByRootAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Process_orphans_treats_a_blank_sort_by_as_not_given(string sortBy)
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListLineageAsync(true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessLineageDto>());
        var tools = Make(mock.Object);

        var json = await tools.Process("orphans", sort_by: sortBy);

        mock.Verify(m => m.ListLineageAsync(true, null, It.IsAny<CancellationToken>()), Times.Once);
        json.Should().Be("[]");
    }

    [Fact]
    public async Task Process_kill_by_pid_returns_the_kill_result_as_json()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.KillAsync(1234, It.IsAny<KillOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KillResult(1234, "notepad", true, true, false, 120));
        var tools = Make(mock.Object);

        var root = Parse(await tools.Process("kill", pid: 1234, confirm: true, graceful: true, grace_ms: 5000));

        var killed = root.GetProperty("killed");
        killed.GetArrayLength().Should().Be(1);
        killed[0].GetProperty("pid").GetInt32().Should().Be(1234);
        killed[0].GetProperty("name").GetString().Should().Be("notepad");
        killed[0].GetProperty("graceful").GetBoolean().Should().BeTrue();
        killed[0].GetProperty("exitedGracefully").GetBoolean().Should().BeTrue();
        killed[0].GetProperty("forced").GetBoolean().Should().BeFalse();
        killed[0].GetProperty("waitedMs").GetInt32().Should().Be(120);
        mock.Verify(m => m.KillAsync(1234,
            It.Is<KillOptions>(o => o.Graceful && o.GraceMs == 5000 && o.ExpectedStartUtc == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Process_kill_defaults_to_a_hard_kill_with_a_three_second_grace()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.KillAsync(7, It.IsAny<KillOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KillResult(7, "cmd", false, false, true, 0));
        var tools = Make(mock.Object);

        await tools.Process("kill", pid: 7, confirm: true);

        mock.Verify(m => m.KillAsync(7,
            It.Is<KillOptions>(o => !o.Graceful && o.GraceMs == 3000), It.IsAny<CancellationToken>()), Times.Once,
            "today's hard kill stays the default for every existing caller");
    }

    [Fact]
    public async Task Process_kill_by_name_returns_one_json_row_per_match_with_the_same_options()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListAsync((string?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProcessDto(1, "node", null, 10), new ProcessDto(2, "node", null, 20) });
        mock.Setup(m => m.KillAsync(1, It.IsAny<KillOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KillResult(1, "node", true, true, false, 15));
        mock.Setup(m => m.KillAsync(2, It.IsAny<KillOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KillResult(2, "node", true, false, true, 300));
        var tools = Make(mock.Object);

        var root = Parse(await tools.Process("kill", name: "node", confirm: true, graceful: true));

        var killed = root.GetProperty("killed");
        killed.EnumerateArray().Select(e => e.GetProperty("pid").GetInt32()).Should().Equal(new[] { 1, 2 });
        killed[1].GetProperty("forced").GetBoolean().Should().BeTrue();
        killed[1].GetProperty("waitedMs").GetInt32().Should().Be(300);
        mock.Verify(m => m.KillAsync(It.IsAny<int>(), It.Is<KillOptions>(o => o.Graceful),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Process_kill_refuses_graceful_with_tree()
    {
        var mock = new Mock<IProcessService>();
        var tools = Make(mock.Object);

        var act = () => tools.Process("kill", pid: 1234, confirm: true, tree: true, graceful: true);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("graceful").And.Contain("tree");
        mock.Verify(m => m.KillTreeAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(m => m.KillAsync(It.IsAny<int>(), It.IsAny<KillOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60_001)]
    public async Task Process_kill_refuses_a_grace_ms_outside_the_range(int graceMs)
    {
        var mock = new Mock<IProcessService>();
        var tools = Make(mock.Object);

        var act = () => tools.Process("kill", pid: 1234, confirm: true, graceful: true, grace_ms: graceMs);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("grace_ms");
        mock.Verify(m => m.KillAsync(It.IsAny<int>(), It.IsAny<KillOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60_000)]
    public async Task Process_kill_accepts_the_ends_of_the_grace_ms_range(int graceMs)
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.KillAsync(1234, It.IsAny<KillOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KillResult(1234, "x", true, false, true, 0));
        var tools = Make(mock.Object);

        await tools.Process("kill", pid: 1234, confirm: true, graceful: true, grace_ms: graceMs);

        mock.Verify(m => m.KillAsync(1234, It.Is<KillOptions>(o => o.GraceMs == graceMs),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Process_kill_by_name_with_no_match_returns_an_empty_killed_list()
    {
        // The JSON shape must hold when nothing matched: an empty array, not null and not a
        // sentence a caller would have to parse differently from the success case.
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.ListAsync((string?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProcessDto(1, "node", null, 10) });
        var tools = Make(mock.Object);

        var root = Parse(await tools.Process("kill", name: "nothing-by-that-name", confirm: true));

        root.GetProperty("killed").GetArrayLength().Should().Be(0);
        mock.Verify(m => m.KillAsync(It.IsAny<int>(), It.IsAny<KillOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The action and argument menus a model can only read from the error text.</summary>
    [Fact]
    public async Task Process_refuses_an_unknown_action_naming_the_three()
    {
        var mock = new Mock<IProcessService>();
        var tools = Make(mock.Object);

        var act = () => tools.Process("terminate");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("list").And.Contain("orphans").And.Contain("kill");
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Process_kill_without_a_pid_or_a_name_is_refused()
    {
        var mock = new Mock<IProcessService>();
        var tools = Make(mock.Object);

        var act = () => tools.Process("kill", confirm: true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("name").And.Contain("pid");
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Process_kill_refuses_a_malformed_startTime_before_killing_anything()
    {
        var mock = new Mock<IProcessService>();
        var tools = Make(mock.Object);

        var act = () => tools.Process("kill", pid: 1234, confirm: true, startTime: "yesterday");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("startTime").And.Contain("yesterday");
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public void Process_describes_its_new_parameters()
    {
        var method = typeof(ProcessTools).GetMethod(nameof(ProcessTools.Process))!;

        method.GetCustomAttribute<DescriptionAttribute>()!.Description.Should()
            .Contain("sort_by").And.Contain("limit").And.Contain("graceful").And.Contain("grace_ms")
            .And.NotContain("not implemented");
        foreach (var name in new[] { "sort_by", "limit", "graceful", "grace_ms" })
            method.GetParameters().Single(p => p.Name == name)
                .GetCustomAttribute<DescriptionAttribute>().Should().NotBeNull($"'{name}' needs its own description");
    }

    // ---- C-3 R4-10: two list shapes cannot both be the answer ---------------------------------

    /// <summary>
    /// R4-10: <c>includeLineage</c> and <c>groupByRoot</c> are two different result shapes and the
    /// tool silently picks the group one, so a caller who asked for lineage rows gets collapsed
    /// trees and no hint that their flag was dropped. Refuse, naming both — the same rule the
    /// tool already applies to sort_by/limit on those shapes.
    /// </summary>
    [Fact]
    public async Task Process_list_refuses_includeLineage_together_with_groupByRoot_naming_both()
    {
        var mock = new Mock<IProcessService>();
        mock.Setup(m => m.GroupByRootAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessGroupDto>());
        mock.Setup(m => m.ListLineageAsync(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Array.Empty<ProcessLineageDto>());
        var tools = Make(mock.Object);

        var act = () => tools.Process("list", includeLineage: true, groupByRoot: true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("includeLineage").And.Contain("groupByRoot",
                "a refusal that names only one of them leaves the caller guessing which to drop");
        mock.Verify(m => m.GroupByRootAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        mock.Verify(m => m.ListLineageAsync(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// C-3 R4-9: the two things about a graceful kill a caller cannot discover by trying it — that
    /// <c>grace_ms</c> is spent per process on a name kill (five matches can take five times as
    /// long), and that cancelling during the grace leaves the process running rather than killing
    /// it. Both are decisions, and the description is where a model reads them.
    /// </summary>
    [Fact]
    public void Process_describes_the_per_process_grace_and_what_cancelling_does()
    {
        var description = typeof(ProcessTools).GetMethod(nameof(ProcessTools.Process))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().MatchEquivalentOf("*grace_ms*per process*",
            "a name kill spends the grace period on each match, so the wall clock is grace_ms x matches");
        description.Should().ContainEquivalentOf("cancel",
            "cancelling mid-grace leaves the process alive to answer the close - the opposite of what "
            + "a caller who cancels a 'kill' would assume");
    }

}
