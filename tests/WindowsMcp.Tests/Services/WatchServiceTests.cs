using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public sealed class WatchServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly WatchService _svc = new();

    public WatchServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wmcp-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Start_returns_a_session_that_appears_in_list()
    {
        var s = _svc.Start(_dir, "*.exe", includeSubdirectories: true);
        s.Id.Should().NotBeNullOrEmpty();
        s.Path.Should().Be(_dir);
        s.Filter.Should().Be("*.exe");
        s.IncludeSubdirectories.Should().BeTrue();

        _svc.List().Should().ContainSingle(x => x.Id == s.Id);
    }

    [Fact]
    public void Start_on_missing_directory_throws()
    {
        var missing = Path.Combine(_dir, "does-not-exist");
        var act = () => _svc.Start(missing, null, false);
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void Poll_unknown_id_throws()
    {
        var act = () => _svc.Poll("nope", 10);
        act.Should().Throw<KeyNotFoundException>().WithMessage("*nope*");
    }

    [Fact]
    public void Stop_removes_the_session_and_is_idempotent()
    {
        var s = _svc.Start(_dir, null, false);
        _svc.Stop(s.Id).Should().BeTrue();
        _svc.List().Should().NotContain(x => x.Id == s.Id);
        _svc.Stop(s.Id).Should().BeFalse(); // already gone
    }

    [Fact]
    public void List_reports_default_filter_star_when_none_given()
    {
        var s = _svc.Start(_dir, null, false);
        s.Filter.Should().Be("*");
    }

    [Fact]
    public void Start_beyond_MaxSessions_throws()
    {
        for (var i = 0; i < WatchService.MaxSessions; i++)
            _svc.Start(_dir, null, false);

        var act = () => _svc.Start(_dir, null, false);
        act.Should().Throw<InvalidOperationException>().WithMessage("*watch sessions*");
    }
}
