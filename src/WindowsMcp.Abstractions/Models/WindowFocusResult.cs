namespace WindowsMcp.Abstractions.Models;

/// <summary>
/// The outcome of a focus request, split into its two independent facts.
///
/// These were previously collapsed into a single bool, and the tool reported any false as
/// "window '&lt;title&gt;' not found". That is wrong and it actively misleads: Windows restricts
/// which process may change the foreground window, so <c>SetForegroundWindow</c> routinely returns
/// false for a background server even when the window was located perfectly. Measured 2026-09-17 -
/// `focus` said "not found" for a window that `window restore` located in the same process moments
/// later, which sent the investigation after a lookup bug that did not exist.
/// </summary>
/// <param name="Found">Whether a window matching the requested title was located.</param>
/// <param name="Activated">
/// Whether the window actually came to the foreground. False with <paramref name="Found"/> true
/// means Windows refused the foreground change - the window was raised as far as it could be.
/// </param>
public readonly record struct WindowFocusResult(bool Found, bool Activated);
