using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class ScreenTools
{
    private readonly IScreenshotService _screenshot;
    private readonly IOcrService _ocr;

    public ScreenTools(IScreenshotService screenshot, IOcrService ocr)
    {
        _screenshot = screenshot;
        _ocr = ocr;
    }

    [McpServerTool, Description("Capture a screenshot of the screen or a region.")]
    public async Task<string> Screenshot(
        [Description("Region as 'x,y,w,h' or null for full primary display")] string? region = null,
        [Description("Image format: png or jpeg")] string format = "png",
        [Description("Output mode: 'file' (default) saves to %TEMP%\\WindowsMcp and returns the file path — context-efficient; 'base64' returns inline base64 data")] string output = "file")
    {
        var r = ParseRegion(region);
        var fmt = format.ToLowerInvariant() == "jpeg" ? ImageFormat.Jpeg : ImageFormat.Png;
        var result = await _screenshot.CaptureAsync(r, fmt);

        if (output.Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(new
            {
                width = result.Width,
                height = result.Height,
                format = result.Format.ToString().ToLowerInvariant(),
                data_base64 = Convert.ToBase64String(result.Bytes)
            });
        }

        // Default "file" mode: persist to temp dir, return path (no base64 in context).
        var ext = result.Format == ImageFormat.Jpeg ? "jpg" : "png";
        var dir = Path.Combine(Path.GetTempPath(), "WindowsMcp");
        Directory.CreateDirectory(dir);
        var fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.{ext}";
        var filePath = Path.Combine(dir, fileName);
        await File.WriteAllBytesAsync(filePath, result.Bytes);
        return JsonSerializer.Serialize(new
        {
            path = filePath,
            width = result.Width,
            height = result.Height,
            format = result.Format.ToString().ToLowerInvariant()
        });
    }

    [McpServerTool, Description("Run OCR on the screen or a region and return extracted text.")]
    public async Task<string> Ocr(
        [Description("Region as 'x,y,w,h' or null for full primary display")] string? region = null)
    {
        var r = ParseRegion(region);
        return await _ocr.ExtractTextAsync(r);
    }

    private static ScreenRegion? ParseRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region)) return null;
        var parts = region.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
            throw new ArgumentException($"Invalid region '{region}'; expected 'x,y,w,h'");
        if (!int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)
            || !int.TryParse(parts[2], out var w) || !int.TryParse(parts[3], out var h))
            throw new ArgumentException($"Invalid region '{region}'; each component must be an integer");
        if (w < 0 || h < 0)
            throw new ArgumentException($"Invalid region '{region}'; width and height must be non-negative");
        return new ScreenRegion(x, y, w, h);
    }
}
