using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;
using Xunit;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// B-4 / roadmap C1 (R1): the one resolver every input verb uses to turn an <c>element_id</c> into
/// a point. It is pure, so the whole contract — the centre, and the three refusals — is pinned
/// here without a desktop; the verbs' tests then only have to prove they GO through it.
/// </summary>
[Trait("Category", "Unit")]
public class ElementTargetTests
{
    private static ElementInfo Element(Bounds? bounds, bool offscreen = false, string id = "el_7", string name = "Save")
        => new(id, name, "Button", IsEnabled: true, IsOffscreen: offscreen, Bounds: bounds,
               Value: null, IsChecked: null, IsSelected: null);

    [Theory]
    [InlineData(0, 0, 10, 10, 5, 5)]
    [InlineData(100, 200, 40, 20, 120, 210)]
    // Integer division, not rounding: an odd extent rounds DOWN, and the centre of a 1x1 element
    // is the element itself. Half-pixel drift is what makes a click land on a neighbouring cell.
    [InlineData(10, 20, 5, 7, 12, 23)]
    [InlineData(0, 0, 1, 1, 0, 0)]
    [InlineData(7, 9, 1, 1, 7, 9)]
    [InlineData(3, 3, 3, 3, 4, 4)]
    public void CentreOf_is_the_integer_division_centre_of_the_bounds(
        int x, int y, int width, int height, int expectedX, int expectedY)
    {
        ElementTarget.CentreOf(Element(new Bounds(x, y, width, height)))
            .Should().Be((expectedX, expectedY));
    }

    [Fact]
    public void CentreOf_accepts_an_element_on_a_monitor_left_of_and_above_the_primary()
    {
        // D-3 / roadmap C2: coordinates are signed virtual-desktop pixels. A negative centre is a
        // legitimate answer, not an error - refusing it would make every element on a left-hand
        // monitor unclickable.
        ElementTarget.CentreOf(Element(new Bounds(-1920, -100, 200, 100)))
            .Should().Be((-1820, -50));
    }

    [Fact]
    public void CentreOf_refuses_an_offscreen_element_naming_it_and_the_reason()
    {
        var act = () => ElementTarget.CentreOf(Element(new Bounds(0, 0, 100, 40), offscreen: true));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message
            .Should().Contain("el_7", "the agent has to know WHICH id it may not click")
            .And.Contain("off-screen", "and why - so it scrolls or focuses the window first");
    }

    [Fact]
    public void CentreOf_offscreen_refusal_points_at_interact_element_scroll_into_view_as_the_first_remedy()
    {
        // E-2: an off-screen element can now be brought into view in one call with no pointer
        // movement, so that is the first thing the refusal offers; wheel-scrolling or focusing the
        // window and re-snapshotting is the fallback for controls without ScrollItemPattern.
        var act = () => ElementTarget.CentreOf(Element(new Bounds(0, 0, 100, 40), offscreen: true));

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain("interact_element").And.Contain("scroll_into_view");
        var first = message.IndexOf("scroll_into_view", StringComparison.Ordinal);
        foreach (var other in new[] { "focus", "wheel", "scroll it", "`scroll`", "scroll tool" })
        {
            var at = message.IndexOf(other, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) first.Should().BeLessThan(at, $"scroll_into_view is offered before '{other}'");
        }
        message.Should().Contain("el_7").And.Contain("off-screen", "the existing contract still holds");
    }

    [Fact]
    public void CentreOf_refuses_an_element_with_no_bounds_naming_it_and_the_reason()
    {
        var act = () => ElementTarget.CentreOf(Element(null));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("el_7").And.Contain("no bounds");
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(0, 0)]
    [InlineData(-5, 10)]
    [InlineData(10, -5)]
    public void CentreOf_refuses_empty_bounds_naming_it_and_the_reason(int width, int height)
    {
        // A zero- or negative-extent rect has no interior: its "centre" is a point the element
        // does not occupy, and a click there hits whatever is underneath.
        var act = () => ElementTarget.CentreOf(Element(new Bounds(50, 50, width, height)));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("el_7").And.Contain("empty bounds");
    }

    [Fact]
    public void CentreOf_reports_off_screen_before_it_reports_missing_bounds()
    {
        // An off-screen element usually has no usable rect either; "off-screen" is the actionable
        // half of that pair, so the checks are ordered and the message is deterministic.
        var act = () => ElementTarget.CentreOf(Element(null, offscreen: true));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("off-screen").And.NotContain("no bounds");
    }

    [Fact]
    public void CentreOf_names_the_element_id_it_was_actually_given()
    {
        // Not a constant in the message: two refusals in one batch must be distinguishable (B-7).
        var act = () => ElementTarget.CentreOf(Element(null, id: "el_412"));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("el_412");
    }

    // ---- E-2 round 2 (C): a long name is clipped, so the remedy after it survives the cap --------

    public static TheoryData<string> Refusals => new() { "offscreen", "no_bounds", "empty_bounds" };

    private static ElementInfo Refused(string which, string name) => which switch
    {
        "offscreen" => Element(new Bounds(0, 0, 100, 40), offscreen: true, name: name),
        "no_bounds" => Element(null, name: name),
        _ => Element(new Bounds(50, 50, 0, 10), name: name),
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void CentreOf_clips_a_long_name_to_80_characters_in_every_refusal(string which)
    {
        var act = () => ElementTarget.CentreOf(Refused(which, new string('N', 3000)));

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain($"('{new string('N', 80)}...')").And.NotContain(new string('N', 81));
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void CentreOf_leaves_an_80_character_name_whole(string which)
    {
        var name = new string('N', 80);
        var act = () => ElementTarget.CentreOf(Refused(which, name));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain($"('{name}')");
    }

    [Fact]
    public void CentreOf_offscreen_remedy_survives_ToolErrors_MessageFor_with_a_3000_character_name()
    {
        var act = () => ElementTarget.CentreOf(Refused("offscreen", new string('N', 3000)));

        var ex = act.Should().Throw<InvalidOperationException>().Which;
        WindowsMcp.ToolErrors.MessageFor(ex).Should().Contain("interact_element scroll_into_view").And.Contain("new snapshot");
    }
}
