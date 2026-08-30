namespace WindowsMcp.Services;

/// <summary>
/// Canonicalizes and gates filesystem paths used by file tools. Device paths
/// (<c>\\.\</c>) are refused; search results are capped to bound enumeration DoS.
/// </summary>
internal static class PathPolicy
{
    public const int MaxSearchHits = 10_000;

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty");

        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\.\", StringComparison.Ordinal)
            || trimmed.StartsWith("//./", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new ArgumentException("Device and extended paths (\\\\.\\ / \\\\?\\) are not permitted");

        return Path.GetFullPath(trimmed);
    }
}
