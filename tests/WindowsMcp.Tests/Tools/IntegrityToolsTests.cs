using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class IntegrityToolsTests
{
    private static IntegrityTools Make(IIntegrityService? integrity = null)
        => new(integrity ?? new Mock<IIntegrityService>().Object);

    [Fact]
    public async Task Baseline_dispatches()
    {
        var mock = new Mock<IIntegrityService>();
        mock.Setup(s => s.BaselineAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntegrityBaseline(DateTime.UtcNow, Array.Empty<string>(), Array.Empty<IntegrityItem>()));

        var json = await Make(mock.Object).Integrity("baseline");

        json.Should().Contain("Roots");
        mock.Verify(s => s.BaselineAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Check_dispatches()
    {
        var mock = new Mock<IIntegrityService>();
        mock.Setup(s => s.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntegrityCheckResult(null, DateTime.UtcNow, false, 0, Array.Empty<IntegrityChange>()));

        var json = await Make(mock.Object).Integrity("check");

        json.Should().Contain("HasBaseline");
        mock.Verify(s => s.CheckAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_dispatches()
    {
        var mock = new Mock<IIntegrityService>();
        mock.Setup(s => s.DefaultWatchList()).Returns(new[] { @"C:\Windows\System32\drivers\etc\hosts" });
        mock.Setup(s => s.GetBaseline()).Returns((IntegrityBaseline?)null);

        var json = await Make(mock.Object).Integrity("list");

        json.Should().Contain("watchList").And.Contain("hosts");
        mock.Verify(s => s.DefaultWatchList(), Times.Once);
        mock.Verify(s => s.GetBaseline(), Times.Once);
    }

    [Fact]
    public async Task Unknown_mode_throws()
    {
        var act = () => Make().Integrity("explode");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*mode*");
    }
}
