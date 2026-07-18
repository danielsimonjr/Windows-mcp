using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class WatchTools
{
    private readonly IWatchService _watch;

    public WatchTools(IWatchService watch) => _watch = watch;

    [McpServerTool, Description(
        "Live directory watching (FileSystemWatcher) with server-side event buffering. " +
        "mode: start (watch a directory; returns a session id), poll (drain buffered created/changed/deleted/renamed events for a session), " +
        "stop (end a session), list (active sessions with buffered/dropped counts). " +
        "path: directory to watch (start). filter: glob like *.exe (start; default *). subdirs: recurse (start). " +
        "id: session id (poll/stop). max: cap poll batch (default 500). " +
        "Events buffer in a bounded ring between polls (oldest dropped when full).")]
    public string Watch(
        [Description("Mode: start, poll, stop, list")] string mode,
        [Description("Directory to watch (start mode)")] string? path = null,
        [Description("Filename glob, e.g. *.exe (start mode; default *)")] string? filter = null,
        [Description("Recurse into subdirectories (start mode)")] bool subdirs = false,
        [Description("Session id (poll/stop modes)")] string? id = null,
        [Description("Max events per poll (default 500)")] int max = 500)
    {
        switch (mode.ToLowerInvariant())
        {
            case "start":
                if (string.IsNullOrWhiteSpace(path))
                    throw new ArgumentException("start mode requires 'path'");
                return JsonSerializer.Serialize(_watch.Start(path, filter, subdirs));
            case "poll":
                if (string.IsNullOrWhiteSpace(id))
                    throw new ArgumentException("poll mode requires 'id'");
                return JsonSerializer.Serialize(_watch.Poll(id, max));
            case "stop":
                if (string.IsNullOrWhiteSpace(id))
                    throw new ArgumentException("stop mode requires 'id'");
                return JsonSerializer.Serialize(new { stopped = _watch.Stop(id) });
            case "list":
                return JsonSerializer.Serialize(_watch.List());
            default:
                throw new ArgumentException($"Unknown mode '{mode}'; expected start|poll|stop|list");
        }
    }
}
