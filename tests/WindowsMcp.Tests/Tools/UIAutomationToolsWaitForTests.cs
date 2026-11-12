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
/// B-6 (R45-R63): the <c>wait_for</c> TOOL — the condition vocabulary the model is given, the
/// argument rules that fire before the service is touched, and the fact that a wait ALWAYS
/// answers with a structured result (roadmap C4). What the service then does with the request is
/// <c>WaitForServiceTests</c>; the verdicts themselves are <c>WaitConditionsTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class UIAutomationToolsWaitForTests
{
    private static readonly ElementInfo Saved =
        new("el_9", "Save", "Button", true, false, new Bounds(1, 2, 3, 4), null, null, null);

    /// <summary>A service that answers every wait with <paramref name="result"/> and records the request.</summary>
    private static Mock<IUIAutomationService> Waiting(WaitForResult? result = null, List<WaitRequest>? seen = null)
    {
        var mock = new Mock<IUIAutomationService>();
        mock.Setup(s => s.WaitForAsync(It.IsAny<WaitRequest>(), It.IsAny<CancellationToken>()))
            .Callback((WaitRequest r, CancellationToken _) => seen?.Add(r))
            .ReturnsAsync(result ?? new WaitForResult(true, "element_exists", 120, 1, "found 'Save' (el_9)", Saved));
        return mock;
    }

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ---- the old call shape still works, and means element_exists ----------------------------

    [Fact]
    public async Task WaitFor_with_only_a_text_waits_for_that_element_with_the_old_defaults()
    {
        // Roadmap C4: every call written before B-6 keeps its meaning. text stays first, the six
        // filters keep their names, order and defaults, and condition defaults to what the tool
        // has always done.
        var seen = new List<WaitRequest>();
        var mock = Waiting(seen: seen);

        await new UIAutomationTools(mock.Object).WaitFor("Ready");

        var request = seen.Should().ContainSingle().Subject;
        request.Condition.Should().Be(WaitCondition.ElementExists);
        request.Text.Should().Be("Ready");
        request.TimeoutMs.Should().Be(10000);
        request.IntervalMs.Should().Be(500);
        request.Kind.Should().Be(FindKind.Any);
        request.Scope.Should().Be(FindScope.Foreground);
        request.WindowTitle.Should().BeNull();
        request.IncludeOffscreen.Should().BeFalse();
        request.UseDom.Should().BeFalse();
    }

    [Fact]
    public async Task WaitFor_forwards_kind_scope_window_offscreen_and_the_two_budgets()
    {
        var seen = new List<WaitRequest>();
        var mock = Waiting(seen: seen);

        await new UIAutomationTools(mock.Object).WaitFor(
            "Ready", timeout_ms: 2000, interval_ms: 100, kind: "text", scope: "window",
            window: "Notepad", include_offscreen: true);

        var request = seen.Should().ContainSingle().Subject;
        request.TimeoutMs.Should().Be(2000);
        request.IntervalMs.Should().Be(100);
        request.Kind.Should().Be(FindKind.Text);
        request.Scope.Should().Be(FindScope.Window);
        request.WindowTitle.Should().Be("Notepad");
        request.IncludeOffscreen.Should().BeTrue();
        mock.Verify(s => s.WaitForAsync(It.IsAny<WaitRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitFor_keeps_the_scope_and_window_rules_find_element_uses()
    {
        var mock = Waiting();
        var tools = new UIAutomationTools(mock.Object);

        Func<Task> noTitle = () => tools.WaitFor("Ready", scope: "window");
        Func<Task> strayTitle = () => tools.WaitFor("Ready", scope: "desktop", window: "Notepad");

        await noTitle.Should().ThrowAsync<ArgumentException>().WithMessage("*requires window*");
        await strayTitle.Should().ThrowAsync<ArgumentException>().WithMessage("*only used with scope=window*");
        mock.VerifyNoOtherCalls();
    }

    // ---- the condition vocabulary -------------------------------------------------------------

    [Theory]
    [InlineData("element_exists", WaitCondition.ElementExists)]
    [InlineData("element", WaitCondition.ElementExists)]
    [InlineData("element_enabled", WaitCondition.ElementEnabled)]
    [InlineData("enabled", WaitCondition.ElementEnabled)]
    [InlineData("focused_element", WaitCondition.FocusedElement)]
    [InlineData("focused", WaitCondition.FocusedElement)]
    [InlineData("text_exists", WaitCondition.TextExists)]
    [InlineData("text", WaitCondition.TextExists)]
    [InlineData("active_window", WaitCondition.ActiveWindow)]
    [InlineData("window", WaitCondition.ActiveWindow)]
    [InlineData("ACTIVE_WINDOW", WaitCondition.ActiveWindow)]
    [InlineData("Text", WaitCondition.TextExists)]
    public async Task WaitFor_accepts_every_condition_name_and_upstream_alias(string condition, WaitCondition expected)
    {
        var seen = new List<WaitRequest>();

        await new UIAutomationTools(Waiting(seen: seen).Object).WaitFor("Ready", condition: condition);

        seen.Should().ContainSingle().Which.Condition.Should().Be(expected);
    }

    [Fact]
    public async Task WaitFor_rejects_an_unknown_condition_by_listing_the_ones_it_has()
    {
        // A rejection that does not enumerate the vocabulary leaves the model guessing a second time.
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("Ready", condition: "appears");

        var thrown = await act.Should().ThrowAsync<ArgumentException>();
        thrown.Which.Message.Should().Contain("appears")
            .And.Contain("element_exists").And.Contain("element_enabled").And.Contain("focused_element")
            .And.Contain("text_exists").And.Contain("active_window");
        mock.VerifyNoOtherCalls();   // an unparseable condition is refused before the service is asked to wait
    }

    [Fact]
    public async Task WaitFor_forwards_use_dom_for_the_conditions_that_read_the_page()
    {
        var seen = new List<WaitRequest>();

        await new UIAutomationTools(Waiting(seen: seen).Object)
            .WaitFor("Probe heading", condition: "text_exists", use_dom: true);

        var request = seen.Should().ContainSingle().Subject;
        request.UseDom.Should().BeTrue();
        request.Condition.Should().Be(WaitCondition.TextExists);
    }

    [Fact]
    public async Task WaitFor_accepts_use_dom_with_a_condition_that_ignores_it()
    {
        // Accepted and ignored, not refused: the flag is cheap and refusing it would make the
        // model choose between two spellings of the same wait.
        var seen = new List<WaitRequest>();

        await new UIAutomationTools(Waiting(seen: seen).Object)
            .WaitFor("Notepad", condition: "active_window", use_dom: true);

        seen.Should().ContainSingle().Which.UseDom.Should().BeTrue();
    }

    // ---- the result (roadmap C4) --------------------------------------------------------------

    [Fact]
    public async Task WaitFor_returns_the_whole_result_when_the_condition_is_satisfied()
    {
        var mock = Waiting(new WaitForResult(true, "element_exists", 1234, 3, "found 'Save' (el_9)", Saved));

        var json = Json(await new UIAutomationTools(mock.Object).WaitFor("Save"));

        json.GetProperty("Satisfied").GetBoolean().Should().BeTrue();
        json.GetProperty("Condition").GetString().Should().Be("element_exists");
        json.GetProperty("ElapsedMs").GetInt64().Should().Be(1234);
        json.GetProperty("Attempts").GetInt32().Should().Be(3);
        json.GetProperty("Detail").GetString().Should().Be("found 'Save' (el_9)");
        json.GetProperty("Element").GetProperty("ElementId").GetString().Should().Be("el_9",
            "the id is what the next call clicks with");
    }

    [Fact]
    public async Task WaitFor_returns_a_result_on_timeout_instead_of_the_string_null()
    {
        // THE contract break of section B (roadmap C4 / decision 2): "null" told the model nothing
        // about how long it waited or what was on screen instead, and read as an error.
        var mock = Waiting(new WaitForResult(false, "text_exists", 10000, 20, "no text matching 'Ready'"));

        var text = await new UIAutomationTools(mock.Object).WaitFor("Ready", condition: "text");

        text.Should().NotBe("null");
        var json = Json(text);
        json.GetProperty("Satisfied").GetBoolean().Should().BeFalse();
        json.GetProperty("Detail").GetString().Should().Be("no text matching 'Ready'");
        json.GetProperty("Attempts").GetInt32().Should().Be(20);
        json.TryGetProperty("Element", out var element).Should().BeFalse(
            "there is no element to report; a null Element would be noise on every timeout");
        _ = element;
    }

    // ---- the ranges, mirrored at the tool -----------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(120001)]
    public async Task WaitFor_refuses_a_timeout_outside_the_range_before_asking_the_service(int timeoutMs)
    {
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("Ready", timeout_ms: timeoutMs);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("timeout_ms").And.Contain("120000");
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    public async Task WaitFor_refuses_an_interval_outside_the_range_before_asking_the_service(int intervalMs)
    {
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("Ready", interval_ms: intervalMs);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("interval_ms").And.Contain("5000");
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WaitFor_refuses_a_blank_text_naming_the_condition(string text)
    {
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor(text, condition: "active_window");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("active_window");
        mock.VerifyNoOtherCalls();   // a wait on nothing would be satisfied by anything
    }

    [Theory]
    [InlineData("window", "active_window")]
    [InlineData("text", "text_exists")]
    [InlineData("focused", "focused_element")]
    [InlineData("enabled", "element_enabled")]
    [InlineData("element", "element_exists")]
    public async Task WaitFor_names_the_canonical_condition_when_an_alias_asked_for_nothing(
        string alias, string canonical)
    {
        // The alias is accepted, but the message teaches the canonical name - otherwise the model
        // learns "window" from its own mistake and carries the short form into the next call.
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("  ", condition: alias);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain(canonical).And.Contain("needs text",
                "the alias was understood - the refusal is about the missing text, not the condition");
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WaitFor_refuses_an_unknown_condition_before_it_looks_at_the_text()
    {
        // Order matters for the diagnosis: with both wrong, the condition is the one that has to
        // be reported, because there is no condition to name a text requirement for.
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("", condition: "appears");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("appears").And.NotContain("needs text");
        mock.VerifyNoOtherCalls();
    }

    // ---- the description IS the spec the model reads -------------------------------------------

    [Fact]
    public void WaitFor_describes_the_five_conditions_their_aliases_and_the_result_shape()
    {
        var description = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.WaitFor))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("element_exists").And.Contain("element_enabled").And.Contain("focused_element")
            .And.Contain("text_exists").And.Contain("active_window")
            .And.Contain("use_dom")
            .And.ContainEquivalentOf("satisfied", "the result shape is what the model keys on")
            .And.ContainEquivalentOf("detail");
        description.Should().NotContain("'null'",
            "the null-on-timeout contract is gone; a description that still promises it is a lie");
    }

    [Fact]
    public void WaitFor_describes_a_timeout_as_an_outcome_rather_than_an_error()
    {
        var description = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.WaitFor))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().ContainEquivalentOf("timeout")
            .And.ContainEquivalentOf("not an error",
                "a model that reads a timeout as a failure retries instead of acting on the detail");
    }

    [Fact]
    public void WaitFor_describes_its_two_new_parameters()
    {
        var parameters = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.WaitFor))!.GetParameters();

        var condition = parameters.Single(p => p.Name == "condition");
        var useDom = parameters.Single(p => p.Name == "use_dom");

        condition.GetCustomAttribute<DescriptionAttribute>()!.Description
            .Should().Contain("element_exists").And.Contain("active_window");
        useDom.GetCustomAttribute<DescriptionAttribute>()!.Description
            .Should().ContainEquivalentOf("page", "use_dom is about the browser page, not the window");
        condition.HasDefaultValue.Should().BeTrue();
        condition.DefaultValue.Should().Be("element_exists");
        useDom.DefaultValue.Should().Be(false);
    }

    [Fact]
    public void WaitFor_keeps_its_nine_pre_E4_parameters_in_order_and_appends_the_four_find_filters()
    {
        // Callers pass text positionally and the schema order is what the model copies: the seven
        // original parameters first, then B-6's condition and use_dom, then E-4's four find
        // filters, each addition on the END.
        var names = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.WaitFor))!
            .GetParameters().Select(p => p.Name).ToArray();

        names.Should().Equal(
            "text", "timeout_ms", "interval_ms", "kind", "scope", "window", "include_offscreen",
            "condition", "use_dom",
            "automation_id", "control_type", "class_name", "within");
    }

    // ---- E-4 (RED): the four find filters on the element conditions ---------------------------
    // element_exists and element_enabled run a find every poll, so they take find_element's
    // automation_id / control_type / class_name / within. The other three conditions never search
    // elements (a snapshot, or the window list), so a filter there would be silently ignored: it is
    // refused, in the tool's existing mismatch style ("window is only used with scope=window").

    [Theory]
    [InlineData("element_exists", WaitCondition.ElementExists)]
    [InlineData("enabled", WaitCondition.ElementEnabled)]
    public async Task WaitFor_forwards_the_four_find_filters_for_the_element_conditions(string condition, WaitCondition expected)
    {
        var seen = new List<WaitRequest>();

        await new UIAutomationTools(Waiting(seen: seen).Object).WaitFor(
            "Save", condition: condition, automation_id: "btnSave", control_type: "button", class_name: "Button", within: "el_3");

        var request = seen.Should().ContainSingle().Subject;
        request.Condition.Should().Be(expected);
        (request.AutomationId, request.ControlType, request.ClassName, request.Within)
            .Should().Be(("btnSave", "button", "Button", "el_3"));
        request.Scope.Should().Be(FindScope.Foreground);
        request.WindowTitle.Should().BeNull();
    }

    [Fact]
    public async Task WaitFor_without_filters_sends_none()
    {
        var seen = new List<WaitRequest>();

        await new UIAutomationTools(Waiting(seen: seen).Object).WaitFor("Save");

        var request = seen.Should().ContainSingle().Subject;
        (request.AutomationId, request.ControlType, request.ClassName, request.Within)
            .Should().Be(((string?)null, (string?)null, (string?)null, (string?)null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task WaitFor_treats_blank_filters_as_none(string blank)
    {
        // Model clients send "" for what they do not use; a blank within must not conflict with a
        // scope, and a blank filter must not reach the service as a filter.
        var seen = new List<WaitRequest>();

        await new UIAutomationTools(Waiting(seen: seen).Object).WaitFor(
            "Save", scope: "desktop", automation_id: blank, control_type: blank, class_name: blank, within: blank);

        var request = seen.Should().ContainSingle().Subject;
        (request.AutomationId, request.ControlType, request.ClassName, request.Within)
            .Should().Be(((string?)null, (string?)null, (string?)null, (string?)null));
        request.Scope.Should().Be(FindScope.Desktop);
    }

    [Theory]
    [InlineData("text_exists", "automation_id")]
    [InlineData("text_exists", "control_type")]
    [InlineData("text_exists", "class_name")]
    [InlineData("text_exists", "within")]
    [InlineData("focused_element", "automation_id")]
    [InlineData("focused_element", "within")]
    [InlineData("active_window", "class_name")]
    [InlineData("window", "within")]          // an alias resolves first, then the rule applies
    public async Task WaitFor_refuses_a_find_filter_with_a_condition_that_does_not_search_elements(string condition, string parameter)
    {
        var mock = Waiting();
        var tools = new UIAutomationTools(mock.Object);

        Func<Task> act = () => parameter switch
        {
            "automation_id" => tools.WaitFor("Ready", condition: condition, automation_id: "btnSave"),
            "control_type" => tools.WaitFor("Ready", condition: condition, control_type: "Button"),
            "class_name" => tools.WaitFor("Ready", condition: condition, class_name: "Button"),
            _ => tools.WaitFor("Ready", condition: condition, within: "el_3"),
        };

        var ex = (await act.Should().ThrowAsync<ArgumentException>(
            "a filter the condition would silently ignore is refused, like window without scope=window")).Which;
        ex.ParamName.Should().Be(parameter);
        ex.Message.Should().Contain(parameter, "the refusal names the argument")
            .And.Contain("element_exists", "and the conditions it IS used with");
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("desktop", null)]
    [InlineData("window", "Notepad")]
    public async Task WaitFor_refuses_within_with_a_non_default_scope(string scope, string? window)
    {
        // E-4 T2 for wait_for: within names the subtree, so a scope or a window title beside it
        // is a conflict, not a refinement.
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("Save", scope: scope, window: window, within: "el_3");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("within");
        mock.VerifyNoOtherCalls();
    }

    // GREEN (coordinator decision 5): an element wait may have empty text when a PROPERTY filter
    // names the control; within alone does not name one.

    [Theory]
    [InlineData("automation_id")]
    [InlineData("control_type")]
    [InlineData("class_name")]
    public async Task WaitFor_accepts_empty_text_for_an_element_condition_with_a_property_filter(string parameter)
    {
        var seen = new List<WaitRequest>();
        var tools = new UIAutomationTools(Waiting(seen: seen).Object);

        await (parameter switch
        {
            "automation_id" => tools.WaitFor("", automation_id: "btnSave"),
            "control_type" => tools.WaitFor("", condition: "element_enabled", control_type: "Button"),
            _ => tools.WaitFor("", class_name: "Button"),
        });

        seen.Should().ContainSingle();
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("\t")]
    public async Task WaitFor_accepts_whitespace_text_with_a_property_filter_like_empty_text(string text)
    {
        // R7: whitespace-only text is no text; the service then matches by the property alone.
        var seen = new List<WaitRequest>();

        await new UIAutomationTools(Waiting(seen: seen).Object).WaitFor(text, automation_id: "btnSave");

        seen.Should().ContainSingle().Which.AutomationId.Should().Be("btnSave");
    }

    // ---- R10: text is optional in the schema ----------------------------------------------------

    [Fact]
    public void WaitFor_text_is_optional_and_defaults_to_empty()
    {
        var text = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.WaitFor))!
            .GetParameters().Single(p => p.Name == "text");

        text.HasDefaultValue.Should().BeTrue("a schema-validating client must be able to send a property-only wait");
        text.DefaultValue.Should().Be("");
    }

    [Theory]
    [InlineData("control_type")]
    [InlineData("class_name")]
    [InlineData("automation_id")]
    public void WaitFor_property_filter_descriptions_say_text_may_be_empty_with_them(string name)
    {
        var d = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.WaitFor))!
            .GetParameters().Single(p => p.Name == name).GetCustomAttribute<DescriptionAttribute>()!.Description;

        d.Should().ContainEquivalentOf("text").And.ContainEquivalentOf("empty");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task WaitFor_with_only_within_still_needs_text(string text)
    {
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor(text, within: "el_3");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("needs text");
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WaitFor_a_blank_property_filter_does_not_lift_the_text_requirement()
    {
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("", automation_id: "  ");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("needs text");
        mock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WaitFor_refuses_within_with_a_non_default_scope_naming_within()
    {
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("Save", scope: "desktop", within: "el_3");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("within");
        mock.VerifyNoOtherCalls();
    }

    // ---- PR #42 review Q1: a within conflict is reported as within, whatever else is wrong ------

    public static TheoryData<string, string?> WithinConflicts => new()
    {
        { "desktop", null },
        { "DESKTOP", null },
        { "Desktop", "Notepad" },
        { "window", "Notepad" },
        { "WINDOW", "Notepad" },
        { "foreground", "Notepad" },   // a window title without scope=window
        { "FOREGROUND", "Notepad" },
        { "window", null },            // scope=window with no window
        { "window", "   " },           // a blank window is no window
        { "everywhere", null },        // an unknown scope
        { "everywhere", "Notepad" },
    };

    [Theory]
    [MemberData(nameof(WithinConflicts))]
    public async Task WaitFor_reports_a_within_conflict_as_within_whatever_else_is_wrong_with_the_target(string scope, string? window)
    {
        var mock = Waiting();

        Func<Task> act = () => new UIAutomationTools(mock.Object).WaitFor("Save", scope: scope, window: window, within: "el_3");

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("within");
        ex.Message.Should().Contain("within");
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("foreground", null)]
    [InlineData("Foreground", "")]
    [InlineData("FOREGROUND", "   ")]
    public async Task WaitFor_accepts_within_with_the_default_scope_and_a_blank_window(string scope, string? window)
    {
        var seen = new List<WaitRequest>();

        await new UIAutomationTools(Waiting(seen: seen).Object).WaitFor("Save", scope: scope, window: window, within: "el_3");

        var r = seen.Should().ContainSingle().Subject;
        (r.Scope, r.WindowTitle, r.Within).Should().Be((FindScope.Foreground, (string?)null, "el_3"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WaitFor_with_a_blank_within_keeps_every_existing_target_refusal(string? within)
    {
        var mock = Waiting();
        var tools = new UIAutomationTools(mock.Object);

        Func<Task> noTitle = () => tools.WaitFor("Save", scope: "window", within: within);
        Func<Task> strayTitle = () => tools.WaitFor("Save", scope: "desktop", window: "Notepad", within: within);
        Func<Task> titleWithoutScope = () => tools.WaitFor("Save", window: "Notepad", within: within);
        Func<Task> unknownScope = () => tools.WaitFor("Save", scope: "everywhere", within: within);

        await noTitle.Should().ThrowAsync<ArgumentException>().WithMessage("*requires window*");
        await strayTitle.Should().ThrowAsync<ArgumentException>().WithMessage("*only used with scope=window*");
        await titleWithoutScope.Should().ThrowAsync<ArgumentException>().WithMessage("*only used with scope=window*");
        await unknownScope.Should().ThrowAsync<ArgumentException>().WithMessage("*everywhere*");
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("automation_id")]
    [InlineData("control_type")]
    [InlineData("class_name")]
    [InlineData("within")]
    public void WaitFor_new_filter_descriptions_are_not_stubs_and_say_which_conditions_use_them(string name)
    {
        var d = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.WaitFor))!
            .GetParameters().Single(p => p.Name == name).GetCustomAttribute<DescriptionAttribute>()!.Description;

        d.Should().NotBeNullOrWhiteSpace();
        d.Should().NotContainEquivalentOf("not implemented", "a stub description would ship as the schema");
        d.Should().Contain("element_exists", "the model must learn the filter is for the element conditions");
    }

    [Fact]
    public void WaitFor_filters_default_to_null()
    {
        var parameters = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.WaitFor))!.GetParameters();

        foreach (var name in new[] { "automation_id", "control_type", "class_name", "within" })
        {
            var p = parameters.Single(x => x.Name == name);
            p.HasDefaultValue.Should().BeTrue(name);
            p.DefaultValue.Should().BeNull(name);
        }
    }
}
