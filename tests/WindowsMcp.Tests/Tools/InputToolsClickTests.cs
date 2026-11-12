using System.ComponentModel;
using System.Reflection;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// B-4 (R4) and the shared element-target resolver every phase-2 verb inherits (roadmap C1).
/// <c>click</c> is where the resolver is built, so the exclusivity rules and the refusals are
/// pinned here in full; the other three verbs then only prove they go through the same door.
/// </summary>
[Trait("Category", "Unit")]
public class InputToolsClickTests
{
    private static Mock<IInputService> ClickingInput()
    {
        var input = new Mock<IInputService>();
        input.Setup(s => s.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int x, int y, MouseButton b, int c, CancellationToken _) => new ClickResult(x, y, b, c));
        return input;
    }

    // ---- coordinates: today's call shape, with the response the roadmap specifies -------------

    [Fact]
    public async Task Click_at_coordinates_clicks_there_and_reports_the_point_it_used()
    {
        var input = ClickingInput();

        var json = InputVerb.Json(await InputVerb.Tools(input).Click(x: 100, y: 200, button: "left", clicks: 2));

        input.Verify(s => s.ClickAsync(100, 200, MouseButton.Left, 2, It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Str(json, "action").Should().Be("click");
        InputVerb.Num(json, "x").Should().Be(100);
        InputVerb.Num(json, "y").Should().Be(200);
        InputVerb.Str(json, "button").Should().Be("left", "an enum ordinal tells the model nothing");
        InputVerb.Num(json, "clicks").Should().Be(2);
        InputVerb.Absent(json, "elementId").Should().BeTrue("no id was given");
        InputVerb.Absent(json, "name").Should().BeTrue();
    }

    [Theory]
    [InlineData("left", MouseButton.Left)]
    [InlineData("right", MouseButton.Right)]
    [InlineData("middle", MouseButton.Middle)]
    [InlineData("R", MouseButton.Right)]
    public async Task Click_forwards_the_button_it_was_given(string button, MouseButton expected)
    {
        var input = ClickingInput();

        var json = InputVerb.Json(await InputVerb.Tools(input).Click(x: 1, y: 2, button: button));

        input.Verify(s => s.ClickAsync(1, 2, expected, 1, It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Str(json, "button").Should().Be(expected.ToString().ToLowerInvariant());
    }

    [Fact]
    public async Task Click_rejects_an_unknown_button_with_a_clear_message()
    {
        var input = ClickingInput();

        var act = () => InputVerb.Tools(input).Click(x: 0, y: 0, button: "fourth");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("button");
        input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Click_passes_one_to_three_clicks_straight_through(int clicks)
    {
        var input = ClickingInput();

        var json = InputVerb.Json(await InputVerb.Tools(input).Click(x: 5, y: 6, clicks: clicks));

        input.Verify(s => s.ClickAsync(5, 6, MouseButton.Left, clicks, It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Num(json, "clicks").Should().Be(clicks);
    }

    // ---- clicks:0 = hover (upstream's Click(loc, clicks=0)) -----------------------------------

    [Fact]
    public async Task Click_with_zero_clicks_only_moves_the_pointer_and_says_it_hovered()
    {
        var input = ClickingInput();

        var json = InputVerb.Json(await InputVerb.Tools(input).Click(x: 30, y: 40, clicks: 0));

        input.Verify(s => s.HoverAsync(30, 40, 0, It.IsAny<CancellationToken>()), Times.Once);
        input.Verify(s => s.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "clicks:0 is upstream's hover alias - a button press would be a side effect the caller refused");
        InputVerb.Str(json, "action").Should().Be("hover");
        InputVerb.Num(json, "clicks").Should().Be(0);
        InputVerb.Num(json, "x").Should().Be(30);
        InputVerb.Num(json, "y").Should().Be(40);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-3)]
    public async Task Click_rejects_a_negative_click_count_by_name(int clicks)
    {
        var input = ClickingInput();

        var act = () => InputVerb.Tools(input).Click(x: 0, y: 0, clicks: clicks);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("clicks");
        input.VerifyNoOtherCalls();
    }

    // ---- element_id: the resolver (roadmap C1) ------------------------------------------------

    [Fact]
    public async Task Click_by_element_id_resolves_the_centre_and_clicks_it()
    {
        var input = ClickingInput();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_12", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element(x: 100, y: 200, width: 40, height: 20));

        var json = InputVerb.Json(await InputVerb.Tools(input, uia).Click(element_id: "el_12"));

        uia.Verify(s => s.GetElementAsync("el_12", It.IsAny<CancellationToken>()), Times.Once);
        input.Verify(s => s.ClickAsync(120, 210, MouseButton.Left, 1, It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Num(json, "x").Should().Be(120);
        InputVerb.Num(json, "y").Should().Be(210);
        InputVerb.Str(json, "elementId").Should().Be("el_12");
        InputVerb.Str(json, "name").Should().Be("Save",
            "the model asked for an id and gets back which control it actually hit");
    }

    [Fact]
    public async Task Click_by_element_id_with_zero_clicks_hovers_the_centre()
    {
        var input = ClickingInput();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_12", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element());

        var json = InputVerb.Json(await InputVerb.Tools(input, uia).Click(element_id: "el_12", clicks: 0));

        input.Verify(s => s.HoverAsync(120, 210, 0, It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Str(json, "action").Should().Be("hover");
        InputVerb.Str(json, "elementId").Should().Be("el_12");
    }

    [Fact]
    public async Task Click_refuses_an_offscreen_element_and_never_reaches_the_desktop()
    {
        var input = ClickingInput();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element(id: "el_5", offscreen: true));

        var act = () => InputVerb.Tools(input, uia).Click(element_id: "el_5");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("el_5").And.Contain("off-screen");
        input.VerifyNoOtherCalls();   // a click at the centre of an off-screen rect lands on another window
    }

    [Fact]
    public async Task Click_offscreen_refusal_offers_interact_element_scroll_into_view()
    {
        // E-2 neighbour: the refusal the caller actually sees from `click` names the one-call fix.
        var input = ClickingInput();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element(id: "el_5", offscreen: true));

        var act = () => InputVerb.Tools(input, uia).Click(element_id: "el_5");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("interact_element").And.Contain("scroll_into_view");
        input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Click_refuses_an_element_with_no_bounds_and_never_reaches_the_desktop()
    {
        var input = ClickingInput();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_99", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Boundless());

        var act = () => InputVerb.Tools(input, uia).Click(element_id: "el_99");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("el_99").And.Contain("no bounds");
        input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Click_lets_an_unknown_element_id_surface_as_the_lookup_threw_it()
    {
        // Element ids expire with the next snapshot (roadmap C5); "el_12 is gone, take another
        // snapshot" is the useful answer, so the lookup's own exception is not swallowed.
        var input = ClickingInput();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_404", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Unknown element id 'el_404'"));

        var act = () => InputVerb.Tools(input, uia).Click(element_id: "el_404");

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain("el_404");
        input.VerifyNoOtherCalls();
    }

    // ---- exclusivity: exactly one target (roadmap C1) -----------------------------------------

    [Fact]
    public async Task Click_with_both_coordinates_and_an_element_id_is_refused()
    {
        var input = ClickingInput();
        var uia = new Mock<IUIAutomationService>();

        var act = () => InputVerb.Tools(input, uia).Click(x: 1, y: 2, element_id: "el_12");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("element_id").And.Contain("coordinates").And.Contain("both",
                "guessing which of two conflicting targets was meant is how a click lands in the wrong window");
        input.VerifyNoOtherCalls();
        uia.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Click_with_no_target_at_all_is_refused_and_names_both_ways_to_give_one()
    {
        var input = ClickingInput();

        var act = () => InputVerb.Tools(input).Click();

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("element_id").And.Contain("coordinates");
        input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Click_with_only_x_is_refused()
    {
        var input = ClickingInput();

        var act = () => InputVerb.Tools(input).Click(x: 5);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("x").And.Contain("y");
        input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Click_with_only_y_is_refused()
    {
        var input = ClickingInput();

        var act = () => InputVerb.Tools(input).Click(y: 5);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("x").And.Contain("y");
        input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Click_at_coordinates_never_asks_the_ui_automation_service_anything()
    {
        var input = ClickingInput();
        var uia = new Mock<IUIAutomationService>(MockBehavior.Strict);

        await InputVerb.Tools(input, uia).Click(x: 1, y: 2);

        uia.VerifyNoOtherCalls();     // a coordinate click must not pay for a UIA round-trip
    }

    // ---- the advertised surface ---------------------------------------------------------------

    [Fact]
    public void Click_keeps_x_and_y_as_its_first_two_parameters()
    {
        // Positional compatibility: `click(x, y)` is how every existing caller and every example
        // in the docs writes it. The new parameters go after them.
        var parameters = typeof(InputTools).GetMethod(nameof(InputTools.Click))!.GetParameters();

        parameters.Take(3).Select(p => p.Name).Should().Equal("x", "y", "element_id");
        parameters[0].ParameterType.Should().Be(typeof(int?));
        parameters[1].ParameterType.Should().Be(typeof(int?));
        parameters.Should().OnlyContain(p => p.HasDefaultValue, "every parameter of click is optional now");
    }

    [Fact]
    public void Click_describes_the_element_id_target_and_the_hover_alias()
    {
        var description = typeof(InputTools).GetMethod(nameof(InputTools.Click))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("element_id", "the snapshot hands out ids; the description is what tells the model it can use one")
            .And.Contain("clicks")
            .And.ContainEquivalentOf("hover", "clicks:0 is the hover alias and is invisible unless it is described");
    }

    [Theory]
    [InlineData("R", MouseButton.Right)]
    [InlineData("MIDDLE", MouseButton.Middle)]
    public async Task Click_with_zero_clicks_still_echoes_the_button_it_parsed(string button, MouseButton expected)
    {
        // The hover branch builds its OWN response object, so it can drift from the click branch:
        // echoing the raw string back would answer "R" where the click branch answers "right", and
        // a caller keying off the field would see two different vocabularies from one tool.
        var input = ClickingInput();

        var json = InputVerb.Json(await InputVerb.Tools(input).Click(x: 3, y: 4, button: button, clicks: 0));

        InputVerb.Str(json, "button").Should().Be(expected.ToString().ToLowerInvariant());
        InputVerb.Str(json, "action").Should().Be("hover");
        input.Verify(s => s.HoverAsync(3, 4, 0, It.IsAny<CancellationToken>()), Times.Once);
    }
}
