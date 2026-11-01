using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// A-7: <c>screenshot</c> returns MCP content blocks (<see cref="CallToolResult"/>) instead of a
/// JSON string, so the model sees the picture from one call. Everything here is mocked — the real
/// capture path is covered by <c>ScreenshotServiceTests</c> (UIAutomation) and the transport
/// round-trip by <c>HttpTransportScreenshotImageTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class ScreenToolsTests : IDisposable
{
    // 8 bytes that are not valid base64 of anything accidental, and start with the PNG magic.
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

    /// <summary>Files the "file" output mode wrote under %TEMP%\WindowsMcp, removed in Dispose.</summary>
    private readonly List<string> _written = [];

    public void Dispose()
    {
        foreach (var path in _written)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    // ---- harness --------------------------------------------------------------------------

    private static Mock<IScreenshotService> ShotMock(
        byte[]? bytes = null, int width = 100, int height = 100, ImageFormat? resultFormat = null,
        int? originalWidth = null, int? originalHeight = null, double coordinateScale = 1.0,
        string? cursorDrawn = null, int annotationsDrawn = 0, StageTiming[]? stages = null,
        string resultBackend = "gdi")
    {
        var mock = new Mock<IScreenshotService>();
        mock.Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ScreenRegion? _, CaptureOptions? opts, CancellationToken _) =>
            {
                var effective = resultFormat ?? opts?.Format ?? ImageFormat.Png;
                return new ScreenshotResult(
                    bytes ?? (effective == ImageFormat.Jpeg ? JpegBytes : PngBytes),
                    width, height, effective,
                    originalWidth ?? width, originalHeight ?? height, coordinateScale, cursorDrawn,
                    annotationsDrawn, stages, resultBackend);
            });
        return mock;
    }

    // ---- A-8: the desktops a test can put the tool on ---------------------------------------

    /// <summary>One primary 1920x1080 at the origin — the desktop every test gets unless it says otherwise.</summary>
    internal static MonitorInfo[] SingleMonitor => [new(0, "Monitor0", 0, 0, 1920, 1080, true)];

    /// <summary>
    /// Two 1920x1080 side by side, primary at the origin: virtual screen (0,0,3840,1080),
    /// display 1 = (1920,0,1920,1080).
    /// </summary>
    internal static MonitorInfo[] SideBySide =>
    [
        new(0, "Monitor0", 0, 0, 1920, 1080, true),
        new(1, "Monitor1", 1920, 0, 1920, 1080, false),
    ];

    /// <summary>
    /// The secondary sits left of and above the primary: virtual screen (-1920,-40,3840,1120).
    /// EnumDisplayMonitors does not order by position, so index 1 is the one with the negative origin.
    /// </summary>
    internal static MonitorInfo[] LeftOfPrimary =>
    [
        new(0, "Monitor0", 0, 0, 1920, 1080, true),
        new(1, "Monitor1", -1920, -40, 1920, 1080, false),
    ];

    private static Mock<IWindowService> WinMock(MonitorInfo[]? monitors = null)
    {
        var mock = new Mock<IWindowService>();
        mock.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(monitors ?? SingleMonitor);
        return mock;
    }

    /// <summary>
    /// A-11: every screenshot reads the cursor, so the tool now needs an <see cref="IInputService"/>.
    /// The default point (100,100) is inside the primary display of every desktop above, which
    /// makes the expected <c>cursor.monitorIndex</c> 0 unless a test moves it.
    /// </summary>
    private static Mock<IInputService> InputMock(int x = 100, int y = 100)
    {
        var mock = new Mock<IInputService>();
        mock.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPosition(x, y));
        return mock;
    }

    /// <summary>
    /// A-6: the element source <c>annotate</c> draws from. The default returns an EMPTY snapshot,
    /// so every pre-A-6 test behaves exactly as it did — a tool that called it anyway would still
    /// have nothing to draw, which is why the annotate tests below verify the call itself.
    /// </summary>
    internal static SnapshotResult EmptySnapshot =>
        new([], null, new CursorPosition(0, 0), -1, [], [], null, false, 500, 0, 0);

    private static Mock<IUIAutomationService> UiaMock(SnapshotResult? snapshot = null)
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(u => u.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot ?? EmptySnapshot);
        return mock;
    }

    /// <summary>
    /// A-14: the post-capture glow. Every screenshot hides it before the shutter and shows it
    /// after, so every test in this class now has one - the default mock records the calls and
    /// does nothing else.
    /// </summary>
    /// <summary>Like the real overlay on a desktop: visible once Show has run. The tool reports IsVisible, not the request.</summary>
    private static Mock<IFlashOverlay> FlashMock()
    {
        var mock = new Mock<IFlashOverlay>();
        mock.Setup(f => f.Show(It.IsAny<ScreenRegion>(), It.IsAny<TimeSpan>()))
            .Callback(() => mock.SetupGet(f => f.IsVisible).Returns(true));
        return mock;
    }

    private static ScreenTools MakeTools(
        IScreenshotService? shot = null, IOcrService? ocr = null, ScreenshotOptions? options = null,
        IWindowService? windows = null, IInputService? input = null, IUIAutomationService? uia = null,
        IFlashOverlay? flash = null, ILogger<ScreenTools>? log = null) =>
        new(shot ?? ShotMock().Object, ocr ?? new Mock<IOcrService>().Object,
            windows ?? WinMock().Object, input ?? InputMock().Object, uia ?? UiaMock().Object,
            flash ?? FlashMock().Object, options, log);

    /// <summary>The whole primary display — what a call with neither region nor display captures (roadmap C3).</summary>
    private static readonly ScreenRegion PrimaryRect = new(0, 0, 1920, 1080);

    /// <summary>The single <see cref="CaptureOptions"/> the tool handed to the service.</summary>
    private static CaptureOptions CapturedOptions(Mock<IScreenshotService> mock)
    {
        var calls = mock.Invocations
            .Where(i => i.Method.Name == nameof(IScreenshotService.CaptureAsync))
            .ToList();
        calls.Should().ContainSingle("the tool captures exactly once per call");
        return calls[0].Arguments[1].Should().BeOfType<CaptureOptions>().Subject;
    }

    // The metadata block is always the FIRST text block; with annotate:true a second text block
    // (the element list) follows it, so this must not assume a single one.
    private static TextContentBlock TextBlock(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().First();

    private static ImageContentBlock ImageBlock(CallToolResult result) =>
        result.Content.OfType<ImageContentBlock>().Should().ContainSingle().Subject;

    /// <summary>
    /// SDK 2.2.0: <c>ImageContentBlock.Data</c> is the <b>base64 UTF-8 bytes</b> that go on the
    /// wire (<c>DecodedData</c> is the raw image). This is the base64 text a client sees.
    /// </summary>
    private static string Base64Of(ImageContentBlock block) => Encoding.UTF8.GetString(block.Data.Span);

    /// <summary>
    /// A metadata field that the contract says is always present — asserted as present first so a
    /// missing field fails with the field's name rather than a bare KeyNotFoundException.
    /// </summary>
    private static JsonElement Field(JsonElement meta, string name)
    {
        meta.TryGetProperty(name, out var value).Should().BeTrue($"the metadata must carry '{name}'");
        return value;
    }

    /// <summary>Asserts a metadata rect equals <paramref name="expected"/> (x/y/width/height).</summary>
    private static void ShouldBeRect(JsonElement rect, ScreenRegion expected)
    {
        rect.GetProperty("x").GetInt32().Should().Be(expected.X);
        rect.GetProperty("y").GetInt32().Should().Be(expected.Y);
        rect.GetProperty("width").GetInt32().Should().Be(expected.Width);
        rect.GetProperty("height").GetInt32().Should().Be(expected.Height);
    }

    /// <summary>The metadata object carried by the single text block.</summary>
    private static JsonElement Meta(CallToolResult result)
    {
        using var doc = JsonDocument.Parse(TextBlock(result).Text);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object, "the text block is one JSON object of metadata");
        return doc.RootElement.Clone();
    }

    /// <summary>Records the path a "file" capture wrote so Dispose can clean it up.</summary>
    private string TrackPath(CallToolResult result)
    {
        var path = Meta(result).GetProperty("path").GetString()!;
        _written.Add(path);
        return path;
    }

    // ---- R1 / R2 — inline is the default, text then image ----------------------------------

    [Fact]
    public async Task Screenshot_default_output_is_inline_text_then_image()
    {
        var mock = ShotMock(JpegBytes, 640, 480);
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot();

        result.IsError.Should().NotBe(true, "a successful capture is not an error result");
        result.Content.Should().HaveCount(2, "inline returns exactly one metadata block and one image block");
        result.Content[0].Should().BeOfType<TextContentBlock>("the metadata block comes first");
        result.Content[1].Should().BeOfType<ImageContentBlock>("the image block comes second");
    }

    [Fact]
    public async Task Screenshot_inline_image_block_carries_the_captured_bytes()
    {
        var mock = ShotMock(JpegBytes, 640, 480);
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot(null, format: "jpeg");

        var image = ImageBlock(result);
        Base64Of(image).Should().Be(Convert.ToBase64String(JpegBytes), "Data is the base64 of exactly the captured bytes");
        image.DecodedData.ToArray().Should().Equal(JpegBytes);
        image.MimeType.Should().Be("image/jpeg");
    }

    // ---- R10 — the old base64 test, rewritten against the new contract ----------------------

    [Fact]
    public async Task Screenshot_returns_base64_png()
    {
        var mock = ShotMock(PngBytes, 100, 100);
        var tools = MakeTools(mock.Object);

        // output:"base64" is kept as an alias of "inline" for one release (A-7).
        var result = await tools.Screenshot(null, format: "png", output: "base64");

        Base64Of(ImageBlock(result)).Should().Be(Convert.ToBase64String(PngBytes));
        ImageBlock(result).MimeType.Should().Be("image/png");
        Meta(result).GetProperty("width").GetInt32().Should().Be(100);
        Meta(result).GetProperty("height").GetInt32().Should().Be(100);
    }

    // ---- R3 — base64 is an alias of inline -------------------------------------------------

    [Fact]
    public async Task Screenshot_base64_output_is_identical_to_inline()
    {
        var tools = MakeTools(ShotMock(PngBytes, 320, 240).Object);

        var inline = await tools.Screenshot("10,20,320,240", format: "png", output: "inline");
        var alias = await tools.Screenshot("10,20,320,240", format: "png", output: "base64");

        TextBlock(alias).Text.Should().Be(TextBlock(inline).Text, "'base64' is an alias, not a different shape");
        Base64Of(ImageBlock(alias)).Should().Be(Base64Of(ImageBlock(inline)));
        ImageBlock(alias).MimeType.Should().Be(ImageBlock(inline).MimeType);
        alias.Content.Should().HaveCount(inline.Content.Count);
    }

    // ---- R4 — file output ------------------------------------------------------------------

    [Fact]
    public async Task Screenshot_file_output_returns_only_a_text_block()
    {
        var tools = MakeTools(ShotMock(PngBytes, 800, 600).Object);

        var result = await tools.Screenshot(null, format: "png", output: "file");

        result.Content.Should().ContainSingle("file mode returns the path only — no image block");
        result.Content[0].Should().BeOfType<TextContentBlock>();
        result.Content.OfType<ImageContentBlock>().Should().BeEmpty();

        var meta = Meta(result);
        _written.Add(meta.GetProperty("path").GetString()!);
        meta.GetProperty("width").GetInt32().Should().Be(800);
        meta.GetProperty("height").GetInt32().Should().Be(600);
        meta.GetProperty("format").GetString().Should().Be("png");
        meta.GetProperty("coordinateSpace").GetString().Should().Be("virtual-desktop");
    }

    [Fact]
    public async Task Screenshot_file_output_writes_the_bytes_under_temp_windowsmcp()
    {
        var tools = MakeTools(ShotMock(PngBytes, 800, 600).Object);

        var result = await tools.Screenshot(null, format: "png", output: "file");

        var path = TrackPath(result);
        var expectedDir = Path.Combine(Path.GetTempPath(), "WindowsMcp");
        Path.GetDirectoryName(path).Should().Be(expectedDir.TrimEnd(Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue("the reported path must actually exist");
        (await File.ReadAllBytesAsync(path)).Should().Equal(PngBytes, "the file holds the captured bytes");
    }

    [Theory]
    [InlineData("png", ".png")]
    [InlineData("jpeg", ".jpg")]
    public async Task Screenshot_file_output_extension_matches_the_format(string format, string extension)
    {
        var tools = MakeTools(ShotMock().Object);

        var result = await tools.Screenshot(null, format: format, output: "file");

        TrackPath(result).Should().EndWith(extension);
    }

    // ---- R5 — output validation ------------------------------------------------------------

    [Theory]
    [InlineData("path")]
    [InlineData("")]
    [InlineData("inline ")]
    [InlineData("image")]
    public async Task Screenshot_unknown_output_throws_naming_the_choices(string output)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        Func<Task> act = () => tools.Screenshot(null, format: "png", output: output);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("inline").And.Contain("file").And.Contain("base64");
        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never, "the mode is validated before anything is captured");
    }

    [Theory]
    [InlineData("INLINE")]
    [InlineData("Inline")]
    [InlineData("BASE64")]
    public async Task Screenshot_output_matching_is_case_insensitive(string output)
    {
        var tools = MakeTools(ShotMock().Object);

        var result = await tools.Screenshot(null, format: "png", output: output);

        result.Content.Should().HaveCount(2);
        Base64Of(ImageBlock(result)).Should().Be(Convert.ToBase64String(PngBytes));
    }

    [Fact]
    public async Task Screenshot_file_output_matching_is_case_insensitive()
    {
        var tools = MakeTools(ShotMock().Object);

        var result = await tools.Screenshot(null, format: "png", output: "FiLe");

        result.Content.Should().ContainSingle();
        TrackPath(result).Should().EndWith(".png");
    }

    // ---- R6 — format resolution -------------------------------------------------------------

    [Theory]
    [InlineData("inline")]
    [InlineData("base64")]
    public async Task Screenshot_format_auto_resolves_to_jpeg_for_inline_output(string output)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot(null, format: "auto", output: output);

        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.Is<CaptureOptions>(o => o.Format == ImageFormat.Jpeg), It.IsAny<CancellationToken>()), Times.Once);
        ImageBlock(result).MimeType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task Screenshot_format_auto_resolves_to_png_for_file_output()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot(null, format: "auto", output: "file");

        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.Is<CaptureOptions>(o => o.Format == ImageFormat.Png), It.IsAny<CancellationToken>()), Times.Once);
        TrackPath(result).Should().EndWith(".png");
    }

    [Fact]
    public async Task Screenshot_format_defaults_to_auto()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot();

        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.Is<CaptureOptions>(o => o.Format == ImageFormat.Jpeg), It.IsAny<CancellationToken>()),
            Times.Once, "the default output is inline, so the default format resolves to jpeg");
    }

    [Theory]
    [InlineData("png", "inline", ImageFormat.Png)]
    [InlineData("jpeg", "inline", ImageFormat.Jpeg)]
    [InlineData("png", "file", ImageFormat.Png)]
    [InlineData("jpeg", "file", ImageFormat.Jpeg)]
    [InlineData("png", "base64", ImageFormat.Png)]
    [InlineData("jpeg", "base64", ImageFormat.Jpeg)]
    public async Task Screenshot_explicit_format_is_passed_to_capture(string format, string output, ImageFormat expected)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot(null, format: format, output: output);
        if (output == "file") TrackPath(result);

        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.Is<CaptureOptions>(o => o.Format == expected), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("PNG", ImageFormat.Png)]
    [InlineData("JPEG", ImageFormat.Jpeg)]
    [InlineData("Jpeg", ImageFormat.Jpeg)]
    [InlineData("AUTO", ImageFormat.Jpeg)]
    public async Task Screenshot_format_matching_is_case_insensitive(string format, ImageFormat expected)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot(null, format: format, output: "inline");

        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.Is<CaptureOptions>(o => o.Format == expected), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("jpg")]      // the extension is 'jpg'; the format name is 'jpeg'
    [InlineData("")]
    [InlineData("png ")]
    public async Task Screenshot_unknown_format_throws_naming_the_choices(string format)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        Func<Task> act = () => tools.Screenshot(null, format: format, output: "inline");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("png").And.Contain("jpeg").And.Contain("auto");
        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never, "the format is validated before anything is captured");
    }

    // ---- R7 — inline metadata ---------------------------------------------------------------

    [Fact]
    public async Task Screenshot_inline_metadata_reports_size_format_and_coordinate_space()
    {
        var tools = MakeTools(ShotMock(JpegBytes, 1920, 1080).Object);

        var meta = Meta(await tools.Screenshot(null, format: "jpeg"));

        meta.GetProperty("width").GetInt32().Should().Be(1920);
        meta.GetProperty("height").GetInt32().Should().Be(1080);
        meta.GetProperty("format").GetString().Should().Be("jpeg");
        meta.GetProperty("coordinateSpace").GetString().Should().Be("virtual-desktop");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Screenshot_no_region_captures_the_primary_display_and_reports_it(string? region)
    {
        // A-8 replaces A-7's "region is absent when none was given": the rect is now resolved
        // before the capture (primary by default, roadmap C3) and always reported, so the model
        // can always map an image pixel back to a virtual-desktop coordinate.
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        var meta = Meta(await tools.Screenshot(region, format: "png", output: "inline"));

        ShouldBeRect(Field(meta, "region"), PrimaryRect);
        mock.Verify(s => s.CaptureAsync(PrimaryRect, It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once, "the resolved rect is passed to CaptureAsync — the service never gets a null region again");
    }

    [Fact]
    public async Task Screenshot_metadata_carries_the_region_when_one_was_given()
    {
        var tools = MakeTools(ShotMock(PngBytes, 300, 200).Object);

        var meta = Meta(await tools.Screenshot("10,20,300,200", format: "png", output: "inline"));

        var region = meta.GetProperty("region");
        region.GetProperty("x").GetInt32().Should().Be(10);
        region.GetProperty("y").GetInt32().Should().Be(20);
        region.GetProperty("width").GetInt32().Should().Be(300);
        region.GetProperty("height").GetInt32().Should().Be(200);
    }

    [Fact]
    public async Task Screenshot_file_metadata_carries_the_region_too()
    {
        // Ambiguity resolved: the metadata object has one shape; 'file' adds 'path', a region
        // adds 'region'. Flagged in the A-7 RED report.
        var tools = MakeTools(ShotMock(PngBytes, 300, 200).Object);

        var result = await tools.Screenshot("10,20,300,200", format: "png", output: "file");
        TrackPath(result);

        Meta(result).GetProperty("region").GetProperty("x").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task Screenshot_inline_metadata_does_not_carry_a_path()
    {
        // The tool description advertises "path? (file output)". Nothing else asserted the
        // absence, so an unconditional meta["path"] would have slipped past the whole suite.
        var tools = MakeTools(ShotMock(JpegBytes, 640, 480).Object);

        var meta = Meta(await tools.Screenshot(null, format: "jpeg", output: "inline"));

        meta.TryGetProperty("path", out _).Should().BeFalse("inline output returns bytes, not a file");
    }

    // ---- R8 — region parsing is unchanged ----------------------------------------------------

    [Theory]
    [InlineData("10,20,300,200", 10, 20, 300, 200)]
    [InlineData(" 10 , 20 , 300 , 200 ", 10, 20, 300, 200)]     // TrimEntries
    public async Task Screenshot_passes_the_parsed_region_to_capture(string region, int x, int y, int w, int h)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot(region, format: "png", output: "inline");

        mock.Verify(s => s.CaptureAsync(
            new ScreenRegion(x, y, w, h), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Screenshot_negative_region_coordinates_are_legal_on_a_desktop_that_has_them()
    {
        // Virtual desktop: a monitor left of and above the primary makes negative coordinates
        // legal. A-8 validates the region against the virtual screen, so this row now needs a
        // desktop that actually contains it (it did not before).
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock(LeftOfPrimary).Object);

        await tools.Screenshot("-1920,-40,640,480", format: "png", output: "inline");

        mock.Verify(s => s.CaptureAsync(
            new ScreenRegion(-1920, -40, 640, 480), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("1,2,3")]
    [InlineData("1,2,3,4,5")]
    [InlineData("1")]
    public async Task Screenshot_invalid_region_throws_and_never_captures(string region)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        Func<Task> act = () => tools.Screenshot(region, format: "png", output: "inline");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("region").And.Contain("x,y,w,h");
        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- edge: the reported format follows what the service actually encoded ------------------

    [Fact]
    public async Task Screenshot_mime_and_metadata_follow_the_encoded_result_not_the_request()
    {
        // The service is the authority on what it encoded (today's code reads result.Format for
        // both the extension and the reported format). If a backend ever falls back, the image
        // block must not claim a mime type the bytes do not have.
        var mock = ShotMock(PngBytes, 100, 100, resultFormat: ImageFormat.Png);
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot(null, format: "jpeg", output: "inline");

        ImageBlock(result).MimeType.Should().Be("image/png");
        Meta(result).GetProperty("format").GetString().Should().Be("png");
    }

    // ---- R9 — ocr is unchanged ---------------------------------------------------------------

    [Fact]
    public async Task Ocr_passes_the_region_and_returns_the_service_text()
    {
        var ocr = new Mock<IOcrService>();
        ocr.Setup(s => s.ExtractTextAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("hello world");
        var tools = MakeTools(ocr: ocr.Object);

        var text = await tools.Ocr("5,6,7,8");

        text.Should().Be("hello world");
        ocr.Verify(s => s.ExtractTextAsync(new ScreenRegion(5, 6, 7, 8), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ocr_invalid_region_throws()
    {
        var ocr = new Mock<IOcrService>();
        var tools = MakeTools(ocr: ocr.Object);

        Func<Task> act = () => tools.Ocr("1,2,3");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("x,y,w,h");
        ocr.Verify(s => s.ExtractTextAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    // ======== A-9 — downscale, scale env, coordinate-scale report ===========================

    // ---- R7a — the new arguments reach the service as CaptureOptions -----------------------

    [Fact]
    public async Task Screenshot_defaults_pass_the_1920x1080_cap_scale_1_and_quality_90()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot();

        var opts = CapturedOptions(mock);
        opts.MaxWidth.Should().Be(1920, "the default cap is 1920x1080 (A-9)");
        opts.MaxHeight.Should().Be(1080);
        opts.Scale.Should().Be(1.0);
        opts.Quality.Should().Be(90);
    }

    [Fact]
    public async Task Screenshot_passes_max_width_max_height_and_quality_to_capture()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot(null, format: "jpeg", output: "inline", max_width: 800, max_height: 600, scale: 1.0, quality: 55);

        mock.Verify(s => s.CaptureAsync(
            It.IsAny<ScreenRegion?>(),
            It.Is<CaptureOptions>(o => o.MaxWidth == 800 && o.MaxHeight == 600 && o.Quality == 55
                                       && o.Format == ImageFormat.Jpeg && o.Scale == 1.0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Screenshot_zero_max_dimensions_mean_no_limit_and_are_passed_through()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot(null, format: "png", output: "inline", max_width: 0, max_height: 0);

        var opts = CapturedOptions(mock);
        opts.MaxWidth.Should().Be(0, "0 means 'no limit' and must not be silently replaced by the default");
        opts.MaxHeight.Should().Be(0);
    }

    // ---- R7b — validation, before any capture ----------------------------------------------

    [Theory]
    [InlineData(-1, 1080)]
    [InlineData(1920, -1)]
    [InlineData(-1920, -1080)]
    public async Task Screenshot_negative_max_dimension_throws_and_never_captures(int maxWidth, int maxHeight)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        Func<Task> act = () => tools.Screenshot(null, format: "png", output: "inline", max_width: maxWidth, max_height: maxHeight);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain(maxWidth < 0 ? "max_width" : "max_height",
                "the message must name the offending argument");
        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never, "the caps are validated before anything is captured");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    // The guard is written as a positive test (scale > 0 && scale <= 1) precisely so these two,
    // which fail every comparison, are rejected instead of reaching ScaleMath as a silent NaN
    // that would turn the output size into 1x1.
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Screenshot_scale_outside_the_range_throws_and_never_captures(double scale)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        Func<Task> act = () => tools.Screenshot(null, format: "png", output: "inline", scale: scale);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("scale").And.Contain("1", "the message names the (0, 1] range");
        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Screenshot_quality_outside_1_to_100_throws_and_never_captures(int quality)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        Func<Task> act = () => tools.Screenshot(null, format: "jpeg", output: "inline", quality: quality);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("quality").And.Contain("100", "the message names the 1-100 range");
        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task Screenshot_quality_bounds_are_inclusive(int quality)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot(null, format: "jpeg", output: "inline", quality: quality);

        CapturedOptions(mock).Quality.Should().Be(quality);
    }

    // ---- R7c — the process-level scale multiplies the call's own -----------------------------

    [Fact]
    public async Task Screenshot_effective_scale_multiplies_the_process_scale_by_the_argument()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, options: new ScreenshotOptions(0.5));

        await tools.Screenshot(null, format: "png", output: "inline", scale: 0.5);

        CapturedOptions(mock).Scale.Should().BeApproximately(0.25, 1e-12,
            "WINDOWSMCP_SCREENSHOT_SCALE applies on top of the call's own scale");
    }

    [Fact]
    public async Task Screenshot_process_scale_applies_when_no_scale_argument_is_given()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, options: new ScreenshotOptions(0.4));

        await tools.Screenshot();

        CapturedOptions(mock).Scale.Should().BeApproximately(0.4, 1e-12);
    }

    [Fact]
    public async Task Screenshot_without_process_options_uses_scale_one()
    {
        // ScreenTools(shot, ocr, windows, input, uia) — the options-less form (A-8 added the monitor
        // inventory, A-11 the cursor source, A-6 the element source) — must behave as
        // ScreenshotOptions.Default.
        var mock = ShotMock();
        var tools = new ScreenTools(mock.Object, new Mock<IOcrService>().Object,
            WinMock().Object, InputMock().Object, UiaMock().Object, FlashMock().Object);

        await tools.Screenshot(null, format: "png", output: "inline", scale: 0.5);

        CapturedOptions(mock).Scale.Should().Be(0.5);
    }

    // ---- R7d — metadata ----------------------------------------------------------------------

    [Fact]
    public async Task Screenshot_metadata_always_reports_the_original_size()
    {
        var tools = MakeTools(ShotMock(JpegBytes, 1920, 1080, originalWidth: 3840, originalHeight: 2160,
            coordinateScale: 2.0).Object);

        var meta = Meta(await tools.Screenshot(null, format: "jpeg"));

        meta.GetProperty("originalWidth").GetInt32().Should().Be(3840);
        meta.GetProperty("originalHeight").GetInt32().Should().Be(2160);
        meta.GetProperty("width").GetInt32().Should().Be(1920, "width/height stay the OUTPUT dimensions");
        meta.GetProperty("height").GetInt32().Should().Be(1080);
    }

    [Fact]
    public async Task Screenshot_metadata_reports_the_original_size_even_when_nothing_was_scaled()
    {
        var tools = MakeTools(ShotMock(PngBytes, 640, 480).Object);

        var meta = Meta(await tools.Screenshot(null, format: "png"));

        meta.GetProperty("originalWidth").GetInt32().Should().Be(640, "originalWidth/Height are unconditional");
        meta.GetProperty("originalHeight").GetInt32().Should().Be(480);
    }

    [Fact]
    public async Task Screenshot_metadata_carries_coordinateScale_and_the_multiply_note_when_downscaled()
    {
        var tools = MakeTools(ShotMock(JpegBytes, 1920, 1080, originalWidth: 3840, originalHeight: 2160,
            coordinateScale: 2.0).Object);

        var meta = Meta(await tools.Screenshot(null, format: "jpeg"));

        meta.GetProperty("coordinateScale").ValueKind.Should().Be(JsonValueKind.Number,
            "the scale is a number the model can compute with, not a string");
        meta.GetProperty("coordinateScale").GetDouble().Should().Be(2.0);
        meta.GetProperty("note").GetString().Should()
            .Be("multiply image pixel coordinates by 2 before passing them to click/drag/scroll");
    }

    [Fact]
    public async Task Screenshot_metadata_omits_coordinateScale_and_note_when_nothing_was_scaled()
    {
        var tools = MakeTools(ShotMock(JpegBytes, 640, 480, coordinateScale: 1.0).Object);

        var meta = Meta(await tools.Screenshot(null, format: "jpeg"));

        meta.TryGetProperty("coordinateScale", out _).Should().BeFalse("absent fields are absent, not null");
        meta.TryGetProperty("note", out _).Should().BeFalse("no scaling means nothing to warn about");
    }

    [Fact]
    public async Task Screenshot_note_formats_a_fractional_scale_with_invariant_culture()
    {
        var tools = MakeTools(ShotMock(JpegBytes, 800, 450, originalWidth: 2000, originalHeight: 1125,
            coordinateScale: 2.5).Object);

        var original = CultureInfo.CurrentCulture;
        JsonElement meta;
        try
        {
            // A comma-decimal culture must not produce "2,5" in the sentence the model reads.
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            meta = Meta(await tools.Screenshot(null, format: "jpeg"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        meta.GetProperty("note").GetString().Should()
            .Be("multiply image pixel coordinates by 2.5 before passing them to click/drag/scroll");
        meta.GetProperty("coordinateScale").GetDouble().Should().Be(2.5);
    }

    [Fact]
    public async Task Screenshot_file_output_metadata_carries_the_coordinate_scale_too()
    {
        // One metadata shape for both output modes (the A-7 ambiguity, resolved the same way).
        var tools = MakeTools(ShotMock(PngBytes, 960, 540, originalWidth: 1920, originalHeight: 1080,
            coordinateScale: 2.0).Object);

        var result = await tools.Screenshot(null, format: "png", output: "file");
        TrackPath(result);

        var meta = Meta(result);
        meta.GetProperty("originalWidth").GetInt32().Should().Be(1920);
        meta.GetProperty("coordinateScale").GetDouble().Should().Be(2.0);
        meta.GetProperty("note").GetString().Should().Contain("multiply image pixel coordinates by 2");
    }

    // ======== A-8 — multi-display capture and virtual-desktop coordinates ====================

    /// <summary>The <c>selectedDisplays</c> indices, or null when the field is absent.</summary>
    private static int[]? SelectedDisplays(JsonElement meta)
    {
        if (!meta.TryGetProperty("selectedDisplays", out var value)) return null;
        value.ValueKind.Should().Be(JsonValueKind.Array, "selectedDisplays is a list of indices");
        return value.EnumerateArray().Select(e => e.GetInt32()).ToArray();
    }

    private static void ShouldNeverCapture(Mock<IScreenshotService> mock) =>
        mock.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Never, "the rect is resolved and validated before anything is captured");

    // ---- R3a — the default is the primary display -------------------------------------------

    [Fact]
    public async Task Screenshot_default_captures_the_primary_even_when_it_is_not_the_first_monitor()
    {
        // EnumDisplayMonitors order is not position order and does not put the primary first,
        // so "the default is the primary display" (roadmap C3) cannot be "monitors[0]".
        MonitorInfo[] monitors =
        [
            new(0, "Monitor0", -1920, 0, 1920, 1080, false),
            new(1, "Monitor1", 0, 0, 2560, 1440, true),
        ];
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock(monitors).Object);

        var meta = Meta(await tools.Screenshot(format: "png", output: "inline"));

        mock.Verify(s => s.CaptureAsync(new ScreenRegion(0, 0, 2560, 1440), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        ShouldBeRect(Field(meta, "region"), new ScreenRegion(0, 0, 2560, 1440));
    }

    [Fact]
    public async Task Screenshot_enumerates_the_monitors_exactly_once_per_call()
    {
        // Once: EnumDisplayMonitors + GetMonitorInfo per call is the fixed cost of every
        // screenshot in the agent loop, and two inventories could disagree mid-call.
        var windows = WinMock(SideBySide);
        var tools = MakeTools(ShotMock().Object, windows: windows.Object);

        await tools.Screenshot(display: "all", format: "png", output: "inline");

        windows.Verify(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- R3b — display picks the union of the selected monitors -----------------------------

    [Fact]
    public async Task Screenshot_display_one_captures_the_second_monitor()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock(SideBySide).Object);

        var meta = Meta(await tools.Screenshot(display: "1", format: "png", output: "inline"));

        mock.Verify(s => s.CaptureAsync(new ScreenRegion(1920, 0, 1920, 1080), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once, "display:1 is the second monitor's own rect in virtual-desktop coordinates");
        ShouldBeRect(Field(meta, "region"), new ScreenRegion(1920, 0, 1920, 1080));
        SelectedDisplays(meta).Should().Equal(new[] { 1 });
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData("0,1")]
    [InlineData(" 0 , 1 ")]
    public async Task Screenshot_display_all_captures_the_union_of_every_monitor(string display)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock(SideBySide).Object);

        var meta = Meta(await tools.Screenshot(display: display, format: "png", output: "inline"));

        mock.Verify(s => s.CaptureAsync(new ScreenRegion(0, 0, 3840, 1080), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        SelectedDisplays(meta).Should().Equal(new[] { 0, 1 });
    }

    [Fact]
    public async Task Screenshot_display_all_on_a_negative_origin_desktop_keeps_the_negative_origin()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock(LeftOfPrimary).Object);

        var meta = Meta(await tools.Screenshot(display: "all", format: "png", output: "inline"));

        mock.Verify(s => s.CaptureAsync(new ScreenRegion(-1920, -40, 3840, 1120), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once, "CopyFromScreen takes virtual-desktop coordinates — the union must not be clamped to 0,0");
        ShouldBeRect(Field(meta, "region"), new ScreenRegion(-1920, -40, 3840, 1120));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("0,2")]
    public async Task Screenshot_display_index_outside_the_inventory_throws_and_never_captures(string display)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock(SideBySide).Object);

        Func<Task> act = () => tools.Screenshot(display: display, format: "png", output: "inline");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("0,1", "the message lists the indices this desktop actually has");
        ShouldNeverCapture(mock);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("primary")]
    [InlineData("1.5")]
    [InlineData(",")]
    public async Task Screenshot_unparseable_display_throws_and_never_captures(string display)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock(SideBySide).Object);

        Func<Task> act = () => tools.Screenshot(display: display, format: "png", output: "inline");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .ToLowerInvariant().Should().Contain("display");
        ShouldNeverCapture(mock);
    }

    // ---- R3c — region wins over display -----------------------------------------------------

    [Fact]
    public async Task Screenshot_region_wins_over_display()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock(SideBySide).Object);

        var meta = Meta(await tools.Screenshot("10,20,300,200", display: "1", format: "png", output: "inline"));

        mock.Verify(s => s.CaptureAsync(new ScreenRegion(10, 20, 300, 200), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once, "region wins over display (roadmap C3)");
        SelectedDisplays(meta).Should().BeNull("display did not pick the rect, so reporting it as selected would be a lie");
    }

    [Fact]
    public async Task Screenshot_an_invalid_display_is_still_rejected_when_a_region_is_given()
    {
        // region wins for the CAPTURE, but a display argument the caller got wrong is still a
        // caller error: silently ignoring it teaches the model a wrong index works.
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock(SideBySide).Object);

        Func<Task> act = () => tools.Screenshot("10,20,300,200", display: "9", format: "png", output: "inline");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("0,1");
        ShouldNeverCapture(mock);
    }

    // ---- R3d — the region is validated against the virtual screen ---------------------------

    [Theory]
    [InlineData("-1,0,100,100")]      // one pixel off the left edge
    [InlineData("0,-1,100,100")]      // one pixel off the top
    [InlineData("3740,0,101,100")]    // one pixel past the right edge
    [InlineData("0,980,100,101")]     // one pixel past the bottom
    [InlineData("5000,0,10,10")]      // entirely off the desktop
    public async Task Screenshot_region_outside_the_virtual_screen_throws_with_the_bounds(string region)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock(SideBySide).Object);

        Func<Task> act = () => tools.Screenshot(region, format: "png", output: "inline");

        (await act.Should().ThrowAsync<ArgumentException>("out-of-bounds regions raise, they are not clipped"))
            .Which.Message.Should().Contain("x 0..3839").And.Contain("y 0..1079");
        ShouldNeverCapture(mock);
    }

    [Fact]
    public async Task Screenshot_region_straddling_two_monitors_is_captured()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock(SideBySide).Object);

        await tools.Screenshot("1800,100,240,200", format: "png", output: "inline");

        mock.Verify(s => s.CaptureAsync(new ScreenRegion(1800, 100, 240, 200), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once, "the virtual screen is one space; a rect crossing the seam is legal");
    }

    // ---- R4a — displays is always reported ---------------------------------------------------

    [Fact]
    public async Task Screenshot_metadata_lists_every_display_with_its_bounds()
    {
        var tools = MakeTools(ShotMock().Object, windows: WinMock(SideBySide).Object);

        var meta = Meta(await tools.Screenshot(format: "png", output: "inline"));

        var displays = Field(meta, "displays");
        displays.ValueKind.Should().Be(JsonValueKind.Array);
        displays.GetArrayLength().Should().Be(2, "every monitor is listed, not just the selected ones");

        var first = displays[0];
        first.GetProperty("index").GetInt32().Should().Be(0);
        first.GetProperty("x").GetInt32().Should().Be(0);
        first.GetProperty("y").GetInt32().Should().Be(0);
        first.GetProperty("width").GetInt32().Should().Be(1920);
        first.GetProperty("height").GetInt32().Should().Be(1080);
        first.GetProperty("isPrimary").GetBoolean().Should().BeTrue();

        var second = displays[1];
        second.GetProperty("index").GetInt32().Should().Be(1);
        second.GetProperty("x").GetInt32().Should().Be(1920);
        second.GetProperty("isPrimary").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Screenshot_display_entries_keep_exactly_the_six_fields_A8_defined()
    {
        // B-12 gives MonitorInfo a work area, an orientation, a DPI and a scale. That detail is
        // multi_monitor's; the screenshot metadata deliberately does not grow, because every extra
        // key is paid for on every capture the model takes. This test fails if the tool ever
        // starts serialising the MonitorInfo record instead of projecting these six fields.
        var monitors = new[]
        {
            new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true,
                WorkArea: new Bounds(0, 0, 1920, 1032), Orientation: 90, EffectiveDpi: 144, Scale: 1.5),
        };
        var tools = MakeTools(ShotMock().Object, windows: WinMock(monitors).Object);

        var meta = Meta(await tools.Screenshot(format: "png", output: "inline"));

        Field(meta, "displays")[0].EnumerateObject().Select(p => p.Name)
            .Should().Equal("index", "x", "y", "width", "height", "isPrimary");
    }

    [Fact]
    public async Task Screenshot_file_metadata_lists_the_displays_and_the_region_too()
    {
        // One metadata shape for both output modes (the A-7/A-9 rule).
        var tools = MakeTools(ShotMock().Object, windows: WinMock(SideBySide).Object);

        var result = await tools.Screenshot(display: "1", format: "png", output: "file");
        TrackPath(result);

        var meta = Meta(result);
        Field(meta, "displays").GetArrayLength().Should().Be(2);
        ShouldBeRect(Field(meta, "region"), new ScreenRegion(1920, 0, 1920, 1080));
        SelectedDisplays(meta).Should().Equal(new[] { 1 });
    }

    [Theory]
    [InlineData(null, null)]                    // neither given: the default primary
    [InlineData("10,20,300,200", null)]         // region only
    public async Task Screenshot_metadata_omits_selectedDisplays_unless_display_picked_the_rect(
        string? region, string? display)
    {
        var tools = MakeTools(ShotMock().Object, windows: WinMock(SideBySide).Object);

        var meta = Meta(await tools.Screenshot(region, display: display, format: "png", output: "inline"));

        meta.TryGetProperty("selectedDisplays", out _).Should()
            .BeFalse("absent fields are absent, not null — selectedDisplays means 'display chose this rect'");
    }

    // ---- R4b — CoordinateNote, the pure core of the note sentence ---------------------------

    [Fact]
    public void CoordinateNote_is_null_when_image_pixels_are_already_virtual_desktop_pixels()
    {
        ScreenTools.CoordinateNote(new ScreenRegion(0, 0, 1920, 1080), 1.0).Should()
            .BeNull("origin 0,0 and no downscale means there is nothing for the model to do");
    }

    [Theory]
    [InlineData(2.0, "2")]
    [InlineData(2.5, "2.5")]
    [InlineData(4.0, "4")]
    public void CoordinateNote_for_a_scaled_primary_capture_is_A9s_sentence_verbatim(double scale, string formatted)
    {
        // A-9's exact wording: changing it silently changes what every model reads on every call.
        ScreenTools.CoordinateNote(new ScreenRegion(0, 0, 3840, 2160), scale).Should()
            .Be($"multiply image pixel coordinates by {formatted} before passing them to click/drag/scroll");
    }

    [Theory]
    [InlineData(1920, 0, 1.0, "1")]
    [InlineData(1920, 0, 2.0, "2")]
    [InlineData(-1920, -40, 2.5, "2.5")]
    [InlineData(0, -1080, 1.0, "1")]
    [InlineData(10, 20, 1.0, "1")]
    public void CoordinateNote_off_origin_gives_the_full_transform(int x, int y, double scale, string formatted)
    {
        ScreenTools.CoordinateNote(new ScreenRegion(x, y, 640, 480), scale).Should()
            .Be($"virtual-desktop x = {x} + imageX × {formatted}, y = {y} + imageY × {formatted} " +
                "— use these for click/drag/scroll",
                "an off-origin capture needs the offset as well as the scale, or every click lands on the wrong monitor");
    }

    [Fact]
    public void CoordinateNote_formats_the_scale_with_invariant_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            ScreenTools.CoordinateNote(new ScreenRegion(1920, 0, 640, 480), 2.5).Should()
                .Contain("2.5").And.NotContain("2,5", "a comma-decimal culture must not leak into the model's instructions");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ---- R4c — the note in the metadata ------------------------------------------------------

    [Fact]
    public async Task Screenshot_of_a_second_monitor_reports_the_offset_note_without_a_coordinate_scale()
    {
        var tools = MakeTools(ShotMock(PngBytes, 1920, 1080, coordinateScale: 1.0).Object,
            windows: WinMock(SideBySide).Object);

        var meta = Meta(await tools.Screenshot(display: "1", format: "png", output: "inline"));

        Field(meta, "note").GetString().Should()
            .Be("virtual-desktop x = 1920 + imageX × 1, y = 0 + imageY × 1 — use these for click/drag/scroll");
        meta.TryGetProperty("coordinateScale", out _).Should()
            .BeFalse("nothing was downscaled — the coordinateScale rule is unchanged from A-9");
    }

    [Fact]
    public async Task Screenshot_of_a_scaled_second_monitor_reports_both_the_offset_and_the_scale()
    {
        var tools = MakeTools(
            ShotMock(PngBytes, 960, 540, originalWidth: 1920, originalHeight: 1080, coordinateScale: 2.0).Object,
            windows: WinMock(SideBySide).Object);

        var meta = Meta(await tools.Screenshot(display: "1", format: "png", output: "inline"));

        Field(meta, "note").GetString().Should()
            .Be("virtual-desktop x = 1920 + imageX × 2, y = 0 + imageY × 2 — use these for click/drag/scroll");
        meta.GetProperty("coordinateScale").GetDouble().Should().Be(2.0);
    }

    [Fact]
    public async Task Screenshot_of_the_primary_at_scale_one_still_has_no_note()
    {
        var tools = MakeTools(ShotMock(PngBytes, 1920, 1080, coordinateScale: 1.0).Object);

        var meta = Meta(await tools.Screenshot(format: "png", output: "inline"));

        meta.TryGetProperty("note", out _).Should().BeFalse("the primary at 1:1 needs no coordinate instructions");
    }

    // ---- R5 — ocr resolves the rect exactly the same way -------------------------------------

    private static Mock<IOcrService> OcrMock(string text = "ocr text")
    {
        var mock = new Mock<IOcrService>();
        mock.Setup(s => s.ExtractTextAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(text);
        return mock;
    }

    [Fact]
    public async Task Ocr_without_arguments_reads_the_primary_display()
    {
        var ocr = OcrMock();
        var tools = MakeTools(ocr: ocr.Object, windows: WinMock(SideBySide).Object);

        await tools.Ocr();

        ocr.Verify(s => s.ExtractTextAsync(PrimaryRect, It.IsAny<CancellationToken>()),
            Times.Once, "the resolved rect is passed on — OCR never gets a null region again");
    }

    [Theory]
    [InlineData("1", 1920, 0, 1920, 1080)]
    [InlineData("all", 0, 0, 3840, 1080)]
    [InlineData("0,1", 0, 0, 3840, 1080)]
    public async Task Ocr_display_selects_the_same_rect_screenshot_would(string display, int x, int y, int w, int h)
    {
        var ocr = OcrMock();
        var tools = MakeTools(ocr: ocr.Object, windows: WinMock(SideBySide).Object);

        await tools.Ocr(display: display);

        ocr.Verify(s => s.ExtractTextAsync(new ScreenRegion(x, y, w, h), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ocr_region_wins_over_display()
    {
        var ocr = OcrMock();
        var tools = MakeTools(ocr: ocr.Object, windows: WinMock(SideBySide).Object);

        await tools.Ocr("10,20,300,200", display: "1");

        ocr.Verify(s => s.ExtractTextAsync(new ScreenRegion(10, 20, 300, 200), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ocr_enumerates_the_monitors_exactly_once_per_call()
    {
        var windows = WinMock(SideBySide);
        var tools = MakeTools(ocr: OcrMock().Object, windows: windows.Object);

        await tools.Ocr(display: "1");

        windows.Verify(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ocr_region_outside_the_virtual_screen_throws_and_never_reads()
    {
        var ocr = OcrMock();
        var tools = MakeTools(ocr: ocr.Object, windows: WinMock(SideBySide).Object);

        Func<Task> act = () => tools.Ocr("5000,0,10,10");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("x 0..3839").And.Contain("y 0..1079");
        ocr.Verify(s => s.ExtractTextAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ocr_bad_display_throws_and_never_reads()
    {
        var ocr = OcrMock();
        var tools = MakeTools(ocr: ocr.Object, windows: WinMock(SideBySide).Object);

        Func<Task> act = () => tools.Ocr(display: "7");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("0,1");
        ocr.Verify(s => s.ExtractTextAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("1,2,3")]
    [InlineData("nonsense")]
    [InlineData("0,0,0,100")]
    [InlineData("5000,0,10,10")]
    public async Task Ocr_and_screenshot_report_the_same_error_for_the_same_bad_region(string region)
    {
        // The A-8 point of moving ParseRegion into RegionMath: one parser, so the two tools
        // cannot drift into two different error texts for the same input.
        var tools = MakeTools(ocr: OcrMock().Object, windows: WinMock(SideBySide).Object);

        Func<Task> shot = () => tools.Screenshot(region, format: "png", output: "inline");
        Func<Task> ocr = () => tools.Ocr(region);

        var shotMessage = (await shot.Should().ThrowAsync<ArgumentException>()).Which.Message;
        var ocrMessage = (await ocr.Should().ThrowAsync<ArgumentException>()).Which.Message;
        ocrMessage.Should().Be(shotMessage);
    }
    // ======== A-8 GREEN — gaps the RED pass left open ========================================

    /// <summary>
    /// Three monitors where the middle one sticks out above the others, so a union that wrongly
    /// includes it is a different rectangle: 0+2 spans y -200..1239, 0+1+2 spans y -800..1239.
    /// </summary>
    internal static MonitorInfo[] ThreeAcross =>
    [
        new(0, "Monitor0", 0, 0, 1920, 1080, true),
        new(1, "Monitor1", 1920, -800, 1280, 1024, false),
        new(2, "Monitor2", 3200, -200, 2560, 1440, false),
    ];

    // ---- R3b (GREEN) — a subset selection is the union of exactly those monitors -------------

    [Fact]
    public async Task Screenshot_display_subset_unions_only_the_selected_monitors()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock(ThreeAcross).Object);

        var meta = Meta(await tools.Screenshot(display: "0,2", format: "png", output: "inline"));

        mock.Verify(s => s.CaptureAsync(new ScreenRegion(0, -200, 5760, 1440), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once, "monitor 1 is not selected: including it would start the union at y -800");
        SelectedDisplays(meta).Should().Equal(new[] { 0, 2 });
    }

    [Theory]
    [InlineData("2,0", new[] { 2, 0 })]        // the order given survives into the response
    [InlineData("2,2,0", new[] { 2, 0 })]      // and duplicates are dropped, first occurrence winning
    public async Task Screenshot_selectedDisplays_echoes_the_selection_order_without_duplicates(
        string display, int[] expected)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock(ThreeAcross).Object);

        var meta = Meta(await tools.Screenshot(display: display, format: "png", output: "inline"));

        SelectedDisplays(meta).Should().Equal(expected,
            "selectedDisplays is what the caller asked for, not a re-sorted list");
        mock.Verify(s => s.CaptureAsync(new ScreenRegion(0, -200, 5760, 1440), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once, "the union is a bounding box, so the order does not change the rect");
    }

    // ---- R3e (GREEN) — one inventory per call, on every resolution path ----------------------

    [Theory]
    [InlineData(null, null)]                      // default -> Primary
    [InlineData("10,20,300,200", null)]           // region -> VirtualScreen + Validate
    [InlineData(null, "1")]                       // display -> Union
    [InlineData("10,20,300,200", "1")]            // both -> region wins, display still parsed
    public async Task Screenshot_enumerates_the_monitors_once_on_every_resolution_path(string? region, string? display)
    {
        // The region path needs the inventory twice over (validate against the virtual screen,
        // then report every display); enumerating twice would double the per-screenshot Win32
        // cost and could resolve the rect against an inventory the response does not describe.
        var windows = WinMock(SideBySide);
        var tools = MakeTools(ShotMock().Object, windows: windows.Object);

        await tools.Screenshot(region, display: display, format: "png", output: "inline");

        windows.Verify(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- R3f (GREEN) — the inventory collaborator returns nothing, or throws ------------------

    [Theory]
    [InlineData(null, null)]                      // Primary of nothing
    [InlineData("10,20,300,200", null)]           // VirtualScreen of nothing
    [InlineData(null, "all")]                     // 'all' of nothing is an empty selection -> Union of nothing
    [InlineData(null, "0")]                       // there is no monitor 0
    public async Task Screenshot_empty_monitor_inventory_throws_and_never_captures(string? region, string? display)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: WinMock([]).Object);

        Func<Task> act = () => tools.Screenshot(region, display: display, format: "png", output: "inline");

        (await act.Should().ThrowAsync<ArgumentException>(
            "an empty inventory must be a caller-facing error, not an IndexOutOfRange or a 0x0 capture"))
            .Which.Message.ToLowerInvariant().Should().Contain("monitor");
        ShouldNeverCapture(mock);
    }

    [Fact]
    public async Task Ocr_empty_monitor_inventory_throws_and_never_reads()
    {
        var ocr = OcrMock();
        var tools = MakeTools(ocr: ocr.Object, windows: WinMock([]).Object);

        Func<Task> act = () => tools.Ocr();

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .ToLowerInvariant().Should().Contain("monitor");
        ocr.Verify(s => s.ExtractTextAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Screenshot_monitor_enumeration_failure_is_not_swallowed()
    {
        // If the inventory cannot be read the rect cannot be resolved: falling back to "the
        // primary at 0,0" would capture the wrong pixels and report coordinates that are wrong.
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("EnumDisplayMonitors failed"));
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, windows: windows.Object);

        Func<Task> act = () => tools.Screenshot(format: "png", output: "inline");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("EnumDisplayMonitors failed");
        ShouldNeverCapture(mock);
    }

    // ---- R2 (GREEN) — the descriptions are the only spec the model reads ---------------------

    private static string ParameterDescription(string method, string parameter)
    {
        var info = typeof(ScreenTools).GetMethod(method)!.GetParameters().Single(p => p.Name == parameter);
        var description = info.GetCustomAttribute<DescriptionAttribute>();
        description.Should().NotBeNull($"'{parameter}' of {method} must carry a [Description] the model can read");
        return description!.Description;
    }

    [Theory]
    [InlineData("region")]
    [InlineData("display")]
    public void Screenshot_and_ocr_advertise_the_same_text_for_the_shared_arguments(string parameter)
    {
        // A-8 made both tools resolve the rect through one resolver; two descriptions that drift
        // would tell the model the tools accept different things when they do not.
        ParameterDescription(nameof(ScreenTools.Ocr), parameter).Should()
            .Be(ParameterDescription(nameof(ScreenTools.Screenshot), parameter));
    }

    [Fact]
    public void Display_description_states_the_syntax_the_default_and_that_region_wins()
    {
        var text = ParameterDescription(nameof(ScreenTools.Screenshot), "display");

        text.Should().Contain("'all'", "the model cannot guess the keyword")
            .And.Contain("multi_monitor", "the indices are the ones that tool reports")
            .And.Contain("union", "several displays are captured as one rect")
            .And.Contain("primary", "the default is the primary display (roadmap C3)")
            .And.Contain("region", "'region' wins over 'display' — and a bad display still errors");
    }

    [Fact]
    public void Region_description_states_the_coordinate_space_and_that_it_is_rejected_not_clipped()
    {
        var text = ParameterDescription(nameof(ScreenTools.Screenshot), "region");

        text.Should().Contain("x,y,w,h")
            .And.Contain("virtual-desktop", "the same space click/drag/scroll use (roadmap C1)")
            .And.Contain("negative", "a monitor left of or above the primary has negative coordinates")
            .And.Contain("rejected", "out-of-bounds regions raise, they are not clipped");
    }

    // ---- A-11 (R5) — the cursor in the metadata and the include_cursor argument ---------------

    private static void ShouldNeverReadTheCursor(Mock<IInputService> mock) =>
        mock.Verify(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()),
            Times.Never, "a call that never happens must not cost a cursor read either");

    /// <summary>The <c>cursor</c> object the metadata always carries.</summary>
    private static (int X, int Y, int MonitorIndex) Cursor(JsonElement meta)
    {
        var cursor = Field(meta, "cursor");
        cursor.ValueKind.Should().Be(JsonValueKind.Object, "cursor is an object {x, y, monitorIndex}");
        return (Field(cursor, "x").GetInt32(), Field(cursor, "y").GetInt32(),
                Field(cursor, "monitorIndex").GetInt32());
    }

    [Fact]
    public async Task Screenshot_metadata_always_carries_the_cursor()
    {
        var tools = MakeTools(input: InputMock(640, 480).Object);

        var result = await tools.Screenshot(format: "png", output: "inline");

        Cursor(Meta(result)).Should().Be((640, 480, 0),
            "the position comes from IInputService and the index from the same inventory the rect did");
    }

    [Fact]
    public async Task Screenshot_cursor_monitor_index_comes_from_the_capture_inventory()
    {
        // The whole point of reporting an index: (2000,10) is on the SECOND monitor of this desktop.
        var tools = MakeTools(windows: WinMock(SideBySide).Object, input: InputMock(2000, 10).Object);

        var result = await tools.Screenshot(format: "png", output: "inline");

        Cursor(Meta(result)).Should().Be((2000, 10, 1));
    }

    [Fact]
    public async Task Screenshot_cursor_off_every_monitor_reports_index_minus_one()
    {
        var tools = MakeTools(input: InputMock(10_000, 10_000).Object);

        var result = await tools.Screenshot(format: "png", output: "inline");

        Cursor(Meta(result)).Should().Be((10_000, 10_000, -1),
            "the position is still reported; -1 says it is on no monitor");
    }

    [Fact]
    public async Task Screenshot_metadata_carries_the_cursor_even_when_it_is_not_drawn()
    {
        // "Always" means always: include_cursor only decides what is PAINTED, never what is reported.
        var tools = MakeTools(input: InputMock(300, 200).Object);

        var result = await tools.Screenshot(format: "png", output: "inline", include_cursor: false);

        Cursor(Meta(result)).Should().Be((300, 200, 0));
    }

    [Fact]
    public async Task Screenshot_forwards_the_cursor_it_read_so_the_service_draws_at_the_reported_point()
    {
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPosition(321, 45));
        var shot = ShotMock();
        var tools = MakeTools(shot.Object, input: input.Object);

        await tools.Screenshot();

        CapturedOptions(shot).Cursor.Should().Be(new CursorPosition(321, 45),
            "the metadata's cursor and the painted mark must be the same read, not two reads that can disagree");
    }

    [Fact]
    public async Task Screenshot_reads_the_cursor_exactly_once_per_call()
    {
        var input = InputMock();
        var tools = MakeTools(input: input.Object);

        await tools.Screenshot(format: "png", output: "inline");

        input.Verify(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()), Times.Once,
            "one read per screenshot — a second read could report a cursor that moved after the capture");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Screenshot_passes_include_cursor_to_the_capture_options(bool includeCursor)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot(format: "png", output: "inline", include_cursor: includeCursor);

        CapturedOptions(mock).IncludeCursor.Should().Be(includeCursor);
    }

    [Fact]
    public async Task Screenshot_include_cursor_defaults_to_true()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot(format: "png", output: "inline");

        CapturedOptions(mock).IncludeCursor.Should().BeTrue(
            "the default agent-loop call shows the model where the pointer is");
    }

    [Theory]
    [InlineData("icon")]
    [InlineData("ring")]
    public async Task Screenshot_metadata_reports_how_the_cursor_was_drawn(string drawn)
    {
        var mock = ShotMock(cursorDrawn: drawn);
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot(format: "png", output: "inline");

        Field(Meta(result), "cursorDrawn").GetString().Should().Be(drawn,
            "the metadata reports what the service actually painted");
    }

    [Fact]
    public async Task Screenshot_metadata_omits_cursorDrawn_when_nothing_was_drawn()
    {
        var mock = ShotMock(cursorDrawn: null);
        var tools = MakeTools(mock.Object);

        var result = await tools.Screenshot(format: "png", output: "inline");

        Meta(result).TryGetProperty("cursorDrawn", out var value).Should().BeFalse(
            "absent, never null — a field that does not apply is omitted (A-7). Got {0}", value);
    }

    [Fact]
    public async Task Screenshot_file_output_carries_the_cursor_and_cursorDrawn_too()
    {
        var mock = ShotMock(cursorDrawn: "icon");
        var tools = MakeTools(mock.Object, input: InputMock(2000, 10).Object,
            windows: WinMock(SideBySide).Object);

        var result = await tools.Screenshot(format: "png", output: "file");
        TrackPath(result);

        var meta = Meta(result);
        Cursor(meta).Should().Be((2000, 10, 1), "the file mode's metadata has the same shape as inline's");
        Field(meta, "cursorDrawn").GetString().Should().Be("icon");
    }

    [Fact]
    public async Task Screenshot_reads_the_cursor_before_it_captures()
    {
        // Order matters: the read is an argument-free operation that can fail (a broken desktop),
        // and failing it after a capture would burn the capture for nothing.
        var order = new List<string>();
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { order.Add("cursor"); return new CursorPosition(1, 2); });
        var shot = new Mock<IScreenshotService>();
        shot.Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                order.Add("capture");
                return new ScreenshotResult(PngBytes, 100, 100, ImageFormat.Png, 100, 100, 1.0);
            });
        var tools = MakeTools(shot.Object, input: input.Object);

        await tools.Screenshot(format: "png", output: "inline");

        order.Should().Equal("cursor", "capture");
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    public async Task Screenshot_invalid_output_throws_without_reading_the_cursor(string output)
    {
        var mock = ShotMock();
        var input = InputMock();
        var tools = MakeTools(mock.Object, input: input.Object);

        Func<Task> act = () => tools.Screenshot(output: output);

        await act.Should().ThrowAsync<ArgumentException>();
        ShouldNeverReadTheCursor(input);
        ShouldNeverCapture(mock);
    }

    [Fact]
    public async Task Screenshot_cursor_read_failure_propagates_and_never_captures()
    {
        // A cursor that cannot be read is a broken desktop, not a detail to paper over: the caller
        // sees the failure rather than a picture whose metadata invents a position.
        var mock = ShotMock();
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("GetCursorPos failed (Win32 error 5)."));
        var tools = MakeTools(mock.Object, input: input.Object);

        Func<Task> act = () => tools.Screenshot(format: "png", output: "inline");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("GetCursorPos failed");
        ShouldNeverCapture(mock);
    }

    [Fact]
    public async Task Ocr_never_reads_the_cursor()
    {
        // OCR returns text; a cursor position in it would be noise, and the capture it makes must
        // stay cursor-free (OcrServiceTests pins the IncludeCursor:false half).
        var input = InputMock();
        var tools = MakeTools(ocr: OcrMock().Object, input: input.Object);

        await tools.Ocr();

        ShouldNeverReadTheCursor(input);
    }

    [Fact]
    public void Screenshot_include_cursor_is_appended_after_the_A9_arguments_and_defaults_to_true()
    {
        // Appended, not inserted: every existing caller and test passes the earlier arguments
        // positionally, and the schema default is what the model reads. A-11 put include_cursor
        // last; A-6 appended annotate/grid_columns/grid_rows AFTER it, so it is no longer the
        // final parameter — but it is still after everything A-9 added, which is what "appended"
        // protects. Screenshot_annotate_arguments_are_appended_after_include_cursor pins the tail.
        var parameters = typeof(ScreenTools).GetMethod(nameof(ScreenTools.Screenshot))!.GetParameters();

        var includeCursor = parameters.Single(p => p.Name == "include_cursor");
        includeCursor.DefaultValue.Should().Be(true);
        Array.IndexOf(parameters, includeCursor).Should()
            .BeGreaterThan(Array.IndexOf(parameters, parameters.Single(p => p.Name == "quality")),
                "include_cursor came after every argument that existed before A-11");
    }

    [Fact]
    public void Screenshot_description_documents_the_cursor_metadata_and_the_new_argument()
    {
        var description = typeof(ScreenTools).GetMethod(nameof(ScreenTools.Screenshot))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("cursor", "the metadata list is the only place the model learns the field exists")
            .And.Contain("monitorIndex", "the cursor object's shape is part of the contract")
            .And.Contain("cursorDrawn", "and that it is present only when something was drawn");

        ParameterDescription(nameof(ScreenTools.Screenshot), "include_cursor").Should()
            .Contain("cursor").And.Contain("default", "the model must know it is on by default");
    }

    // ---- A-11 (GREEN) — the gaps the implementation opened ----------------------------------

    [Fact]
    public async Task Screenshot_cursor_is_reported_in_virtual_desktop_pixels_not_region_relative()
    {
        // Roadmap C1: everything the response carries is virtual-desktop, and 'cursor' is what the
        // model feeds straight back to click/drag. Rebasing it onto the captured rect's origin
        // (which is what the DRAWN mark is rebased onto) would send every click 1920 px left.
        var tools = MakeTools(windows: WinMock(SideBySide).Object, input: InputMock(2000, 10).Object);

        var result = await tools.Screenshot("1920,0,100,100", format: "png", output: "inline");

        var meta = Meta(result);
        Cursor(meta).Should().Be((2000, 10, 1));
        // The region IS the rebased rect the image starts at — the contrast is the point.
        ShouldBeRect(Field(meta, "region"), new ScreenRegion(1920, 0, 100, 100));
    }

    [Fact]
    public async Task Screenshot_invalid_region_throws_without_reading_the_cursor()
    {
        // The read sits after the rect is resolved AND validated: a region off the virtual screen
        // must cost neither a capture nor a Win32 cursor call.
        var mock = ShotMock();
        var input = InputMock();
        var tools = MakeTools(mock.Object, input: input.Object);

        Func<Task> act = () => tools.Screenshot("5000,0,100,100", format: "png", output: "inline");

        await act.Should().ThrowAsync<ArgumentException>();
        ShouldNeverReadTheCursor(input);
        ShouldNeverCapture(mock);
    }

    // ---- A-6 (R4) — annotate: one snapshot, the boxes, the text block, the grid ---------------

    /// <summary>An interactive element at <paramref name="bounds"/>; the centre is the rect's centre.</summary>
    private static SnapshotElement Element(string id, Bounds bounds, string name = "Save") =>
        new(id, "Untitled - Notepad", "Button", name,
            bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, bounds,
            "click", false, false, null, null, null, null, null, null, null);

    private static SnapshotScrollable Scrollable(string id, Bounds bounds) =>
        new(id, "Untitled - Notepad", "Document", "Text Editor",
            bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, bounds,
            new ScrollInfo(37, 0, true, false));

    private static SnapshotResult SnapshotOf(
        SnapshotElement[] interactive, SnapshotScrollable[]? scrollable = null) =>
        new([], null, new CursorPosition(0, 0), -1, interactive, scrollable ?? [], null,
            false, 500, interactive.Length, 0);

    /// <summary>The metadata block of a result that may carry more than one text block (annotate does).</summary>
    private static JsonElement MetaBlock(CallToolResult result)
    {
        var block = result.Content[0].Should().BeOfType<TextContentBlock>(
            "the metadata block is always first").Subject;
        using var doc = JsonDocument.Parse(block.Text);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        return doc.RootElement.Clone();
    }

    /// <summary>The A-6 element-list block: the second block, the same list the labels came from.</summary>
    private static string SnapshotText(CallToolResult result) =>
        result.Content[1].Should().BeOfType<TextContentBlock>(
            "the snapshot text is the second block, between the metadata and the image").Subject.Text;

    private static void ShouldNeverSnapshot(Mock<IUIAutomationService> mock) =>
        mock.Verify(u => u.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()),
            Times.Never, "a capture that was not asked to annotate must not cost a desktop walk");

    // -- the boxes ---------------------------------------------------------------------------

    [Fact]
    public async Task Screenshot_annotate_boxes_only_the_elements_that_intersect_the_captured_rect()
    {
        // A box for an element that is not in the picture would be drawn nowhere (or, worse,
        // clamped onto an unrelated pixel) and would still consume a label the text block lists.
        var inside = Element("el_1", new Bounds(100, 100, 50, 20));
        var outside = Element("el_2", new Bounds(2000, 100, 50, 20));
        var straddling = Element("el_3", new Bounds(1900, 100, 50, 20));
        var shot = ShotMock();
        var tools = MakeTools(shot.Object, uia: UiaMock(SnapshotOf([inside, outside, straddling])).Object);

        await tools.Screenshot(format: "png", output: "inline", annotate: true);

        CapturedOptions(shot).Annotations.Should().BeEquivalentTo(
            new[] { new AnnotationBox("el_1", inside.Bounds), new AnnotationBox("el_3", straddling.Bounds) },
            o => o.WithStrictOrdering(),
            "the kept elements keep the snapshot's order, and the label is the id click accepts");
    }

    [Theory]
    [InlineData(100, true)]      // wholly inside the primary rect
    [InlineData(1900, true)]     // straddles the right edge
    [InlineData(1920, false)]    // starts exactly where the rect ends: no overlap at all
    [InlineData(-20, true)]      // straddles the left edge
    [InlineData(-60, false)]     // ends exactly where the rect starts: no overlap at all
    public async Task Screenshot_annotate_keeps_an_element_exactly_when_it_overlaps_the_rect(int x, bool kept)
    {
        var element = Element("el_1", new Bounds(x, 100, 50, 20));
        var shot = ShotMock();
        var uia = UiaMock(SnapshotOf([element]));
        var tools = MakeTools(shot.Object, uia: uia.Object);

        var result = await tools.Screenshot(format: "png", output: "inline", annotate: true);

        // The walk happens either way — annotate always produces the element list block, even when
        // nothing in it is inside the picture. Without this the "not kept" rows would pass against
        // a tool that ignored 'annotate' altogether.
        uia.Verify(u => u.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        result.Content.Should().HaveCount(3);
        if (kept)
            CapturedOptions(shot).Annotations.Should().ContainSingle().Which.Label.Should().Be("el_1");
        else
            CapturedOptions(shot).Annotations.Should().BeNull(
                "nothing was kept, so nothing is drawn — an empty list would still cost a draw pass");
    }

    [Fact]
    public async Task Screenshot_annotate_with_no_elements_at_all_passes_no_annotations()
    {
        var shot = ShotMock();
        var tools = MakeTools(shot.Object, uia: UiaMock().Object);   // the empty snapshot

        var result = await tools.Screenshot(format: "png", output: "inline", annotate: true);

        CapturedOptions(shot).Annotations.Should().BeNull();
        result.Content.Should().HaveCount(3, "the text block is still there, saying the list is empty");
    }

    // -- the text block ----------------------------------------------------------------------

    [Fact]
    public async Task Screenshot_annotate_returns_metadata_then_the_element_list_then_the_image()
    {
        var shot = ShotMock();
        var tools = MakeTools(shot.Object,
            uia: UiaMock(SnapshotOf([Element("el_1", new Bounds(100, 100, 50, 20))])).Object);

        var result = await tools.Screenshot(format: "png", output: "inline", annotate: true);

        result.Content.Should().HaveCount(3);
        result.Content[0].Should().BeOfType<TextContentBlock>("metadata first");
        result.Content[1].Should().BeOfType<TextContentBlock>("then the element list the labels index");
        result.Content[2].Should().BeOfType<ImageContentBlock>("then the annotated picture");
    }

    [Fact]
    public async Task Screenshot_annotate_text_block_is_exactly_the_rendered_filtered_snapshot()
    {
        // "Label N in the image is row N in the text from the SAME call" (roadmap A-6) only holds
        // if the text is rendered from the same filtered list the boxes were built from.
        var inside = Element("el_1", new Bounds(100, 100, 50, 20));
        var outside = Element("el_2", new Bounds(2000, 100, 50, 20));
        var scrollIn = Scrollable("el_20", new Bounds(0, 0, 800, 600));
        var scrollOut = Scrollable("el_21", new Bounds(3000, 0, 800, 600));
        var snapshot = SnapshotOf([inside, outside], [scrollIn, scrollOut]);
        var tools = MakeTools(uia: UiaMock(snapshot).Object);

        var result = await tools.Screenshot(format: "png", output: "inline", annotate: true);

        var expected = SnapshotRenderer.Render(snapshot with { Interactive = [inside], Scrollable = [scrollIn] });
        SnapshotText(result).Should().Be(expected,
            "the model reads one list; anything the picture does not show must not be in it");
    }

    [Fact]
    public async Task Screenshot_annotate_text_block_drops_the_scrollables_outside_the_rect()
    {
        var snapshot = SnapshotOf(
            [Element("el_1", new Bounds(100, 100, 50, 20))],
            [Scrollable("el_20", new Bounds(0, 0, 800, 600)), Scrollable("el_21", new Bounds(3000, 0, 800, 600))]);
        var tools = MakeTools(uia: UiaMock(snapshot).Object);

        var result = await tools.Screenshot(format: "png", output: "inline", annotate: true);

        var text = SnapshotText(result);
        text.Should().Contain("Scrollable (1)").And.Contain("el_20");
        text.Should().NotContain("el_21", "that region is not in the picture");
    }

    // -- the call itself ---------------------------------------------------------------------

    [Fact]
    public async Task Screenshot_annotate_takes_exactly_one_desktop_snapshot()
    {
        var uia = UiaMock();
        var tools = MakeTools(uia: uia.Object);

        await tools.Screenshot(format: "png", output: "inline", annotate: true);

        uia.Verify(u => u.SnapshotAsync(
            It.Is<SnapshotRequest>(r => r.Scope == SnapshotScope.Desktop && r.WindowTitle == null
                                        && !r.IncludeTree && r.MaxElements == 0),
            It.IsAny<CancellationToken>()),
            Times.Once, "one walk per screenshot, over the whole desktop the capture can contain");
    }

    [Fact]
    public async Task Screenshot_annotate_snapshots_after_the_rect_is_resolved_and_before_the_capture()
    {
        // The filter needs the rect, so the inventory comes first; and the picture must be of the
        // desktop the element list describes, so the walk must not run after the shutter.
        var order = new List<string>();
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { order.Add("monitors"); return SingleMonitor; });
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(u => u.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { order.Add("snapshot"); return EmptySnapshot; });
        var shot = new Mock<IScreenshotService>();
        shot.Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                order.Add("capture");
                return new ScreenshotResult(PngBytes, 100, 100, ImageFormat.Png, 100, 100, 1.0);
            });
        var tools = MakeTools(shot.Object, windows: windows.Object, uia: uia.Object);

        await tools.Screenshot(format: "png", output: "inline", annotate: true);

        order.Should().ContainInOrder("monitors", "snapshot", "capture");
    }

    [Fact]
    public async Task Screenshot_without_annotate_never_walks_the_desktop()
    {
        var uia = UiaMock(SnapshotOf([Element("el_1", new Bounds(100, 100, 50, 20))]));
        var shot = ShotMock();
        var tools = MakeTools(shot.Object, uia: uia.Object);

        var result = await tools.Screenshot(format: "png", output: "inline");

        ShouldNeverSnapshot(uia);
        result.Content.Should().HaveCount(2, "A-7's two blocks, unchanged");
        CapturedOptions(shot).Annotations.Should().BeNull();
    }

    // -- the grid ----------------------------------------------------------------------------

    [Theory]
    [InlineData(3, 0)]
    [InlineData(0, 2)]
    [InlineData(4, 4)]
    public async Task Screenshot_grid_arguments_reach_the_capture_as_a_GridSpec(int columns, int rows)
    {
        var shot = ShotMock();
        var tools = MakeTools(shot.Object);

        await tools.Screenshot(format: "png", output: "inline", grid_columns: columns, grid_rows: rows);

        CapturedOptions(shot).Grid.Should().Be(new GridSpec(columns, rows));
    }

    [Fact]
    public async Task Screenshot_without_a_grid_passes_no_grid()
    {
        var shot = ShotMock();
        var tools = MakeTools(shot.Object);

        await tools.Screenshot(format: "png", output: "inline");

        CapturedOptions(shot).Grid.Should().BeNull("absent, not a zero-by-zero grid to draw nothing with");
    }

    [Fact]
    public async Task Screenshot_grid_without_annotate_needs_no_snapshot()
    {
        // A grid is pure geometry over the captured rect: it costs a draw, never a desktop walk.
        var uia = UiaMock();
        var shot = ShotMock();
        var tools = MakeTools(shot.Object, uia: uia.Object);

        var result = await tools.Screenshot(format: "png", output: "inline", grid_columns: 3, grid_rows: 3);

        ShouldNeverSnapshot(uia);
        result.Content.Should().HaveCount(2, "no annotate means no element list");
        CapturedOptions(shot).Grid.Should().Be(new GridSpec(3, 3));
    }

    [Theory]
    [InlineData(65, 0, "grid_columns")]
    [InlineData(0, 65, "grid_rows")]
    public async Task Screenshot_grid_above_sixty_four_is_rejected_naming_the_argument(int columns, int rows, string argument)
    {
        // Unbounded, a large value draws a line every pixel plus a caption per line: unreadable and slow.
        var shot = ShotMock();
        var tools = MakeTools(shot.Object);

        Func<Task> act = () => tools.Screenshot(format: "png", output: "inline", grid_columns: columns, grid_rows: rows);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain(argument).And.Contain("64");
        shot.Verify(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Screenshot_grid_of_sixty_four_is_accepted()
    {
        var shot = ShotMock();
        var tools = MakeTools(shot.Object);

        await tools.Screenshot(format: "png", output: "inline", grid_columns: 64, grid_rows: 64);

        CapturedOptions(shot).Grid.Should().Be(new GridSpec(64, 64));
    }

    [Theory]
    [InlineData(-1, 0, "grid_columns")]
    [InlineData(0, -1, "grid_rows")]
    [InlineData(-2, -2, "grid_columns")]
    public async Task Screenshot_negative_grid_throws_naming_the_argument_and_does_nothing(
        int columns, int rows, string named)
    {
        var shot = ShotMock();
        var uia = UiaMock();
        var input = InputMock();
        var windows = WinMock();
        var tools = MakeTools(shot.Object, windows: windows.Object, input: input.Object, uia: uia.Object);

        Func<Task> act = () => tools.Screenshot(
            format: "png", output: "inline", grid_columns: columns, grid_rows: rows);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain(named);
        ShouldNeverSnapshot(uia);
        ShouldNeverCapture(shot);
        ShouldNeverReadTheCursor(input);
        windows.Verify(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()),
            Times.Never, "arguments are validated before any work, as every other A-8/A-9 rule is");
    }

    [Fact]
    public async Task Screenshot_zero_grid_is_legal_and_means_no_grid()
    {
        var shot = ShotMock();
        var tools = MakeTools(shot.Object);

        await tools.Screenshot(format: "png", output: "inline", grid_columns: 0, grid_rows: 0);

        CapturedOptions(shot).Grid.Should().BeNull();
    }

    // -- the metadata ------------------------------------------------------------------------

    [Fact]
    public async Task Screenshot_annotate_metadata_says_so_and_echoes_what_was_drawn()
    {
        var shot = ShotMock(annotationsDrawn: 7);
        var tools = MakeTools(shot.Object,
            uia: UiaMock(SnapshotOf([Element("el_1", new Bounds(100, 100, 50, 20))])).Object);

        var result = await tools.Screenshot(format: "png", output: "inline", annotate: true);

        var meta = MetaBlock(result);
        Field(meta, "annotated").GetBoolean().Should().BeTrue();
        Field(meta, "annotations").GetInt32().Should().Be(7,
            "the count is what the SERVICE drew, not how many boxes were asked for");
    }

    [Fact]
    public async Task Screenshot_metadata_omits_the_annotate_fields_when_not_annotating()
    {
        var result = await MakeTools().Screenshot(format: "png", output: "inline");

        var meta = Meta(result);
        meta.TryGetProperty("annotated", out _).Should().BeFalse("absent, never false (A-7's rule)");
        meta.TryGetProperty("annotations", out _).Should().BeFalse();
        meta.TryGetProperty("grid", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(0, 2)]
    [InlineData(4, 4)]
    public async Task Screenshot_metadata_reports_the_grid_that_was_requested(int columns, int rows)
    {
        var tools = MakeTools();

        var result = await tools.Screenshot(
            format: "png", output: "inline", grid_columns: columns, grid_rows: rows);

        var grid = Field(Meta(result), "grid");
        Field(grid, "columns").GetInt32().Should().Be(columns);
        Field(grid, "rows").GetInt32().Should().Be(rows);
    }

    [Fact]
    public async Task Screenshot_metadata_omits_the_grid_when_none_was_requested()
    {
        var result = await MakeTools().Screenshot(format: "png", output: "inline", annotate: true);

        var meta = MetaBlock(result);
        Field(meta, "annotated").GetBoolean().Should().BeTrue("this IS the annotate path");
        meta.TryGetProperty("grid", out _).Should().BeFalse("annotate does not imply a grid");
    }

    // -- file output -------------------------------------------------------------------------

    [Fact]
    public async Task Screenshot_annotate_to_file_writes_the_annotated_bytes_and_still_lists_the_elements()
    {
        byte[] annotated = [0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03, 0x04];
        var shot = ShotMock(annotated, annotationsDrawn: 1);
        var element = Element("el_1", new Bounds(100, 100, 50, 20));
        var snapshot = SnapshotOf([element]);
        var tools = MakeTools(shot.Object, uia: UiaMock(snapshot).Object);

        var result = await tools.Screenshot(format: "png", output: "file", annotate: true);
        var path = TrackPath(result);

        result.Content.Should().HaveCount(2, "file output has no image block, but still has the element list");
        result.Content.OfType<ImageContentBlock>().Should().BeEmpty();
        SnapshotText(result).Should().Be(SnapshotRenderer.Render(snapshot));
        (await File.ReadAllBytesAsync(path)).Should().Equal(annotated,
            "the file holds the picture the boxes were drawn on");
        Field(MetaBlock(result), "annotations").GetInt32().Should().Be(1);
    }

    /// <summary>The metadata block of an annotated FILE result — the same first block, past the second.</summary>
    [Fact]
    public async Task Screenshot_annotate_to_file_metadata_keeps_the_path_and_the_annotate_fields()
    {
        var shot = ShotMock(annotationsDrawn: 2);
        var tools = MakeTools(shot.Object,
            uia: UiaMock(SnapshotOf([Element("el_1", new Bounds(100, 100, 50, 20))])).Object);

        var result = await tools.Screenshot(format: "png", output: "file", annotate: true, grid_columns: 2);
        var path = TrackPath(result);

        var meta = MetaBlock(result);
        Field(meta, "path").GetString().Should().Be(path);
        Field(meta, "annotated").GetBoolean().Should().BeTrue();
        Field(meta, "annotations").GetInt32().Should().Be(2);
        Field(Field(meta, "grid"), "columns").GetInt32().Should().Be(2);
    }

    // -- the shape of the signature and what the model is told ---------------------------------

    [Fact]
    public void Screenshot_annotate_arguments_are_appended_after_include_cursor()
    {
        // Appended, not inserted: every existing caller passes the earlier arguments positionally.
        // A-10 appended 'backend' after grid_rows, so the annotate block is no longer the tail —
        // Screenshot_backend_is_the_last_argument_and_defaults_to_auto pins that. What this test
        // protects is the block's own order, and that it still follows include_cursor.
        var parameters = typeof(ScreenTools).GetMethod(nameof(ScreenTools.Screenshot))!.GetParameters();
        var names = parameters.Select(p => p.Name).ToArray();
        var first = Array.IndexOf(names, "annotate");

        names[first - 1].Should().Be("include_cursor");
        first.Should().BeGreaterThan(0);
        parameters[first].DefaultValue.Should().Be(false, "annotate is opt-in: it costs a desktop walk");
        names[first + 1].Should().Be("grid_columns");
        parameters[first + 1].DefaultValue.Should().Be(0);
        names[first + 2].Should().Be("grid_rows");
        parameters[first + 2].DefaultValue.Should().Be(0);
    }

    [Fact]
    public void Screenshot_description_documents_annotate_the_grid_and_the_shared_labels()
    {
        var description = typeof(ScreenTools).GetMethod(nameof(ScreenTools.Screenshot))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("annotate", "the model cannot use an argument the description never mentions")
            .And.Contain("el_", "the labels in the picture are the snapshot ids click/interact_element accept")
            .And.Contain("same call", "label N is row N of THIS call's list — ids expire on the next snapshot");

        ParameterDescription(nameof(ScreenTools.Screenshot), "annotate").Should()
            .Contain("box").And.Contain("default", "opt-in, and the model must know what it costs");
        ParameterDescription(nameof(ScreenTools.Screenshot), "grid_columns").Should()
            .Contain("0").And.Contain("column");
        ParameterDescription(nameof(ScreenTools.Screenshot), "grid_rows").Should()
            .Contain("0").And.Contain("row");
    }

    [Fact]
    public async Task Screenshot_annotate_filters_against_the_requested_region_not_the_display()
    {
        // The rect the boxes are filtered by is the one that was CAPTURED, not the display it sits
        // on: with a region, an element elsewhere on the same monitor is not in this picture.
        var inRegion = Element("el_1", new Bounds(150, 150, 20, 20));
        var elsewhereOnTheDisplay = Element("el_2", new Bounds(20, 20, 20, 20));
        var shot = ShotMock();
        var tools = MakeTools(shot.Object, uia: UiaMock(SnapshotOf([inRegion, elsewhereOnTheDisplay])).Object);

        var result = await tools.Screenshot("100,100,200,200", format: "png", output: "inline", annotate: true);

        CapturedOptions(shot).Annotations.Should().ContainSingle().Which.Label.Should().Be("el_1");
        SnapshotText(result).Should().Contain("el_1").And.NotContain("el_2",
            "the list and the picture describe the same rect");
    }

    [Theory]
    [InlineData(100, true)]      // wholly inside the primary rect
    [InlineData(1060, true)]     // straddles the bottom edge
    [InlineData(1080, false)]    // starts exactly where the rect ends: no overlap at all
    [InlineData(-10, true)]      // straddles the top edge
    [InlineData(-20, false)]     // ends exactly where the rect starts: no overlap at all
    public async Task Screenshot_annotate_applies_the_same_overlap_rule_on_the_y_axis(int y, bool kept)
    {
        // The x axis is covered above; the y terms of the same predicate are a separate pair of
        // comparisons and a copy-paste there would keep every row of the x theory green.
        var element = Element("el_1", new Bounds(100, y, 50, 20));
        var shot = ShotMock();
        var tools = MakeTools(shot.Object, uia: UiaMock(SnapshotOf([element])).Object);

        await tools.Screenshot(format: "png", output: "inline", annotate: true);

        if (kept)
            CapturedOptions(shot).Annotations.Should().ContainSingle().Which.Label.Should().Be("el_1");
        else
            CapturedOptions(shot).Annotations.Should().BeNull();
    }

    [Fact]
    public async Task Screenshot_annotate_with_a_grid_passes_both_to_the_capture()
    {
        // The two overlays are independent arguments on the same call and must not exclude each
        // other: the desktop test can only see that three blocks came back.
        var shot = ShotMock();
        var tools = MakeTools(shot.Object,
            uia: UiaMock(SnapshotOf([Element("el_1", new Bounds(100, 100, 50, 20))])).Object);

        await tools.Screenshot(
            format: "png", output: "inline", annotate: true, grid_columns: 4, grid_rows: 3);

        var options = CapturedOptions(shot);
        options.Annotations.Should().ContainSingle().Which.Label.Should().Be("el_1");
        options.Grid.Should().Be(new GridSpec(4, 3));
    }

    // ---- A-14 (R3) - the post-capture flash --------------------------------------------------
    // The glow is a courtesy signal to whoever is sitting at the target machine, so the contract is
    // about ORDER as much as about the calls: hidden before the shutter (it must never be IN a
    // picture, not even the one that triggered it) and shown after it, around the rect that was
    // actually captured.

    /// <summary>The duration every capture asks for: upstream's 3.5 s.</summary>
    private static readonly TimeSpan FlashDuration = TimeSpan.FromSeconds(3.5);

    [Fact]
    public async Task Screenshot_hides_the_flash_then_captures_then_shows_it()
    {
        var order = new List<string>();
        var flash = new Mock<IFlashOverlay>();
        flash.Setup(f => f.Hide()).Callback(() => order.Add("hide"));
        flash.Setup(f => f.Show(It.IsAny<ScreenRegion>(), It.IsAny<TimeSpan>())).Callback(() => order.Add("show"));
        var shot = new Mock<IScreenshotService>();
        shot.Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                order.Add("capture");
                return new ScreenshotResult(PngBytes, 100, 100, ImageFormat.Png, 100, 100, 1.0);
            });
        var tools = MakeTools(shot.Object, flash: flash.Object);

        await tools.Screenshot(format: "png", output: "inline");

        order.Should().Equal(["hide", "capture", "show"],
            "the previous call's glow comes down BEFORE the shutter, and this call's goes up after it");
    }

    [Fact]
    public async Task Screenshot_shows_the_flash_around_the_captured_rect_for_three_and_a_half_seconds()
    {
        var flash = FlashMock();
        var tools = MakeTools(flash: flash.Object);

        await tools.Screenshot(format: "png", output: "inline");

        flash.Verify(f => f.Show(PrimaryRect, FlashDuration), Times.Once,
            "the glow frames what was captured, for the 3.5 s upstream uses");
        flash.Verify(f => f.Hide(), Times.Once, "exactly one teardown per capture, not one per attempt");
    }

    [Fact]
    public async Task Screenshot_shows_the_flash_around_the_display_that_was_captured()
    {
        // display:"1" is the second monitor: the glow must follow the rect the tool resolved, not
        // the primary display it would have captured by default.
        var flash = FlashMock();
        var tools = MakeTools(windows: WinMock(SideBySide).Object, flash: flash.Object);

        await tools.Screenshot(display: "1", format: "png", output: "inline");

        flash.Verify(f => f.Show(new ScreenRegion(1920, 0, 1920, 1080), FlashDuration), Times.Once);
    }

    [Fact]
    public async Task Screenshot_shows_the_flash_around_an_explicit_region()
    {
        var flash = FlashMock();
        var tools = MakeTools(flash: flash.Object);

        await tools.Screenshot("100,50,300,200", format: "png", output: "inline");

        flash.Verify(f => f.Show(new ScreenRegion(100, 50, 300, 200), FlashDuration), Times.Once);
    }

    [Fact]
    public async Task Screenshot_with_the_flash_switched_off_still_hides_but_never_shows()
    {
        // --flash off. Hide still runs: a glow left up by a server that was reconfigured (or by an
        // earlier call) must still come down before this capture.
        var flash = FlashMock();
        var tools = MakeTools(options: new ScreenshotOptions(1.0, Flash: false), flash: flash.Object);

        await tools.Screenshot(format: "png", output: "inline");

        flash.Verify(f => f.Show(It.IsAny<ScreenRegion>(), It.IsAny<TimeSpan>()), Times.Never);
        flash.Verify(f => f.Hide(), Times.Once);
    }

    [Fact]
    public async Task Screenshot_that_fails_to_capture_never_shows_the_flash()
    {
        // The glow says "a picture was just taken". A capture that threw took no picture.
        var flash = FlashMock();
        var shot = new Mock<IScreenshotService>();
        shot.Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the screen went away"));
        var tools = MakeTools(shot.Object, flash: flash.Object);

        Func<Task> act = () => tools.Screenshot(format: "png", output: "inline");

        await act.Should().ThrowAsync<InvalidOperationException>();
        flash.Verify(f => f.Show(It.IsAny<ScreenRegion>(), It.IsAny<TimeSpan>()), Times.Never);
        flash.Verify(f => f.Hide(), Times.Once, "the teardown ran before the capture and is not undone by its failure");
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    public async Task Screenshot_with_an_invalid_argument_never_touches_the_flash(string output)
    {
        // Same rule as the cursor read: a bad call must not cost a capture - or a glow announcing one.
        var flash = FlashMock();
        var tools = MakeTools(flash: flash.Object);

        Func<Task> act = () => tools.Screenshot(output: output);

        await act.Should().ThrowAsync<ArgumentException>();
        flash.Verify(f => f.Hide(), Times.Never);
        flash.Verify(f => f.Show(It.IsAny<ScreenRegion>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact]
    public async Task Ocr_never_touches_the_flash()
    {
        // OCR takes no picture the caller can see; announcing it with a glow on someone's desktop
        // would be noise, and hiding a glow the OCR did not raise would cut another call's short.
        var flash = FlashMock();
        var ocr = new Mock<IOcrService>();
        ocr.Setup(o => o.ExtractTextAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CancellationToken>())).ReturnsAsync("text");
        var tools = MakeTools(ocr: ocr.Object, flash: flash.Object);

        await tools.Ocr();

        flash.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Screenshot_metadata_says_the_flash_was_shown()
    {
        var tools = MakeTools();

        var meta = Meta(await tools.Screenshot(format: "png", output: "inline"));

        Field(meta, "flash").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Screenshot_metadata_omits_flash_when_the_glow_was_not_shown()
    {
        // Absent, not false: the metadata only carries what happened (the A-7 rule).
        var tools = MakeTools(options: new ScreenshotOptions(1.0, Flash: false));

        var meta = Meta(await tools.Screenshot(format: "png", output: "inline"));

        meta.TryGetProperty("flash", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Screenshot_to_a_file_flashes_and_reports_it_too()
    {
        // The picture still leaves the machine; the file mode is not a quiet mode.
        var flash = FlashMock();
        var tools = MakeTools(flash: flash.Object);

        var result = await tools.Screenshot(format: "png", output: "file");
        TrackPath(result);

        flash.Verify(f => f.Show(PrimaryRect, FlashDuration), Times.Once);
        Field(Meta(result), "flash").GetBoolean().Should().BeTrue();
    }

    // ---- A-14 (R4) - profiling, tool half ----------------------------------------------------

    /// <summary>The metadata's stage timings, asserted present first so a missing block names itself.</summary>
    private static JsonElement StagesOf(JsonElement meta)
    {
        var stages = Field(meta, "stages");
        stages.ValueKind.Should().Be(JsonValueKind.Object, "stages is an object keyed by stage name");
        return stages;
    }

    private static long StageMs(JsonElement stages, string name)
    {
        stages.TryGetProperty(name, out var value).Should().BeTrue($"the stages must include '{name}'");
        value.ValueKind.Should().Be(JsonValueKind.Number, $"'{name}' is a duration in milliseconds");
        return value.GetInt64();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Screenshot_passes_the_process_profiling_switch_into_the_capture_options(bool profile)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object, options: new ScreenshotOptions(1.0, Profile: profile));

        await tools.Screenshot(format: "png", output: "inline");

        CapturedOptions(mock).Profile.Should().Be(profile,
            "--profile-snapshot is a process option (roadmap C7), not a tool argument");
    }

    [Fact]
    public async Task Screenshot_metadata_has_no_stages_when_profiling_is_off()
    {
        // Off is the default, so this is the shape every existing caller sees: unchanged.
        var tools = MakeTools(ShotMock(stages: [new StageTiming("encode", 7)]).Object);

        var meta = Meta(await tools.Screenshot(format: "png", output: "inline"));

        meta.TryGetProperty("stages", out _).Should()
            .BeFalse("no profiling was asked for, so the response carries no timings at all");
    }

    [Fact]
    public async Task Screenshot_metadata_stages_carry_the_tools_own_steps_and_the_services()
    {
        var shot = ShotMock(stages: [new StageTiming("resize", 3), new StageTiming("encode", 7)]);
        var tools = MakeTools(shot.Object, options: new ScreenshotOptions(1.0, Profile: true));

        var stages = StagesOf(Meta(await tools.Screenshot(format: "png", output: "inline")));

        StageMs(stages, "resolve").Should().BeGreaterThanOrEqualTo(0, "resolving the rect is a monitor enumeration");
        StageMs(stages, "cursor").Should().BeGreaterThanOrEqualTo(0);
        StageMs(stages, "capture").Should().BeGreaterThanOrEqualTo(0);
        StageMs(stages, "resize").Should().Be(3, "the service's own stages come through by name");
        StageMs(stages, "encode").Should().Be(7);
    }

    [Fact]
    public async Task Screenshot_stages_omit_the_snapshot_step_when_nothing_was_annotated()
    {
        var tools = MakeTools(options: new ScreenshotOptions(1.0, Profile: true));

        var stages = StagesOf(Meta(await tools.Screenshot(format: "png", output: "inline")));

        stages.TryGetProperty("snapshot", out _).Should()
            .BeFalse("no walk happened, so there is no walk to time");
    }

    [Fact]
    public async Task Screenshot_stages_include_the_snapshot_step_when_annotating()
    {
        // The walk is the expensive half of an annotated capture - it is the whole reason to profile.
        var tools = MakeTools(options: new ScreenshotOptions(1.0, Profile: true));

        var stages = StagesOf(Meta(await tools.Screenshot(format: "png", output: "inline", annotate: true)));

        StageMs(stages, "snapshot").Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Screenshot_stages_let_the_services_own_measurement_win_a_name_clash()
    {
        // The tool times the CaptureAsync CALL as "capture" and the service reports its own
        // "capture" stage (the CopyFromScreen inside it). The service's stages are merged in after
        // the tool's, so the finer-grained number is the one reported.
        var shot = ShotMock(stages: [new StageTiming("capture", 4242)]);
        var tools = MakeTools(shot.Object, options: new ScreenshotOptions(1.0, Profile: true));

        var stages = StagesOf(Meta(await tools.Screenshot(format: "png", output: "inline")));

        StageMs(stages, "capture").Should().Be(4242);
    }

    [Fact]
    public async Task Screenshot_logs_the_stage_timings_when_profiling_is_on()
    {
        // The roadmap's reason for --profile-snapshot is a line on stderr an operator can read
        // without parsing a tool response: the numbers in the metadata are for the model, the log
        // line is for the human. Every assertion above would stay green if the log call were
        // deleted, so this is the one that holds it.
        var log = new RecordingLogger<ScreenTools>();
        var shot = ShotMock(stages: [new StageTiming("encode", 7)]);
        var tools = MakeTools(shot.Object, options: new ScreenshotOptions(1.0, Profile: true), log: log);

        await tools.Screenshot(format: "png", output: "inline");

        var line = log.MessagesAt(LogLevel.Information).Should().ContainSingle(
            "one line per profiled capture, at the level ConfigureStderrLogging actually emits").Subject;
        line.Should().Contain("resolve").And.Contain("cursor").And.Contain("capture")
            .And.Contain("encode 7 ms", "the service's stages are in the log line too, not only the metadata");
    }

    [Fact]
    public async Task Screenshot_logs_nothing_when_profiling_is_off()
    {
        // Off is the default: a server nobody asked to profile must not write a line per capture.
        var log = new RecordingLogger<ScreenTools>();
        var tools = MakeTools(log: log);

        await tools.Screenshot(format: "png", output: "inline");

        log.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Screenshot_metadata_omits_flash_when_the_overlay_could_not_show()
    {
        // No interactive window station: Show is a silent no-op and IsVisible stays false. The
        // metadata reports the outcome, so it must not claim a glow nobody saw.
        var flash = new Mock<IFlashOverlay>();   // IsVisible stays false after Show
        var tools = MakeTools(ShotMock().Object, flash: flash.Object);

        var meta = Meta(await tools.Screenshot(format: "png", output: "inline"));

        flash.Verify(f => f.Show(It.IsAny<ScreenRegion>(), It.IsAny<TimeSpan>()), Times.Once);
        meta.TryGetProperty("flash", out _).Should().BeFalse();
    }
    // ---- A-10 (R4) - the capture backend argument and the backend metadata --------------------
    // The tool does not choose the backend, it only carries the choice down and reports what came
    // back: ScreenshotService.ResolveBackend owns the rule (ScreenshotBackendTests) and the frames
    // themselves are ScreenshotWgcCaptureTests. What IS a tool-layer requirement is the validation
    // (before any work) and that the metadata never lies about what produced the picture.

    /// <summary>The three values the tool accepts, in the error message and in the description.</summary>
    private static readonly string[] Backends = ["auto", "gdi", "wgc"];

    [Fact]
    public async Task Screenshot_defaults_to_the_process_backend()
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot(format: "png", output: "inline");

        CapturedOptions(mock).Backend.Should().Be("auto",
            "a call that names no backend takes whatever the server was started with");
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("gdi")]
    [InlineData("wgc")]
    public async Task Screenshot_passes_the_requested_backend_to_the_capture_options(string backend)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot(format: "png", output: "inline", backend: backend);

        CapturedOptions(mock).Backend.Should().Be(backend, "the choice reaches the service verbatim");
    }

    [Theory]
    [InlineData("AUTO", "auto")]
    [InlineData("Gdi", "gdi")]
    [InlineData("WGC", "wgc")]
    public async Task Screenshot_backend_matching_is_case_insensitive(string backend, string expected)
    {
        var mock = ShotMock();
        var tools = MakeTools(mock.Object);

        await tools.Screenshot(format: "png", output: "inline", backend: backend);

        CapturedOptions(mock).Backend.Should().BeEquivalentTo(expected,
            "every other enum-like argument on this tool is case-insensitive (output, format, display)");
    }

    [Theory]
    [InlineData("dxcam")]     // upstream's backend names are not ours
    [InlineData("mss")]
    [InlineData("pillow")]
    [InlineData("dxgi")]
    [InlineData("")]
    [InlineData(" wgc")]      // un-trimmed, like every other option
    public async Task Screenshot_unknown_backend_throws_naming_the_choices(string backend)
    {
        var tools = MakeTools();

        Func<Task> act = () => tools.Screenshot(format: "png", output: "inline", backend: backend);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        foreach (var name in Backends)
            message.Should().Contain(name, "the model cannot guess the vocabulary from a bare rejection");
    }

    [Fact]
    public async Task Screenshot_unknown_backend_is_rejected_before_any_work()
    {
        // Same rule as every other argument guard on this tool: a bad call must not cost a monitor
        // enumeration, a cursor read, a flash teardown or a capture.
        var mock = ShotMock();
        var windows = WinMock();
        var input = InputMock();
        var flash = FlashMock();
        var tools = MakeTools(mock.Object, windows: windows.Object, input: input.Object, flash: flash.Object);

        Func<Task> act = () => tools.Screenshot(format: "png", output: "inline", backend: "dxcam");

        await act.Should().ThrowAsync<ArgumentException>();
        windows.Verify(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()), Times.Never);
        ShouldNeverReadTheCursor(input);
        ShouldNeverCapture(mock);
        flash.Verify(f => f.Hide(), Times.Never);
        flash.Verify(f => f.Show(It.IsAny<ScreenRegion>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Theory]
    [InlineData("gdi")]
    [InlineData("wgc")]
    public async Task Screenshot_metadata_reports_the_backend_that_produced_the_picture(string produced)
    {
        var tools = MakeTools(ShotMock(resultBackend: produced).Object);

        var meta = Meta(await tools.Screenshot(format: "png", output: "inline"));

        Field(meta, "backend").GetString().Should().Be(produced,
            "the metadata reports what was captured, never what was asked for");
    }

    [Fact]
    public async Task Screenshot_metadata_backend_follows_the_result_not_the_request()
    {
        // The 'auto' case is the whole reason the field exists: the caller does not know which
        // backend served them until the result says so.
        var tools = MakeTools(ShotMock(resultBackend: "wgc").Object);

        var meta = Meta(await tools.Screenshot(format: "png", output: "inline", backend: "auto"));

        Field(meta, "backend").GetString().Should().Be("wgc");
    }

    [Fact]
    public async Task Screenshot_file_output_metadata_carries_the_backend_too()
    {
        var tools = MakeTools(ShotMock(PngBytes, 100, 100, resultBackend: "wgc").Object);

        var result = await tools.Screenshot(null, format: "png", output: "file");

        var meta = Meta(result);
        _written.Add(meta.GetProperty("path").GetString()!);
        Field(meta, "backend").GetString().Should().Be("wgc", "both output modes describe the same capture");
    }

    [Fact]
    public void Screenshot_backend_is_the_last_argument_and_defaults_to_auto()
    {
        // Appended, not inserted: every existing caller and test passes the earlier arguments
        // positionally, and the schema default is what the model reads.
        var parameters = typeof(ScreenTools).GetMethod(nameof(ScreenTools.Screenshot))!.GetParameters();

        parameters[^1].Name.Should().Be("backend");
        parameters[^1].DefaultValue.Should().Be("auto");
        parameters[^2].Name.Should().Be("grid_rows", "A-6's block stays where it was");
    }

    [Fact]
    public void Ocr_has_no_backend_argument_and_takes_the_process_default()
    {
        // OCR reads pixels, not a picture for the model: there is no reason to expose the choice,
        // and OcrServiceTests pins that its CaptureOptions leave Backend at "auto".
        typeof(ScreenTools).GetMethod(nameof(ScreenTools.Ocr))!.GetParameters()
            .Select(p => p.Name).Should().NotContain("backend");
    }

    [Fact]
    public void Screenshot_description_documents_the_backend_argument_and_metadata()
    {
        var description = typeof(ScreenTools).GetMethod(nameof(ScreenTools.Screenshot))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("backend", "the metadata list is the only place the model learns the field exists")
            .And.Contain("wgc", "and the only place it learns the other backend can be asked for by name")
            .And.Contain("black", "the model needs the one reason to reach for wgc: GDI returns black for GPU/DRM surfaces");

        var attribute = typeof(ScreenTools).GetMethod(nameof(ScreenTools.Screenshot))!.GetParameters()
            .Single(p => p.Name == "backend").GetCustomAttribute<DescriptionAttribute>();
        attribute.Should().NotBeNull("an argument with no description is invisible in the wire schema");
        foreach (var name in Backends)
            attribute!.Description.Should().Contain(name);
        attribute!.Description.Should().Contain("default", "the model must know which one it gets for free");
    }
}
