using System.Security;

namespace WindowsMcp.Services;

/// <summary>
/// D-10 R14: one shape for registry access-denied. <c>OpenSubKey</c> reports it as a
/// <see cref="SecurityException"/> (a SystemException the call-tool filter masks),
/// <c>DeleteSubKey</c> as a bare <see cref="UnauthorizedAccessException"/> naming nothing, and
/// <c>CreateSubKey</c> as one that names the key — the same refusal, three answers.
/// </summary>
internal static class RegistryErrors
{
    /// <summary>
    /// An <see cref="UnauthorizedAccessException"/> naming the hive (as the caller gave it) and
    /// the key, or null when <paramref name="ex"/> is not an access-denied.
    /// </summary>
    /// <param name="partial">
    /// R25: the denial came while a key TREE was being deleted, after the pre-checks passed, so
    /// sub-keys may already be gone. The message then says so; a clean refusal claims nothing.
    /// </param>
    public static Exception? Translate(Exception ex, string hive, string path, bool partial = false)
    {
        if (ex is not (SecurityException or UnauthorizedAccessException)) return null;
        // Already ours (a descendant refused inside an outer Guard): keep the more precise key.
        if (ex.Data.Contains(TranslatedMarker)) return null;

        var key = path.Length == 0 ? hive : $"{hive}\\{path}";
        var message = partial
            ? $"Deleting registry key '{key}' was denied part-way: some sub-keys may already have been removed. " +
              "Read the key again to see what is left. A protected key needs the server to run elevated."
            : $"Access to registry key '{key}' is denied. A protected key needs the server to run elevated.";
        var denied = new UnauthorizedAccessException(message, ex);
        denied.Data[TranslatedMarker] = true;
        return denied;
    }

    /// <summary>
    /// Marks a refusal this class built, so an outer <see cref="Guard{T}"/> leaves it alone: a
    /// descendant that refused inside a tree walk is already named, more precisely than the
    /// parent the outer guard knows about.
    /// </summary>
    private const string TranslatedMarker = "WindowsMcp.RegistryErrors.Translated";

    /// <summary>Runs one registry operation with its access-denied translated.</summary>
    public static T Guard<T>(string hive, string path, Func<T> operation, bool partial = false)
    {
        try { return operation(); }
        catch (Exception ex) when (Translate(ex, hive, path, partial) is { } denied) { throw denied; }
    }
}
