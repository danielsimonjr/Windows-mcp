namespace WindowsMcp.Services;

/// <summary>A top-level window as seen by the enumerator, reduced to what matching needs.</summary>
public readonly record struct WindowCandidate(nint Handle, string Title, bool IsVisible, long Area);

/// <summary>
/// Chooses which enumerated window a caller meant.
///
/// This exists because the previous implementation used <c>FindWindow(null, title)</c>, which
/// requires an EXACT, full-string title. That fails in two ways that are routine rather than
/// exotic:
///
///   1. Modern apps put live state in the title. Obsidian's reads "New tab - &lt;vault&gt; - Obsidian
///      1.13.7" and changes as tabs change, so a title read moments earlier no longer matches when
///      the call is made. Measured 2026-09-17: `focus` reported "window not found" for a title that
///      EnumWindows confirmed was visible with that byte-exact string, while `get_state` listed the
///      same window happily - the two disagreed because only one of them went through FindWindow.
///   2. A caller naturally passes the app name ("Obsidian"), not the full decorated caption.
///
/// The matcher is deliberately separated from the P/Invoke so the ranking rules can be tested
/// without a desktop.
/// </summary>
public static class WindowMatcher
{
    /// <summary>
    /// Returns the best match for <paramref name="query"/>, or null when nothing matches.
    ///
    /// Ranking, strongest first: exact title, then prefix, then substring - all case-insensitive
    /// and trimmed. Within a tier a VISIBLE window always beats a hidden one, and then the LARGER
    /// window wins, because shells keep zero-area helper windows carrying the same caption as the
    /// real one (File Explorer and the PortableApps platform both do on this machine) and
    /// activating one of those looks exactly like a silent no-op.
    /// </summary>
    public static WindowCandidate? Select(IReadOnlyList<WindowCandidate> candidates, string query)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (string.IsNullOrWhiteSpace(query))
            return null;

        string needle = query.Trim();

        // Tier 0 = exact, 1 = prefix, 2 = substring. Lower is better.
        static int Tier(string title, string needle)
        {
            if (title.Equals(needle, StringComparison.OrdinalIgnoreCase)) return 0;
            if (title.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) return 1;
            if (title.Contains(needle, StringComparison.OrdinalIgnoreCase)) return 2;
            return int.MaxValue;
        }

        WindowCandidate? best = null;
        int bestTier = int.MaxValue;

        foreach (WindowCandidate c in candidates)
        {
            if (string.IsNullOrEmpty(c.Title))
                continue;

            int tier = Tier(c.Title.Trim(), needle);
            if (tier == int.MaxValue)
                continue;

            if (best is null || IsBetter(tier, c, bestTier, best.Value))
            {
                best = c;
                bestTier = tier;
            }
        }

        return best;
    }

    private static bool IsBetter(int tier, WindowCandidate c, int bestTier, WindowCandidate best)
    {
        if (tier != bestTier) return tier < bestTier;
        if (c.IsVisible != best.IsVisible) return c.IsVisible;
        return c.Area > best.Area;
    }
}
