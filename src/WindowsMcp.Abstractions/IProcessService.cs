using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IProcessService
{
    /// <summary>All processes, optionally filtered by a case-insensitive substring of the name.
    /// (Name only — a ProcessDto carries no command line; use ListLineageAsync to match on that.)</summary>
    Task<ProcessDto[]> ListAsync(string? nameFilter = null, CancellationToken ct = default);
    Task KillAsync(int pid, CancellationToken ct = default);
    Task<int> StartDetachedAsync(string command, CancellationToken ct = default);
    /// <summary>Deep detail for one process: parent PID, command line, start time, loaded modules.</summary>
    Task<ProcessDetailDto> InspectAsync(int pid, CancellationToken ct = default);
    /// <summary>All processes with recycle-aware lineage + signals; optionally only orphans,
    /// optionally filtered (substring on name OR command line). Filter is applied after
    /// classification so RootPid still resolves to a filtered-out root.</summary>
    Task<ProcessLineageDto[]> ListLineageAsync(bool orphansOnly, string? nameFilter, CancellationToken ct = default);
    /// <summary>Processes collapsed under their nearest-live root ancestor. With a nameFilter
    /// (substring on name OR command line), only groups containing at least one match are returned —
    /// each keeping its full membership and true DescendantCount.</summary>
    Task<ProcessGroupDto[]> GroupByRootAsync(string? nameFilter = null, CancellationToken ct = default);
    /// <summary>Kill a single PID only if its live start time matches expectedStartUtc (guards PID reuse).</summary>
    Task KillGuardedAsync(int pid, DateTime expectedStartUtc, CancellationToken ct = default);
    /// <summary>Kill a PID and its recycle-validated descendants, leaves-first; returns count killed.</summary>
    Task<int> KillTreeAsync(int pid, DateTime? expectedStartUtc, CancellationToken ct = default);
}
