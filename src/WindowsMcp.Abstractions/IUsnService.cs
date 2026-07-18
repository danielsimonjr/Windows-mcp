using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

/// <summary>
/// NTFS USN change-journal reader: volume-wide "what files changed" via the OS change journal.
/// Efficient and complete (every create/delete/rename/write is recorded), unlike a directory scan.
/// Requires an elevated volume handle. Complements the curated <see cref="IIntegrityService"/> tripwire
/// with whole-volume coverage.
/// </summary>
public interface IUsnService
{
    /// <summary>Journal identity and the FirstUsn / NextUsn / LowestValidUsn range for a volume.</summary>
    Task<UsnStatus> StatusAsync(string volume, CancellationToken ct = default);

    /// <summary>Read change records from <paramref name="startUsn"/> forward (0 = oldest available), up to <paramref name="max"/>.</summary>
    Task<UsnReadResult> ReadAsync(string volume, long startUsn, int max, CancellationToken ct = default);
}
