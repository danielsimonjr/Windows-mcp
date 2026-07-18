using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

/// <summary>
/// File-integrity "tripwire": snapshot SHA-256 of a curated watch-list, then later detect
/// added / removed / modified files. A Tech-Guru change-detection primitive for the persistence
/// spots malware and misconfiguration touch (hosts file, Startup folders, key config).
/// </summary>
public interface IIntegrityService
{
    /// <summary>Hash the default watch-list (plus any <paramref name="extraPaths"/>), persist it, and return it.</summary>
    Task<IntegrityBaseline> BaselineAsync(IEnumerable<string>? extraPaths = null, CancellationToken ct = default);

    /// <summary>Diff the current filesystem against the saved baseline. HasBaseline=false if none exists yet.</summary>
    Task<IntegrityCheckResult> CheckAsync(CancellationToken ct = default);

    /// <summary>The saved baseline, or null if none has been taken.</summary>
    IntegrityBaseline? GetBaseline();

    /// <summary>The curated default watch-list (env vars expanded).</summary>
    string[] DefaultWatchList();
}
