using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class IntegrityTools
{
    private readonly IIntegrityService _integrity;

    public IntegrityTools(IIntegrityService integrity) => _integrity = integrity;

    [McpServerTool, Description(
        "File-integrity tripwire over a curated watch-list (hosts file, user+machine Startup folders, " +
        "~/.claude/settings.json, ~/.gitconfig, and the C:\\ governance files). " +
        "mode: baseline (snapshot SHA-256 of the watch-list to %LOCALAPPDATA%\\windows-mcp\\integrity, survives plugin upgrades), " +
        "check (diff current filesystem vs baseline -> added/removed/modified), " +
        "list (show the default watch-list and the current baseline). " +
        "paths: extra paths (semicolon-separated) ADDED to the default watch-list on baseline.")]
    public async Task<string> Integrity(
        [Description("Mode: baseline, check, list")] string mode,
        [Description("Extra paths to watch, semicolon-separated (baseline mode only)")] string? paths = null,
        CancellationToken ct = default)
    {
        var extra = string.IsNullOrWhiteSpace(paths)
            ? null
            : paths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return mode.ToLowerInvariant() switch
        {
            "baseline" => JsonSerializer.Serialize(await _integrity.BaselineAsync(extra, ct)),
            "check" => JsonSerializer.Serialize(await _integrity.CheckAsync(ct)),
            "list" => JsonSerializer.Serialize(new { watchList = _integrity.DefaultWatchList(), baseline = _integrity.GetBaseline() }),
            _ => throw new ArgumentException($"Unknown mode '{mode}'; expected baseline|check|list")
        };
    }
}
