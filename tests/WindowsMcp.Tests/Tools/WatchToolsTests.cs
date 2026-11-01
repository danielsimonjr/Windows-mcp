using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class WatchToolsTests
{
    private static WatchTools Make(IWatchService? watch = null)
        => new(watch ?? new Mock<IWatchService>().Object);

    [Fact]
    public void Start_requires_path()
    {
        var mock = new Mock<IWatchService>();
        var tools = Make(mock.Object);

        var act = () => tools.Watch("start", path: null);
        act.Should().Throw<ArgumentException>().WithMessage("*path*");
        mock.Verify(s => s.Start(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void Poll_requires_id()
    {
        var mock = new Mock<IWatchService>();
        var tools = Make(mock.Object);

        var act = () => tools.Watch("poll", id: null);
        act.Should().Throw<ArgumentException>().WithMessage("*id*");
        mock.Verify(s => s.Poll(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void Stop_requires_id()
    {
        var mock = new Mock<IWatchService>();
        var tools = Make(mock.Object);

        var act = () => tools.Watch("stop", id: "  ");
        act.Should().Throw<ArgumentException>().WithMessage("*id*");
        mock.Verify(s => s.Stop(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void List_dispatches()
    {
        var mock = new Mock<IWatchService>();
        mock.Setup(s => s.List())
            .Returns(new[] { new WatchSession("w1", @"C:\tmp", "*", false, 0, 0) });

        var json = Make(mock.Object).Watch("list");

        json.Should().Contain("w1");
        mock.Verify(s => s.List(), Times.Once);
    }

    [Fact]
    public void Unknown_mode_throws()
    {
        var act = () => Make().Watch("explode");
        act.Should().Throw<ArgumentException>().WithMessage("*mode*");
    }

    [Fact]
    public void Poll_serializes_events()
    {
        var when = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc);
        var mock = new Mock<IWatchService>();
        mock.Setup(s => s.Poll("w1", 500))
            .Returns(new[] { new WatchEvent("created", @"C:\tmp\a.txt", when) });

        var json = Make(mock.Object).Watch("poll", id: "w1");

        json.Should().Contain("created").And.Contain("a.txt");
        mock.Verify(s => s.Poll("w1", 500), Times.Once);
    }
}
