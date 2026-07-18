using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public sealed class IntegrityServiceTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _store;

    public IntegrityServiceTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "wmcp-integ-" + Guid.NewGuid().ToString("N"));
        _store = Path.Combine(_tmp, "store");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* best-effort temp cleanup */ }
    }

    private IntegrityService Make(params string[] watch) => new(_store, () => watch);

    [Fact]
    public async Task Baseline_then_Check_detects_modified_removed_and_added()
    {
        var a = Path.Combine(_tmp, "a.txt");
        var b = Path.Combine(_tmp, "b.txt");
        var startup = Path.Combine(_tmp, "startup");
        Directory.CreateDirectory(startup);
        var c = Path.Combine(startup, "c.lnk");
        File.WriteAllText(a, "1");
        File.WriteAllText(b, "2");
        File.WriteAllText(c, "3");

        var svc = Make(a, b, startup);
        var baseline = await svc.BaselineAsync();
        baseline.Items.Should().HaveCount(3); // a, b, and c (expanded from the startup dir)

        File.WriteAllText(a, "1-changed");                     // modified
        File.Delete(b);                                        // removed
        File.WriteAllText(Path.Combine(startup, "d.lnk"), "4"); // added under a watched dir

        var result = await svc.CheckAsync();

        result.HasBaseline.Should().BeTrue();
        result.Unchanged.Should().Be(1); // c
        result.Changes.Should().HaveCount(3);
        result.Changes.Should().Contain(ch => ch.Path == a && ch.Kind == "modified");
        result.Changes.Should().Contain(ch => ch.Path == b && ch.Kind == "removed");
        result.Changes.Should().Contain(ch => ch.Path.EndsWith("d.lnk") && ch.Kind == "added");
    }

    [Fact]
    public async Task Watched_but_absent_file_appearing_is_flagged_added()
    {
        var ghost = Path.Combine(_tmp, "later.txt"); // absent at baseline
        var svc = Make(ghost);
        var baseline = await svc.BaselineAsync();
        baseline.Items.Should().ContainSingle(i => i.Path == ghost && !i.Exists);

        File.WriteAllText(ghost, "surprise");
        var result = await svc.CheckAsync();
        result.Changes.Should().ContainSingle(ch => ch.Path == ghost && ch.Kind == "added");
    }

    [Fact]
    public async Task Check_with_no_baseline_reports_HasBaseline_false()
    {
        var svc = Make(Path.Combine(_tmp, "nope.txt"));
        var result = await svc.CheckAsync();
        result.HasBaseline.Should().BeFalse();
        result.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Baseline_is_persisted_and_reloadable()
    {
        var a = Path.Combine(_tmp, "a.txt");
        File.WriteAllText(a, "x");
        await Make(a).BaselineAsync();

        var reloaded = Make(a).GetBaseline(); // fresh instance, same store
        reloaded.Should().NotBeNull();
        reloaded!.Items.Should().ContainSingle(i => i.Path == a && i.Exists);
    }

    [Fact]
    public async Task Unchanged_files_produce_no_changes()
    {
        var a = Path.Combine(_tmp, "a.txt");
        File.WriteAllText(a, "stable");
        var svc = Make(a);
        await svc.BaselineAsync();

        var result = await svc.CheckAsync();
        result.Changes.Should().BeEmpty();
        result.Unchanged.Should().Be(1);
    }
}
