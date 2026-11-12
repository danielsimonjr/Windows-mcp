using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services.UiTree;

/// <summary>
/// B-4 / roadmap C1: the one place an element id becomes a point for the input verbs. The centre
/// is integer division of the bounds; an element the pointer cannot reach is refused with a
/// message that names the id and the reason, so the agent scrolls or focuses first instead of
/// clicking a coordinate that means nothing. Off-screen is reported before missing bounds
/// because both usually hold at once and the first is the actionable one.
/// </summary>
internal static class ElementTarget
{
    internal static (int X, int Y) CentreOf(ElementInfo info)
    {
        if (info.IsOffscreen)
            throw new InvalidOperationException(
                $"Element {info.ElementId} ('{UiRefusals.Clip(info.Name)}') is off-screen: bring it into view with interact_element scroll_into_view (or scroll its container, or focus its window), then take a new snapshot.");
        if (info.Bounds is null)
            throw new InvalidOperationException(
                $"Element {info.ElementId} ('{UiRefusals.Clip(info.Name)}') has no bounds, so it has no point to aim at.");
        var b = info.Bounds;
        if (b.Width <= 0 || b.Height <= 0)
            throw new InvalidOperationException(
                $"Element {info.ElementId} ('{UiRefusals.Clip(info.Name)}') has empty bounds ({b.Width}x{b.Height}), so it has no point to aim at.");
        return (b.X + b.Width / 2, b.Y + b.Height / 2);
    }
}
