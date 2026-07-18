namespace WindowsMcp.Abstractions.Models;

/// <summary>One watched file's hash + metadata at baseline time. Exists=false records a
/// watched path that was absent when the baseline was taken (so its later appearance is caught).</summary>
public record IntegrityItem(string Path, bool Exists, string? Sha256, long SizeBytes, DateTime? ModifiedUtc);

/// <summary>A single detected change. Kind is one of: added | removed | modified.</summary>
public record IntegrityChange(string Path, string Kind, string? OldSha256, string? NewSha256);

/// <summary>A saved integrity snapshot: the resolved roots plus a hash of every file under them.</summary>
public record IntegrityBaseline(DateTime CreatedUtc, string[] Roots, IntegrityItem[] Items);

/// <summary>Result of diffing the current filesystem against a saved baseline.</summary>
public record IntegrityCheckResult(
    DateTime? BaselineUtc, DateTime CheckedUtc, bool HasBaseline, int Unchanged, IntegrityChange[] Changes);
