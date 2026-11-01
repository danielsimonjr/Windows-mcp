using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class UsnToolsTests
{
    private static UsnTools Make(IUsnService? usn = null)
        => new(usn ?? new Mock<IUsnService>().Object);

    [Fact]
    public async Task Status_dispatches_and_forwards_volume()
    {
        var mock = new Mock<IUsnService>();
        mock.Setup(s => s.StatusAsync("D", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UsnStatus("D:", 1, 0, 100, 0));

        var json = await Make(mock.Object).FsChanges("status", volume: "D");

        json.Should().Contain("D:");
        mock.Verify(s => s.StatusAsync("D", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Since_dispatches_and_forwards_volume()
    {
        var mock = new Mock<IUsnService>();
        mock.Setup(s => s.ReadAsync("E", 5, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UsnReadResult("E:", 20, 0, Array.Empty<UsnChange>()));

        var json = await Make(mock.Object).FsChanges("since", volume: "E", start_usn: 5, max: 10);

        json.Should().Contain("E:");
        mock.Verify(s => s.ReadAsync("E", 5, 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unknown_mode_throws()
    {
        var act = () => Make().FsChanges("explode");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*mode*");
    }
}
