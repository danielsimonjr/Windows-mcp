using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using FluentAssertions;
using Moq;
using Moq.Protected;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// D-10 GREEN round 3: <c>UIAutomationService.Refusing</c> and <c>InteractAsync</c>'s up-front
/// checks, driven through the REAL service with a FAKE element. <c>UiRefusalsTests</c> proves the
/// pure decisions (ProbeSaysGone, NeedsProbe, Translate's <c>targetIsAlive</c>);
/// <c>UIAutomationServiceRefusalIntegrationTests</c> proves the live Win32 behaviour, but only
/// for what a Win32 control can be made to do (disabled, destroyed). What neither reaches:
/// <list type="bullet">
/// <item>that the service passes <c>targetIsAlive: true</c> and consults <c>NeedsProbe</c> at all
/// (a mutation inside <c>Refusing</c> leaves every <c>UiRefusalsTests</c> row green);</item>
/// <item>a provider whose <c>IsEnabled</c> is NOT SUPPORTED (no live fixture has one);</item>
/// <item>a hung provider (<see cref="TimeoutException"/>) and an exited one (FlaUI's HRESULT-less
/// <see cref="Win32Exception"/>), which no fixture can be made to produce on demand.</item>
/// </list>
/// The element is a STRICT <c>Mock&lt;FrameworkAutomationElementBase&gt;</c> behind a real
/// <see cref="AutomationElement"/>: every property read goes through FlaUI's own
/// <c>TryGetPropertyValue</c> conversion, and any member the service touches that the row did not
/// arrange — a pattern lookup, a child search — throws, so "refused before any pattern call or
/// focus" is checked by construction. The fake is put into the service's id cache by reflection
/// (there is no public way to mint an id for an element the service did not walk). No desktop,
/// no window, no input: the input mock is strict too.
/// </summary>
[Trait("Category", "Unit")]
public sealed class UIAutomationServiceRefusingTests : IDisposable
{
    private const int ButtonControlTypeId = 50000;   // UIA_ButtonControlTypeId
    private const string ElementName = "Save";

    private readonly UIA3Automation _automation = new();
    private readonly Mock<IInputService> _input = new(MockBehavior.Strict);
    private readonly UIAutomationService _svc;

    public UIAutomationServiceRefusingTests()
    {
        _svc = new UIAutomationService(_input.Object, new Mock<IWindowService>().Object);
    }

    public void Dispose()
    {
        _svc.Dispose();
        _automation.Dispose();
    }

    /// <summary>A fake element: what each property read answers, and a count of every read and focus.</summary>
    private sealed class Fake
    {
        public required Mock<FrameworkAutomationElementBase> Framework { get; init; }
        public required AutomationElement Element { get; init; }
        public required string Id { get; init; }
        public int ProcessIdReads { get; set; }
        public int IsEnabledReads { get; set; }
        public int Focuses { get; set; }
    }

    /// <summary>The value a read answers "not supported" with.</summary>
    private object NotSupported => _automation.NotSupportedValue;

