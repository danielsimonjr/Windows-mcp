using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class ProcessLineageTests
{
    static readonly DateTime Now = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
    static Win32ProcRow Row(int pid, int ppid, string name, DateTime? created,
        string? cmd = null, long mem = 0) => new(pid, ppid, name, created, cmd, mem);

    [Fact]
    public void From_parses_cim_datetime_and_coerces_workingset()
    {
        var row = new Dictionary<string, object>
        {
            ["ProcessId"] = 100, ["ParentProcessId"] = 4, ["Name"] = "svchost.exe",
            ["CreationDate"] = "20260708070935.590000-300",
            ["CommandLine"] = "svchost -k netsvcs", ["WorkingSetSize"] = (ulong)(50 * 1024 * 1024),
        };
        var r = ProcessLineage.From(row);
        r.Should().NotBeNull();
        r!.Value.Pid.Should().Be(100);
        r.Value.CreationUtc.Should().NotBeNull();
        r.Value.CreationUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        r.Value.MemoryMb.Should().Be(50);
    }

    [Fact]
    public void From_tolerates_missing_date_string_workingset_and_skips_rows_without_pid()
    {
        ProcessLineage.From(new Dictionary<string, object> { ["Name"] = "x" }).Should().BeNull();
        var r = ProcessLineage.From(new Dictionary<string, object>
        {
            ["ProcessId"] = 4, ["ParentProcessId"] = 0, ["Name"] = "System",
            ["CreationDate"] = "", ["WorkingSetSize"] = "1048576",
        });
        r!.Value.CreationUtc.Should().BeNull();
        r.Value.MemoryMb.Should().Be(1);
    }

    [Fact]
    public void Classify_marks_dead_parent_as_orphan()
    {
        var rows = new[] { Row(10, 999, "node.exe", Now.AddMinutes(-30)) }; // ppid 999 absent
        var dto = ProcessLineage.Classify(rows, Now).Single();
        dto.Orphaned.Should().BeTrue();
        dto.ParentName.Should().BeNull();
        dto.RootPid.Should().Be(10);
        dto.AgeMinutes.Should().Be(30);
        dto.RuntimeKind.Should().Be("node");
    }

    [Fact]
    public void Classify_marks_recycled_parent_as_orphan_but_not_genuine_parent()
    {
        var rows = new[]
        {
            Row(1, 0, "System", Now.AddMinutes(-100)),
            Row(20, 1, "child.exe", Now.AddMinutes(-50)),   // parent older -> genuine
            Row(30, 40, "kid.exe", Now.AddMinutes(-50)),
            Row(40, 0, "reused.exe", Now.AddMinutes(-10)),  // "parent" younger -> recycled
        };
        var map = ProcessLineage.Classify(rows, Now).ToDictionary(d => d.Pid);
        map[20].Orphaned.Should().BeFalse();
        map[30].Orphaned.Should().BeTrue();   // recycled parent
    }

    [Fact]
    public void Classify_null_dated_parent_is_not_treated_as_recycled()
    {
        var rows = new[]
        {
            Row(4, 0, "System", null),                       // no CIM date
            Row(50, 4, "wininit.exe", Now.AddMinutes(-200)),
        };
        ProcessLineage.Classify(rows, Now).Single(d => d.Pid == 50).Orphaned.Should().BeFalse();
    }

    [Fact]
    public void Classify_walks_multi_level_root_and_guards_cycles()
    {
        var rows = new[]
        {
            Row(1, 0, "root.exe", Now.AddMinutes(-90)),
            Row(2, 1, "mid.exe", Now.AddMinutes(-80)),
            Row(3, 2, "leaf.exe", Now.AddMinutes(-70)),
            Row(7, 8, "a.exe", Now.AddMinutes(-60)),         // mutual cycle 7<->8
            Row(8, 7, "b.exe", Now.AddMinutes(-60)),
        };
        var map = ProcessLineage.Classify(rows, Now).ToDictionary(d => d.Pid);
        map[3].RootPid.Should().Be(1);
        map[7].RootPid.Should().BeOneOf(7, 8); // terminates, no infinite loop
    }

    [Fact]
    public void GroupByRoot_counts_and_lists_children()
    {
        var rows = new[]
        {
            Row(1, 0, "claude.exe", Now.AddMinutes(-90)),
            Row(2, 1, "node.exe", Now.AddMinutes(-80)),
            Row(3, 1, "node.exe", Now.AddMinutes(-80)),
        };
        var groups = ProcessLineage.GroupByRoot(ProcessLineage.Classify(rows, Now));
        var g = groups.Single(x => x.RootPid == 1);
        g.DescendantCount.Should().Be(3);
        g.ChildPids.Should().BeEquivalentTo(new[] { 1, 2, 3 });
        g.RootName.Should().Be("claude.exe");
    }

    // A filtered group keeps its FULL membership and true DescendantCount — the filter selects
    // which trees to show, it never trims a tree. Trimming would make DescendantCount mean
    // "matching descendants", which reads as "descendants" and misleads.
    [Fact]
    public void GroupByRoot_filter_returns_whole_trees_that_contain_a_match()
    {
        var rows = new[]
        {
            Row(1, 0, "explorer.exe", Now.AddMinutes(-90)),
            Row(2, 1, "claude.exe", Now.AddMinutes(-80)),
            Row(3, 1, "svchost.exe", Now.AddMinutes(-80)),
            Row(10, 0, "chrome.exe", Now.AddMinutes(-70)),
            Row(11, 10, "chrome.exe", Now.AddMinutes(-60)),
        };
        var groups = ProcessLineage.GroupByRoot(ProcessLineage.Classify(rows, Now), "claude");

        // Only explorer's tree contains a claude match; chrome's tree is dropped entirely.
        groups.Should().ContainSingle();
        var g = groups.Single();
        g.RootPid.Should().Be(1);
        g.DescendantCount.Should().Be(3);                        // true count, not 1
        g.ChildPids.Should().BeEquivalentTo(new[] { 1, 2, 3 });  // full membership, incl. non-matching
    }

    [Fact]
    public void GroupByRoot_filter_matches_command_line_and_is_case_insensitive()
    {
        var rows = new[]
        {
            Row(1, 0, "explorer.exe", Now.AddMinutes(-90)),
            Row(2, 1, "node.exe", Now.AddMinutes(-80), cmd: @"node C:\tools\WIDGET\server.js"),
            Row(10, 0, "chrome.exe", Now.AddMinutes(-70)),
        };
        var groups = ProcessLineage.GroupByRoot(ProcessLineage.Classify(rows, Now), "widget");
        groups.Should().ContainSingle();
        groups.Single().RootPid.Should().Be(1);
    }

    [Fact]
    public void GroupByRoot_filter_with_no_match_returns_empty_not_everything()
    {
        var rows = new[]
        {
            Row(1, 0, "explorer.exe", Now.AddMinutes(-90)),
            Row(2, 1, "node.exe", Now.AddMinutes(-80)),
        };
        // The live bug: a filter matching nothing returned the ENTIRE process table.
        ProcessLineage.GroupByRoot(ProcessLineage.Classify(rows, Now), "zzz_no_such_process")
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GroupByRoot_without_filter_returns_all_groups(string? filter)
    {
        var rows = new[]
        {
            Row(1, 0, "explorer.exe", Now.AddMinutes(-90)),
            Row(10, 0, "chrome.exe", Now.AddMinutes(-70)),
        };
        ProcessLineage.GroupByRoot(ProcessLineage.Classify(rows, Now), filter)
            .Should().HaveCount(2);
    }

    [Fact]
    public void IsSystemAdjacent_flags_boot_processes()
    {
        ProcessLineage.IsSystemAdjacent(Row(9, 0, "explorer.exe", Now)).Should().BeTrue();
        ProcessLineage.IsSystemAdjacent(Row(9, 100, "node.exe", Now)).Should().BeFalse();
    }

    [Fact]
    public void Classify_depth_cap_stops_a_chain_deeper_than_64_hops()
    {
        // Non-cyclic ancestor chain of 200 (pid i's parent is the older pid i-1; pid 1's parent 0
        // is absent). Exercises the 64-hop depth cap directly — the seen-set guard never fires here
        // because there is no cycle, so only the hop counter can terminate the walk.
        var rows = new List<Win32ProcRow>();
        for (int i = 1; i <= 200; i++)
            rows.Add(Row(i, i - 1, $"p{i}.exe", Now.AddMinutes(-(300 - i))));

        var map = ProcessLineage.Classify(rows, Now).ToDictionary(d => d.Pid);

        map.Should().HaveCount(200);                // terminated — no hang / stack overflow
        map[200].RootPid.Should().BeGreaterThan(1); // cap cut the walk short of the true root (pid 1)
    }
}
