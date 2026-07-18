using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class WatchService : IWatchService, IDisposable
{
    private sealed class Session
    {
        public required string Id { get; init; }
        public required string Path { get; init; }
        public required string Filter { get; init; }
        public required bool IncludeSubdirectories { get; init; }
        public required FileSystemWatcher Watcher { get; init; }
        public required EventRingBuffer Buffer { get; init; }
    }

    private readonly Dictionary<string, Session> _sessions = new();
    private readonly object _lock = new();
    private int _seq;

    public WatchSession Start(string path, string? filter, bool includeSubdirectories)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Watch path not found: {path}");

        var buffer = new EventRingBuffer(2000);
        var fsw = new FileSystemWatcher(path)
        {
            Filter = string.IsNullOrWhiteSpace(filter) ? "*" : filter,
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        fsw.Created += (_, e) => buffer.Add(new WatchEvent("created", e.FullPath, DateTime.UtcNow));
        fsw.Changed += (_, e) => buffer.Add(new WatchEvent("changed", e.FullPath, DateTime.UtcNow));
        fsw.Deleted += (_, e) => buffer.Add(new WatchEvent("deleted", e.FullPath, DateTime.UtcNow));
        fsw.Renamed += (_, e) => buffer.Add(new WatchEvent("renamed", e.FullPath, DateTime.UtcNow));
        fsw.EnableRaisingEvents = true;

        string id;
        lock (_lock)
        {
            id = "w" + (++_seq);
            _sessions[id] = new Session
            {
                Id = id, Path = path, Filter = fsw.Filter,
                IncludeSubdirectories = includeSubdirectories, Watcher = fsw, Buffer = buffer,
            };
        }
        return new WatchSession(id, path, fsw.Filter, includeSubdirectories, 0, 0);
    }

    public WatchEvent[] Poll(string id, int max)
    {
        EventRingBuffer? buffer = null;
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s)) buffer = s.Buffer;
        }
        return buffer?.Drain(max <= 0 ? 500 : max) ?? Array.Empty<WatchEvent>();
    }

    public bool Stop(string id)
    {
        Session? session = null;
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s))
            {
                session = s;
                _sessions.Remove(id);
            }
        }
        if (session is null) return false;
        session.Watcher.EnableRaisingEvents = false;
        session.Watcher.Dispose();
        return true;
    }

    public WatchSession[] List()
    {
        lock (_lock)
        {
            return _sessions.Values
                .Select(s => new WatchSession(s.Id, s.Path, s.Filter, s.IncludeSubdirectories, s.Buffer.Count, s.Buffer.Dropped))
                .ToArray();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values)
            {
                try { s.Watcher.Dispose(); } catch { /* best-effort teardown */ }
            }
            _sessions.Clear();
        }
    }
}