    /// <param name="isEnabled">What the IsEnabled read answers (a value, <see cref="NotSupported"/>), or throws.</param>
    /// <param name="processId">What the ProcessId read answers, or throws.</param>
    /// <param name="onFocus">What SetFocus does; null = succeeds.</param>
    /// <param name="name">The element's Name.</param>
    /// <param name="configure">Further members the row arranges (patterns, a child search).</param>
    /// <param name="issue">Put the element in the service's cache (false for a child the service finds itself).</param>
    private Fake Arrange(Func<object> isEnabled, Func<object>? processId = null, Action? onFocus = null,
        string name = ElementName, Action<Mock<FrameworkAutomationElementBase>>? configure = null, bool issue = true)
    {
        processId ??= () => 4242;
        var lib = _automation.PropertyLibrary.Element;
        var framework = new Mock<FrameworkAutomationElementBase>(MockBehavior.Strict, _automation);
        Fake? fake = null;

        object Read(int id)
        {
            if (id == lib.IsEnabled.Id) { fake!.IsEnabledReads++; return isEnabled(); }
            if (id == lib.ProcessId.Id) { fake!.ProcessIdReads++; return processId(); }
            if (id == lib.Name.Id) return name;
            if (id == lib.ControlType.Id) return ButtonControlTypeId;
            return NotSupported;
        }

        framework.Protected()
            .Setup<object>("InternalGetPropertyValue", ItExpr.IsAny<int>(), ItExpr.IsAny<bool>(), ItExpr.IsAny<bool>())
            .Returns((int id, bool _, bool _) => Read(id));
        framework.Setup(f => f.SetFocus()).Callback(() =>
        {
            fake!.Focuses++;
            onFocus?.Invoke();
        });

        configure?.Invoke(framework);

        var element = new AutomationElement(framework.Object);
        var cache = (ElementCache<AutomationElement>)typeof(UIAutomationService)
            .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_svc)!;
        fake = new Fake { Framework = framework, Element = element, Id = issue ? cache.Add(element) : "" };
        return fake;
    }

    /// <summary>A pattern the element does not offer: <c>PatternOrDefault</c> is null.</summary>
    private static IAutomationPattern<T> Absent<T>() where T : class, FlaUI.Core.Patterns.Infrastructure.IPattern
    {
        var pattern = new Mock<IAutomationPattern<T>>();
        pattern.SetupGet(p => p.PatternOrDefault).Returns((T)null!);
        pattern.SetupGet(p => p.IsSupported).Returns(false);
        return pattern.Object;
    }

    private Fake Disabled(Func<object>? processId = null) => Arrange(() => false, processId);

    // ---- R24a: an acting verb on a target that reports disabled is refused BEFORE the body -----

    public static TheoryData<string> ActingPaths => new()
    {
        "interact_click", "interact_invoke", "interact_toggle", "interact_select", "interact_select_item",
        "interact_focus", "interact_type", "focus",
    };

    private static string ActionOf(string path) => path switch
    {
        "interact_select_item" => "select",
        "focus" => "focus",
        _ => path["interact_".Length..],
    };

    private Task Act(string path, string id) => path switch
    {
        "interact_click" => _svc.InteractAsync(id, "click", null),
        "interact_invoke" => _svc.InteractAsync(id, "invoke", null),
        "interact_toggle" => _svc.InteractAsync(id, "toggle", null),
        "interact_select" => _svc.InteractAsync(id, "select", null),
        "interact_select_item" => _svc.InteractAsync(id, "select", "Apple"),
        "interact_focus" => _svc.InteractAsync(id, "focus", null),
        "interact_type" => _svc.InteractAsync(id, "type", "wmcp"),
        "focus" => _svc.FocusAsync(id),
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "no such path"),
    };

    [Theory]
    [MemberData(nameof(ActingPaths))]
    public async Task An_acting_verb_on_a_target_reporting_disabled_is_refused_before_any_pattern_call_or_focus(string path)
    {
        var fake = Disabled();

        Func<Task> act = () => Act(path, fake.Id);

        // A strict framework mock: had the body run, its first pattern lookup / Focus / child search
        // would have thrown a MockException (or, for focus, counted a SetFocus) instead.
        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Replace(ElementName, "<name>", StringComparison.Ordinal).Should().ContainEquivalentOf("disabled");
        ex.Message.Should().Contain(ActionOf(path), "the refusal names the action").And.Contain($"'{ElementName}'");
        fake.Focuses.Should().Be(0, "R24a: a disabled control is refused before Focus could move the foreground");
        _input.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(ActingPaths))]
    public async Task A_destroyed_target_that_answers_not_enabled_is_reported_gone_not_disabled(string path)
    {
        // A destroyed Win32 control answers every property with a default — IsEnabled false and
        // ProcessId 0 — so "disabled" would be a lie about a control that is not there.
        var fake = Disabled(processId: () => 0);

        Func<Task> act = () => Act(path, fake.Id);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain($"Element '{fake.Id}' is no longer available");
        ex.Message.Should().NotContainEquivalentOf("disabled");
        fake.Focuses.Should().Be(0);
    }

    // ---- R24a / GREEN check: "positively false" is the only thing the gate refuses ------------

    public static TheoryData<string> EnabledAnswers => new() { "true", "not-supported", "throws-gone", "throws-win32" };

    private Func<object> IsEnabledAnswer(string answer) => answer switch
    {
        "true" => () => true,
        "not-supported" => () => NotSupported,
        "throws-gone" => () => throw new FlaUI.Core.Exceptions.ElementNotAvailableException(),
        "throws-win32" => () => throw FlaUiShaped.From(FlaUiShaped.E_FAIL),
        _ => throw new ArgumentOutOfRangeException(nameof(answer), answer, "no such answer"),
    };

    [Theory]
    [MemberData(nameof(EnabledAnswers))]
    public async Task Focus_on_a_target_that_does_not_positively_report_disabled_is_not_refused(string answer)
    {
        // A provider that does not support IsEnabled (or cannot answer) is not refused on a guess:
        // the verb runs, and the provider's own answer — success here — is what the caller gets.
        var fake = Arrange(IsEnabledAnswer(answer));

        await _svc.FocusAsync(fake.Id);

        fake.IsEnabledReads.Should().Be(1, "precondition: the gate asked");
        fake.Focuses.Should().Be(1, "the gate let the verb through");
    }

    [Theory]
    [MemberData(nameof(EnabledAnswers))]
    public async Task Interact_focus_on_a_target_that_does_not_positively_report_disabled_runs_the_verb(string answer)
    {
        var fake = Arrange(IsEnabledAnswer(answer));

        var result = await _svc.InteractAsync(fake.Id, "focus", null);

        result.Method.Should().Be("Focus");
        result.ElementId.Should().Be(fake.Id);
        fake.Focuses.Should().Be(1);
    }

    [Fact]
    public async Task The_gate_reads_IsEnabled_once_and_does_not_probe_liveness_for_an_enabled_target()
    {
        // Every read is a cross-process call on the one STA thread every element tool shares.
        var fake = Arrange(() => true);

        await _svc.FocusAsync(fake.Id);

        fake.IsEnabledReads.Should().Be(1);
        fake.ProcessIdReads.Should().Be(0, "an enabled target that succeeded is never probed");
    }

    // ---- R23 through the service: a hung target is not probed ---------------------------------

    [Fact]
    public async Task A_timeout_reading_IsEnabled_propagates_and_the_verb_never_runs()
    {
        // The gate's own read hit a hung provider. Treating that as "not disabled" ran the verb
        // against the same provider and spent the UIA timeout a second time.
        var timeout = FlaUiShaped.From(FlaUiShaped.UIA_E_TIMEOUT);
        var fake = Arrange(() => throw timeout);

        Func<Task> act = () => _svc.FocusAsync(fake.Id);

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(timeout);
        fake.Focuses.Should().Be(0, "the verb must not run against a provider that just timed out");
        fake.ProcessIdReads.Should().Be(0, "and nothing else is read from it");
    }

    [Fact]
    public async Task A_timeout_from_the_body_is_rethrown_as_itself_without_reading_the_element_again()
    {
        var timeout = FlaUiShaped.From(FlaUiShaped.UIA_E_TIMEOUT);
        timeout.Should().BeOfType<TimeoutException>("precondition: FlaUI turns UIA_E_TIMEOUT into a TimeoutException");
        var fake = Arrange(() => true, onFocus: () => throw timeout);

        Func<Task> act = () => _svc.FocusAsync(fake.Id);

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(timeout);
        fake.ProcessIdReads.Should().Be(0,
            "R23: every further read of a hung target costs the UIA timeout again on the shared STA thread");
    }

    // ---- R20 through the service: a provider whose process exited ------------------------------

    [Fact]
    public async Task A_body_failure_whose_liveness_read_cannot_complete_is_reported_gone()
    {
        // The exited-process shape: the body and the ProcessId read both throw FlaUI's HRESULT-less
        // Win32Exception. Before R20 this reached the caller masked.
        var fake = Arrange(() => true,
            processId: () => throw FlaUiShaped.From(FlaUiShaped.RPC_S_SERVER_UNAVAILABLE),
            onFocus: () => throw FlaUiShaped.From(FlaUiShaped.RPC_S_SERVER_UNAVAILABLE));

        Func<Task> act = () => _svc.FocusAsync(fake.Id);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain($"Element '{fake.Id}' is no longer available");
        ex.InnerException.Should().BeOfType<Win32Exception>("the body's failure is kept for the log");
    }

    [Fact]
    public async Task The_same_Win32Exception_from_a_live_target_stays_masked()
    {
        // The probe says alive (a pid), so the HRESULT-less fault is not evidence of anything: it is
        // rethrown as it is and the SDK masks it, rather than being dressed up as a refusal.
        var thrown = FlaUiShaped.From(FlaUiShaped.RPC_S_SERVER_UNAVAILABLE);
        var fake = Arrange(() => true, onFocus: () => throw thrown);

        Func<Task> act = () => _svc.FocusAsync(fake.Id);

        (await act.Should().ThrowAsync<Win32Exception>()).Which.Should().BeSameAs(thrown);
        fake.ProcessIdReads.Should().Be(1, "the probe ran once and said alive");
    }

    // ---- R22 through the service: a live target is not the one that went away ------------------

    [Fact]
    public async Task Element_not_available_from_the_body_of_a_live_target_blames_a_part_of_it()
    {
        var fake = Arrange(() => true, onFocus: () => throw new FlaUI.Core.Exceptions.ElementNotAvailableException());

        Func<Task> act = () => _svc.FocusAsync(fake.Id);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().NotContain($"Element '{fake.Id}' is no longer available",
            "R22: the service just probed el_N and it is alive");
        ex.Message.Should().Contain(fake.Id).And.Contain($"'{ElementName}'").And.ContainEquivalentOf("no longer available");
        ex.Message.Should().MatchRegex("(?i)\\b(item|part|child)\\b");
    }

    [Fact]
    public async Task Element_not_enabled_from_the_body_of_a_target_that_reported_enabled_says_the_control_or_the_item()
    {
        // The gate passed (IsEnabled true), the provider then refused: the disabled thing is the
        // control or a part of it, and the message must not claim more than that.
        var fake = Arrange(() => true, onFocus: () => throw new FlaUI.Core.Exceptions.ElementNotEnabledException());

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "focus", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().ContainEquivalentOf("or the item being acted on");
        ex.Message.Should().Contain("focus");
        ex.InnerException.Should().BeOfType<FlaUI.Core.Exceptions.ElementNotEnabledException>();
    }

    // ---- R22 through the service: select with a value, the ITEM is the one that fails ----------

    /// <summary>
    /// A combo (the target, alive and enabled, no ExpandCollapse) whose descendant search finds
    /// <paramref name="item"/>; the item offers neither SelectionItem nor Invoke.
    /// </summary>
    private Fake Combo(Fake? item) => Arrange(() => true, configure: f =>
    {
        f.Protected().Setup<IAutomationPattern<FlaUI.Core.Patterns.IExpandCollapsePattern>>("InitializeExpandCollapsePattern")
            .Returns(Absent<FlaUI.Core.Patterns.IExpandCollapsePattern>());
        f.Setup(x => x.FindFirst(It.IsAny<FlaUI.Core.Definitions.TreeScope>(), It.IsAny<FlaUI.Core.Conditions.ConditionBase>()))
            .Returns(item?.Element!);
    });

    private Fake PatternlessItem(Func<object> processId) => Arrange(() => true, processId, name: "Apple", issue: false,
        configure: f =>
        {
            f.Protected().Setup<IAutomationPattern<FlaUI.Core.Patterns.ISelectionItemPattern>>("InitializeSelectionItemPattern")
                .Returns(Absent<FlaUI.Core.Patterns.ISelectionItemPattern>());
            f.Protected().Setup<IAutomationPattern<FlaUI.Core.Patterns.IInvokePattern>>("InitializeInvokePattern")
                .Returns(Absent<FlaUI.Core.Patterns.IInvokePattern>());
        });

    [Fact]
    public async Task Select_with_a_value_whose_item_offers_no_pattern_names_the_item_not_the_container()
    {
        var combo = Combo(PatternlessItem(processId: () => 4242));

        Func<Task> act = () => _svc.InteractAsync(combo.Id, "select", "Apple");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("SelectionItemPattern").And.Contain("'Apple'", "the item that lacks the pattern is named");
        ex.Message.Should().NotContainEquivalentOf("no longer available");
    }

    [Fact]
    public async Task Select_with_a_value_whose_item_died_after_the_search_says_an_item_of_the_live_container_went_away()
    {
        var combo = Combo(PatternlessItem(processId: () => 0));

        Func<Task> act = () => _svc.InteractAsync(combo.Id, "select", "Apple");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain(combo.Id).And.Contain($"'{ElementName}'", "the container the caller named");
        ex.Message.Should().MatchRegex("(?i)\\b(item|part|child)\\b").And.ContainEquivalentOf("no longer available");
        ex.Message.Should().NotContain($"Element '{combo.Id}' is no longer available", "the container is alive");
        ex.Message.Should().NotContain("SelectionItemPattern", "a dead item's missing pattern is not the reason");
    }

    [Fact]
    public async Task Select_with_a_value_no_item_has_is_the_lookup_miss_naming_the_value()
    {
        var combo = Combo(item: null);

        Func<Task> act = () => _svc.InteractAsync(combo.Id, "select", "Apple");

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain("'Apple'");
        combo.ProcessIdReads.Should().Be(1, "liveness first: a dead combo has no children either");
    }

    // ---- AssertOnSta's liveness probe is IsGone (round 3 sibling of R20) -------------------------

    [Theory]
    [InlineData("exists")]
    [InlineData("enabled")]
    public async Task Assert_on_an_element_whose_liveness_read_cannot_complete_fails_as_no_longer_available(string state)
    {
        // Before round 3 the probe read ProcessId bare: the exited-process Win32Exception escaped
        // the "gone" catch (IsElementGone cannot recognise it) and reached the caller masked.
        var fake = Arrange(() => true, processId: () => throw FlaUiShaped.From(FlaUiShaped.RPC_E_DISCONNECTED));

        var result = await _svc.AssertElementAsync(fake.Id, state);

        result.Pass.Should().BeFalse();
        result.Observed.Should().Be("element no longer available");
    }

    [Theory]
    [InlineData("exists")]
    [InlineData("enabled")]
    public async Task Assert_on_an_element_reporting_pid_zero_fails_as_no_longer_available(string state)
    {
        var fake = Arrange(() => true, processId: () => 0);

        var result = await _svc.AssertElementAsync(fake.Id, state);

        result.Pass.Should().BeFalse();
        result.Observed.Should().Be("element no longer available");
    }

    [Theory]
    [InlineData("true", true, "enabled")]
    [InlineData("false", false, "disabled")]
    [InlineData("not-supported", true, "enabled")]   // UIA's documented default when the provider omits it
    public async Task Assert_enabled_on_a_live_element_reports_what_it_read(string answer, bool pass, string observed)
    {
        var fake = Arrange(answer == "false" ? () => false : IsEnabledAnswer(answer));

        var result = await _svc.AssertElementAsync(fake.Id, "enabled");

        result.Pass.Should().Be(pass);
        result.Observed.Should().Be(observed);
    }

    // ---- InteractAsync: errors about the CALL come before the element is looked at -------------

    [Fact]
    public async Task An_unknown_action_on_an_id_the_server_never_issued_is_the_action_error_not_the_lookup_miss()
    {
        Func<Task> act = () => _svc.InteractAsync("el_999999", "hover", null);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("action");
        ex.Message.Should().Contain("'hover'").And.Contain("click|invoke|toggle|select|focus|type");
    }

    [Fact]
    public async Task Type_without_a_value_on_an_id_the_server_never_issued_is_the_value_error_not_the_lookup_miss()
    {
        Func<Task> act = () => _svc.InteractAsync("el_999999", "type", null);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("value");
    }

    [Fact]
    public async Task An_unknown_action_on_a_disabled_control_is_still_an_unknown_action()
    {
        var fake = Disabled();

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "hover", null);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("action");
        fake.IsEnabledReads.Should().Be(0, "the element is not looked at for a call that is wrong on its face");
    }

    [Fact]
    public async Task Type_without_a_value_on_a_disabled_control_is_the_value_error()
    {
        var fake = Disabled();

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "type", null);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("value");
        fake.IsEnabledReads.Should().Be(0);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("invoke")]
    [InlineData("toggle")]
    [InlineData("select")]
    [InlineData("focus")]
    [InlineData("type")]
    [InlineData("FOCUS")]   // the action is case-insensitive, as InteractOnSta's switch always was
    [InlineData("Type")]
    public async Task Every_advertised_action_passes_the_up_front_check_and_reaches_the_id_lookup(string action)
    {
        Func<Task> act = () => _svc.InteractAsync("el_999999", action, "x");

        (await act.Should().ThrowAsync<KeyNotFoundException>(
            "the action is valid, so the next thing that can fail is the never-issued id")).Which.Message
            .Should().Contain("el_999999");
    }

    // ---- the action list cannot drift from the switch or the description ----------------------

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Windows-mcp.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the pin reads UIAutomationService.cs, so it must run from inside the repo");
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    [Fact]
    public void InteractActions_the_InteractOnSta_switch_and_the_tool_description_name_the_same_actions()
    {
        // InteractAsync refuses anything outside InteractActions BEFORE the element is looked at, so
        // an action added to the switch but not the list is unreachable, and one added to the list
        // but not the switch hits the switch's default only once a live element resolved. Pin all
        // three to one set.
        var list = (string[])typeof(UIAutomationService)
            .GetField("InteractActions", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        var source = RepoFile("src", "WindowsMcp", "Services", "UIAutomationService.cs");
        var start = source.IndexOf("private InteractStep InteractOnSta(", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "precondition: InteractOnSta is where the switch lives");
        var end = source.IndexOf("default:", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "precondition: the switch has a default");
        var cases = Regex.Matches(source[start..end], "case \"([a-z_]+)\":")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        var parameter = typeof(UIAutomationTools).GetMethod(nameof(UIAutomationTools.InteractElement))!
            .GetParameters().Single(p => p.Name == "action");
        var advertised = parameter.GetCustomAttribute<DescriptionAttribute>()!.Description
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        cases.Should().OnlyHaveUniqueItems();
        list.Should().BeEquivalentTo(cases, "every action InteractOnSta handles is accepted up front, and nothing else");
        list.Should().BeEquivalentTo(advertised, "the tool's action parameter advertises exactly the accepted set");
        list.Should().OnlyContain(a => a == a.ToLowerInvariant(), "the check compares against the lowered verb");
    }
}
