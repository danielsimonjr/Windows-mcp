using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

/// <summary>
/// Live directory watching via FileSystemWatcher. Events buffer server-side (bounded ring) between
/// polls, so a client can start a watch, do other work, then drain what changed. Complements the
/// point-in-time integrity tripwire and the volume-wide USN journal with real-time notification.
/// </summary>
public interface IWatchService
{
    /// <summary>Begin watching a directory. Returns the new session (with its id).</summary>
    WatchSession Start(string path, string? filter, bool includeSubdirectories);

    /// <summary>Drain up to <paramref name="max"/> buffered events for a session (empty if the id is unknown).</summary>
    WatchEvent[] Poll(string id, int max);

    /// <summary>End a session and release its watcher. Returns false if the id is unknown.</summary>
    bool Stop(string id);

    /// <summary>All active sessions with their buffered/dropped counts.</summary>
    WatchSession[] List();
}
