namespace WindowsMcp.Abstractions.Models;

/// <summary>A single live filesystem event. Kind is one of: created | changed | deleted | renamed.</summary>
public record WatchEvent(string Kind, string Path, DateTime AtUtc);

/// <summary>An active watch session. Buffered = events waiting to be polled; Dropped = events lost to the ring cap.</summary>
public record WatchSession(string Id, string Path, string Filter, bool IncludeSubdirectories, int Buffered, int Dropped);
