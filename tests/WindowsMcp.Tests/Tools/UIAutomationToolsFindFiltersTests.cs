using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// E-4 (RED), the <c>find_element</c> TOOL: the six new arguments reach the service in ONE
/// <see cref="FindElementRequest"/> (never the positional overload, which cannot carry them), the
/// argument rules that fire before the service is asked, the result JSON the client reads, and the
/// description — the only spec the model sees. What the service then does with the request is
/// <c>UIAutomationServiceFindFiltersTests</c> (fake tree) and
/// <c>UIAutomationServiceFindFiltersIntegrationTests</c> (real UIA).
/// </summary>
[Trait("Category", "Unit")]
public class UIAutomationToolsFindFiltersTests
{
    private static Mock<IUIAutomationService> Finding(FindElementResult? result = null, List<FindElementRequest>? seen = null)
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.FindElementAsync(It.IsAny<FindElementRequest>(), It.IsAny<CancellationToken>()))
            .Callback((FindElementRequest r, CancellationToken _) => seen?.Add(r))
            .ReturnsAsync(result ?? new FindElementResult([]));
        // The positional overload cannot carry the six new arguments: reaching it is the defect, so
        // it fails the row with a message that says so instead of returning a null the JSON chokes on.
        mock.Setup(s => s.FindElementAsync(It.IsAny<string>(), It.IsAny<FindKind>(), It.IsAny<FindScope>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "E-4 test: find_element called the positional FindElementAsync overload; it must send one FindElementRequest"));
        return mock;
    }

    private static void VerifyPositionalNeverCalled(Mock<IUIAutomationService> mock) =>
        mock.Verify(s => s.FindElementAsync(It.IsAny<string>(), It.IsAny<FindKind>(), It.IsAny<FindScope>(),
            It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

    private static MethodInfo Method => typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.FindElement))!;

    private static string Description => Method.GetCustomAttribute<DescriptionAttribute>()!.Description;

    private static string ParameterDescription(string name) =>
        Method.GetParameters().Single(p => p.Name == name).GetCustomAttribute<DescriptionAttribute>()!.Description;

    // ---- T1: forwarding -------------------------------------------------------------------------

    [Fact]
    public async Task FindElement_forwards_every_argument_in_one_request()
    {
        var seen = new List<FindElementRequest>();
        var mock = Finding(seen: seen);

        await new UIAutomationTools(mock.Object).FindElement(
            "Save", kind: "interactive", include_offscreen: true, automation_id: "btnSave", control_type: "button",
            class_name: "Button", within: "el_3", max_results: 5, offset: 10);

        seen.Should().ContainSingle().Which.Should().Be(new FindElementRequest(
            Text: "Save", Kind: FindKind.Interactive, Scope: FindScope.Foreground, WindowTitle: null, IncludeOffscreen: true,
            AutomationId: "btnSave", ControlType: "button", ClassName: "Button", Within: "el_3", MaxResults: 5, Offset: 10));
        mock.Verify(s => s.FindElementAsync(It.IsAny<FindElementRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        VerifyPositionalNeverCalled(mock);
    }

    [Fact]
    public async Task FindElement_with_no_arguments_sends_the_default_request()
    {
        var seen = new List<FindElementRequest>();
        var mock = Finding(seen: seen);

        await new UIAutomationTools(mock.Object).FindElement();

        seen.Should().ContainSingle().Which.Should().Be(new FindElementRequest(),
            "text \"\", kind any, the foreground window, 20 results from offset 0, no filters");
        VerifyPositionalNeverCalled(mock);
    }

    [Fact]
    public async Task FindElement_forwards_window_scope_and_its_title_with_the_filters()
    {
        var seen = new List<FindElementRequest>();

        await new UIAutomationTools(Finding(seen: seen).Object).FindElement(
            scope: "window", window: "Notepad", automation_id: "15");

        var r = seen.Should().ContainSingle().Subject;
        (r.Scope, r.WindowTitle, r.AutomationId, r.Within).Should().Be((FindScope.Window, "Notepad", "15", (string?)null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindElement_treats_a_blank_within_as_no_within(string within)
    {
        // Model clients send "" for an argument they do not use (InteractAsync's precedent), and a
        // blank window is already "no window" (ParseTarget). A blank within must neither conflict
        // with a scope nor reach the service as an id.
        var seen = new List<FindElementRequest>();

        await new UIAutomationTools(Finding(seen: seen).Object).FindElement("x", scope: "desktop", within: within);

        var r = seen.Should().ContainSingle().Subject;
        r.Scope.Should().Be(FindScope.Desktop);
        r.Within.Should().BeNull();
    }

    [Theory]
    [InlineData("automation_id")]
    [InlineData("control_type")]
    [InlineData("class_name")]
    public async Task FindElement_treats_a_blank_filter_as_no_filter(string parameter)
    {
        var seen = new List<FindElementRequest>();
        var tools = new UIAutomationTools(Finding(seen: seen).Object);

        await (parameter switch
        {
            "automation_id" => tools.FindElement(automation_id: " "),
            "control_type" => tools.FindElement(control_type: " "),
            _ => tools.FindElement(class_name: " "),
        });

        var r = seen.Should().ContainSingle().Subject;
        (r.AutomationId, r.ControlType, r.ClassName).Should().Be(((string?)null, (string?)null, (string?)null));
    }

    // ---- T2: within replaces scope and window ---------------------------------------------------

    [Theory]
    [InlineData("desktop", null)]
    [InlineData("window", "Notepad")]
    public async Task FindElement_refuses_within_with_a_non_default_scope(string scope, string? window)
    {
        var mock = Finding();

        Func<Task> act = () => new UIAutomationTools(mock.Object).FindElement("x", scope: scope, window: window, within: "el_3");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("within");
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FindElement_refuses_within_with_a_non_default_scope_naming_within()
    {
        var mock = Finding();

        Func<Task> act = () => new UIAutomationTools(mock.Object).FindElement("x", scope: "desktop", within: "el_3");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("within");
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FindElement_sends_a_null_text_as_empty()
    {
        // An MCP client can send "text": null explicitly; the default "" does not apply then.
        var seen = new List<FindElementRequest>();

        await new UIAutomationTools(Finding(seen: seen).Object).FindElement(text: null!, automation_id: "btnSave");

        seen.Should().ContainSingle().Which.Text.Should().Be("");
    }

    [Fact]
    public async Task FindElement_refuses_within_with_a_window_title()
    {
        var mock = Finding();

        Func<Task> act = () => new UIAutomationTools(mock.Object).FindElement("x", window: "Notepad", within: "el_3");

        await act.Should().ThrowAsync<ArgumentException>();
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("foreground")]
    [InlineData("FOREGROUND")]
    public async Task FindElement_accepts_within_with_the_default_scope_spelled_out(string scope)
    {
        var seen = new List<FindElementRequest>();

        await new UIAutomationTools(Finding(seen: seen).Object).FindElement("x", scope: scope, within: "el_3");

        var r = seen.Should().ContainSingle().Subject;
        (r.Scope, r.Within, r.WindowTitle).Should().Be((FindScope.Foreground, "el_3", (string?)null));
    }

    // ---- P1 at the tool: max_results and offset bounds, before the service is asked ------------

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public async Task FindElement_refuses_max_results_outside_1_to_100(int maxResults)
    {
        var mock = Finding();

        Func<Task> act = () => new UIAutomationTools(mock.Object).FindElement(max_results: maxResults);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("max_results");
        ex.Message.Should().Contain("1").And.Contain("100");
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FindElement_refuses_a_negative_offset()
    {
        var mock = Finding();

        Func<Task> act = () => new UIAutomationTools(mock.Object).FindElement(offset: -1);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("offset");
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(100, 0)]
    [InlineData(20, 5000)]
    public async Task FindElement_forwards_max_results_and_offset_inside_the_bounds(int maxResults, int offset)
    {
        var seen = new List<FindElementRequest>();

        await new UIAutomationTools(Finding(seen: seen).Object).FindElement(max_results: maxResults, offset: offset);

        var r = seen.Should().ContainSingle().Subject;
        (r.MaxResults, r.Offset).Should().Be((maxResults, offset));
    }

    // ---- T4: the result JSON ---------------------------------------------------------------------

    private static readonly ElementInfo WithIds =
        new("el_4", "Save", "Button", true, false, new Bounds(1, 2, 3, 4), null, null, null,
            AutomationId: "btnSave", ClassName: "Button");

    private static readonly ElementInfo WithoutIds =
        new("el_5", "Search", "Edit", true, false, new Bounds(1, 2, 3, 4), "", null, null);

    [Fact]
    public async Task FindElement_returns_Matches_Total_Offset_and_Truncated()
    {
        var mock = Finding(new FindElementResult([WithIds, WithoutIds], Total: 7, Offset: 2, Truncated: true));

        using var doc = JsonDocument.Parse(await new UIAutomationTools(mock.Object).FindElement(offset: 2, max_results: 2));

        var root = doc.RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().Equal("Matches", "Total", "Offset", "Truncated");
        root.GetProperty("Matches").GetArrayLength().Should().Be(2);
        root.GetProperty("Total").GetInt32().Should().Be(7);
        root.GetProperty("Offset").GetInt32().Should().Be(2);
        root.GetProperty("Truncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task FindElement_reports_TotalIsLowerBound_only_when_the_count_was_cut_short()
    {
        var bounded = Finding(new FindElementResult([WithIds], Total: 5021, Offset: 0, Truncated: true, TotalIsLowerBound: true));

        using var doc = JsonDocument.Parse(await new UIAutomationTools(bounded.Object).FindElement());

        doc.RootElement.GetProperty("TotalIsLowerBound").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task FindElement_shows_AutomationId_and_ClassName_on_a_match_that_has_them_and_omits_them_otherwise()
    {
        var mock = Finding(new FindElementResult([WithIds, WithoutIds]));

        using var doc = JsonDocument.Parse(await new UIAutomationTools(mock.Object).FindElement());

        var matches = doc.RootElement.GetProperty("Matches");
        matches[0].GetProperty("AutomationId").GetString().Should().Be("btnSave");
        matches[0].GetProperty("ClassName").GetString().Should().Be("Button");
        matches[1].TryGetProperty("AutomationId", out _).Should().BeFalse();
        matches[1].TryGetProperty("ClassName", out _).Should().BeFalse();
    }

    // ---- T1: the signature and the description ----------------------------------------------------

    [Fact]
    public void FindElement_text_is_optional_and_defaults_to_empty()
    {
        var text = Method.GetParameters().Single(p => p.Name == "text");

        text.HasDefaultValue.Should().BeTrue();
        text.DefaultValue.Should().Be("");
    }

    [Fact]
    public void FindElement_keeps_its_five_parameters_first_and_appends_the_six_new_ones()
    {
        Method.GetParameters().Select(p => p.Name).Should().Equal(
            "text", "kind", "scope", "window", "include_offscreen",
            "automation_id", "control_type", "class_name", "within", "max_results", "offset");
    }

    [Fact]
    public void FindElement_max_results_defaults_to_20_and_offset_to_0()
    {
        var parameters = Method.GetParameters();

        parameters.Single(p => p.Name == "max_results").DefaultValue.Should().Be(20);
        parameters.Single(p => p.Name == "offset").DefaultValue.Should().Be(0);
    }

    [Fact]
    public void FindElement_description_names_every_new_argument_and_the_paging_fields()
    {
        foreach (var fragment in new[] { "automation_id", "control_type", "class_name", "within", "max_results", "offset", "Total", "Truncated" })
            Description.Should().Contain(fragment);
    }

    [Fact]
    public void FindElement_description_no_longer_promises_at_most_20_matches()
    {
        Description.Should().NotContainEquivalentOf("Returns at most 20 matches",
            "max_results and offset page through every match now; the cap is the page size");
    }

    [Fact]
    public void FindElement_automation_id_is_described_as_exact_and_case_sensitive()
    {
        var d = ParameterDescription("automation_id");

        d.Should().ContainEquivalentOf("exact");
        d.Should().ContainEquivalentOf("case-sensitive");
    }

    [Fact]
    public void FindElement_class_name_is_described_as_exact()
    {
        ParameterDescription("class_name").Should().ContainEquivalentOf("exact");
    }

    [Fact]
    public void FindElement_control_type_is_described_as_a_UIA_control_type_name_with_examples()
    {
        var d = ParameterDescription("control_type");

        d.Should().Contain("UIA");
        d.Should().Contain("Button", "an example of the vocabulary");
    }

    [Fact]
    public void FindElement_within_is_described_as_an_element_id_whose_subtree_replaces_scope_and_window()
    {
        // RE-PINNED (R2/R10): "subtree" became "descendants" — within searches inside the element,
        // never the element itself, and the description must not promise otherwise.
        var d = ParameterDescription("within");

        d.Should().Contain("el_");
        d.Should().ContainEquivalentOf("descendants");
        d.Should().Contain("scope").And.Contain("window");
    }

    [Fact]
    public void FindElement_description_says_within_searches_the_descendants_and_Total_can_be_a_lower_bound()
    {
        Description.Should().ContainEquivalentOf("descendants", "within searches inside the element, not the element itself (R2)");
        Description.Should().ContainEquivalentOf("lower bound", "Total is a lower bound when the search stopped early (R3)");
    }

    [Fact]
    public void FindElement_max_results_is_described_with_its_range()
    {
        ParameterDescription("max_results").Should().MatchRegex(@"1\s*(-|–|to)\s*100");
    }

    [Theory]
    [InlineData("automation_id")]
    [InlineData("control_type")]
    [InlineData("class_name")]
    [InlineData("within")]
    [InlineData("max_results")]
    [InlineData("offset")]
    public void FindElement_new_parameter_descriptions_are_not_stubs(string name)
    {
        var d = ParameterDescription(name);

        d.Should().NotBeNullOrWhiteSpace();
        d.Should().NotContainEquivalentOf("not implemented", "a stub description would ship as the schema");
    }
}
