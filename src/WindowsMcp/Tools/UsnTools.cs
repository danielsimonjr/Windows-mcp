using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class UsnTools
{
    private readonly IUsnService _usn;

    public UsnTools(IUsnService usn) => _usn = usn;

    [McpServerTool, Description(
        "NTFS USN change journal — whole-volume file-change tracking straight from the OS journal " +
        "(every create/delete/rename/write, far more complete than a directory scan). " +
        "mode: status (journal id + FirstUsn/NextUsn/LowestValidUsn range — record NextUsn now, query 'since' it later), " +
        "since (read change records from start_usn forward). " +
        "volume: drive letter (default C). start_usn: USN to read from in 'since' mode (0 = oldest available). " +
        "max: cap records in 'since' mode (default 200). Requires elevation.")]
    public async Task<string> FsChanges(
        [Description("Mode: status, since")] string mode,
        [Description("Drive letter, e.g. C")] string volume = "C",
        [Description("Start USN for 'since' mode (0 = oldest available)")] long start_usn = 0,
        [Description("Max records for 'since' mode (default 200)")] int max = 200,
        CancellationToken ct = default)
    {
        return mode.ToLowerInvariant() switch
        {
            "status" => JsonSerializer.Serialize(await _usn.StatusAsync(volume, ct)),
            "since" => JsonSerializer.Serialize(await _usn.ReadAsync(volume, start_usn, max, ct)),
            _ => throw new ArgumentException($"Unknown mode '{mode}'; expected status|since")
        };
    }
}
