namespace WindowsMcp.Abstractions.Models;

/// <summary>One NTFS USN change-journal record: what file changed, why (reason flags), its USN, and when.</summary>
public record UsnChange(string FileName, string Reasons, long Usn, DateTime TimeUtc);

/// <summary>USN journal state for a volume. Record <see cref="NextUsn"/> now, then query 'since' it later.</summary>
public record UsnStatus(string Volume, ulong JournalId, long FirstUsn, long NextUsn, long LowestValidUsn);

/// <summary>Change records read from the journal, plus the NextUsn to resume from on the following read.</summary>
public record UsnReadResult(string Volume, long NextUsn, int Count, UsnChange[] Changes);
