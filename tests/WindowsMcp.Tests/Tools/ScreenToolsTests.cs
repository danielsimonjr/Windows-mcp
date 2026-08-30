using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class ScreenToolsTests
{
    [Fact]
    public async Task Screenshot_returns_base64_png()
    {
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var shotMock = new Mock<IScreenshotService>();
        shotMock
            .Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<ImageFormat>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(pngBytes, 100, 100, ImageFormat.Png));

        var tools = new ScreenTools(shotMock.Object, new Mock<IOcrService>().Object);
        // output:"base64" required — the tool now defaults to output:"file" (returns a saved path,
        // no inline data), so this base64-intent test must opt into base64 mode explicitly.
        var result = await tools.Screenshot(null, "png", "base64");

        result.Should().Contain(Convert.ToBase64String(pngBytes));
        result.Should().Contain("100");
    }

    [Fact]
    public async Task Ocr_dispatches()
    {
        var ocr = new Mock<IOcrService>();
        ocr.Setup(s => s.ExtractTextAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("hello world");
        var tools = new ScreenTools(new Mock<IScreenshotService>().Object, ocr.Object);

        var result = await tools.Ocr(null);

        result.Should().Be("hello world");
        ocr.Verify(s => s.ExtractTextAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("1,2,3")]
    [InlineData("a,b,c,d")]
    [InlineData("0,0,-1,10")]
    public async Task Screenshot_rejects_invalid_region(string region)
    {
        var shot = new Mock<IScreenshotService>();
        var tools = new ScreenTools(shot.Object, new Mock<IOcrService>().Object);

        var act = () => tools.Screenshot(region);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*region*");
        shot.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<ImageFormat>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
