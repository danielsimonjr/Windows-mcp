using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

// CaptureAsync calls Graphics.CopyFromScreen, which requires an interactive desktop
// session — it throws Win32Exception "The handle is invalid" under headless/service
// sessions (local non-interactive runs and GitHub-hosted Windows runners alike). That
// is the same constraint as the UIAutomation bucket, so it is categorized here to be
// excluded by the documented headless-safe filter (Category!=UIAutomation), not left
// mislabeled as read-only Integration.
[Trait("Category", "UIAutomation")]
public class ScreenshotServiceTests
{
    [Fact]
    public async Task CaptureAsync_returns_non_empty_png_with_dimensions()
    {
        var service = new ScreenshotService();
        var result = await service.CaptureAsync(new ScreenRegion(0, 0, 100, 100), ImageFormat.Png);

        result.Bytes.Should().NotBeNull().And.NotBeEmpty();
        result.Width.Should().Be(100);
        result.Height.Should().Be(100);
        result.Format.Should().Be(ImageFormat.Png);
        // PNG magic bytes: 89 50 4E 47
        result.Bytes.Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    }
}
