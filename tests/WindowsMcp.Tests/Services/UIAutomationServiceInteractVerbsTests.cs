using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;
using FluentAssertions;
using Moq;
using Moq.Protected;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-2 (RED): <c>interact_element</c>'s four new verbs — <c>expand</c>, <c>collapse</c>,
/// <c>set_value</c>, <c>scroll_into_view</c> — and the call-level checks in front of them, driven
/// through the REAL <see cref="UIAutomationService"/> with a FAKE element, the
/// <c>UIAutomationServiceRefusingTests</c> harness: a <c>Mock&lt;FrameworkAutomationElementBase&gt;</c>
/// behind a real <see cref="AutomationElement"/>, put in the id cache by reflection. Pattern
/// PROPERTIES (ExpandCollapseState, RangeValue Minimum/Maximum/Value/IsReadOnly, IsOffscreen,
/// BoundingRectangle) are real <see cref="AutomationProperty{T}"/> objects over the fake framework,
/// so every read goes through FlaUI's own not-supported check and conversion, and a re-read after
/// the call sees whatever the pattern call changed. The input mock is STRICT: none of the four verbs
/// may move the pointer or type, and a SetFocus is counted.
/// <para>
/// The scroll_into_view refusal walks the element's ancestors through the service's
/// <c>ParentOf</c> seam (a fake element has no native parent); the walk itself — bounded, failure
/// tolerant, nearest scrollable wins, one id minted — is what these rows pin.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class UIAutomationServiceInteractVerbsTests : IDisposable
{
    // UIA control type ids.
    private const int ButtonType = 50000;
    private const int ListItemType = 50007;
    private const int ListType = 50008;
    private const int SliderType = 50015;
    private const int GroupType = 50026;
    private const int TreeItemType = 50024;
    private const int EditType = 50004;
    private const int PaneType = 50033;
    private const int DataGridType = 50028;

    private static readonly string[] TenVerbs =
        ["click", "invoke", "toggle", "select", "focus", "type", "expand", "collapse", "set_value", "scroll_into_view"];

    private readonly UIA3Automation _automation = new();
    private readonly Mock<IInputService> _input = new(MockBehavior.Strict);
    private readonly UIAutomationService _svc;

    public UIAutomationServiceInteractVerbsTests()
    {
        _svc = new UIAutomationService(_input.Object, new Mock<IWindowService>().Object);
    }

    public void Dispose()
    {
        _svc.Dispose();
        _automation.Dispose();
    }

    // ============================================================================================
    // Harness
    // ============================================================================================

    private sealed class Fake
    {
        public required Mock<FrameworkAutomationElementBase> Framework { get; init; }
        public required AutomationElement Element { get; init; }
        public required string Id { get; init; }
        public required string Name { get; init; }
        /// <summary>Pattern-property answers by UIA property id; anything absent is "not supported".</summary>
        public Dictionary<int, Func<object>> Props { get; } = new();
        public Dictionary<int, int> Reads { get; } = new();
        public int Focuses { get; set; }
        public int IsEnabledReads { get; set; }
        public int ProcessIdReads { get; set; }
        public int ReadsOf(PropertyId id) => Reads.TryGetValue(id.Id, out var n) ? n : 0;
    }

    private object NotSupported => _automation.NotSupportedValue;
    private IPropertyLibrary Lib => _automation.PropertyLibrary;

    private ElementCache<AutomationElement> Cache => (ElementCache<AutomationElement>)typeof(UIAutomationService)
        .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(_svc)!;

    /// <summary>
    /// A fake element. Every pattern the new verbs could plausibly look up is ABSENT unless the row
    /// offers it (<see cref="Offer{T}"/>); every property the row did not arrange is not supported.
    /// </summary>
    private Fake Arrange(string name = "Save", int controlType = ButtonType,
        Func<object>? isEnabled = null, Func<object>? processId = null, bool issue = true)
    {
        isEnabled ??= () => true;
        processId ??= () => 4242;
        var el = Lib.Element;
        var framework = new Mock<FrameworkAutomationElementBase>(MockBehavior.Strict, _automation);
        Fake? fake = null;

        object Read(int id)
        {
            fake!.Reads[id] = fake.Reads.TryGetValue(id, out var n) ? n + 1 : 1;
            if (id == el.IsEnabled.Id) { fake.IsEnabledReads++; return isEnabled(); }
            if (id == el.ProcessId.Id) { fake.ProcessIdReads++; return processId(); }
            if (id == el.Name.Id) return name;
            if (id == el.ControlType.Id) return controlType;
            return fake.Props.TryGetValue(id, out var answer) ? answer() : NotSupported;
        }

        framework.Protected()
            .Setup<object>("InternalGetPropertyValue", ItExpr.IsAny<int>(), ItExpr.IsAny<bool>(), ItExpr.IsAny<bool>())
            .Returns((int id, bool _, bool _) => Read(id));
        framework.Setup(f => f.SetFocus()).Callback(() => fake!.Focuses++);

        Absent<IExpandCollapsePattern>(framework, "InitializeExpandCollapsePattern");
        Absent<IRangeValuePattern>(framework, "InitializeRangeValuePattern");
        Absent<IValuePattern>(framework, "InitializeValuePattern");
        Absent<IScrollItemPattern>(framework, "InitializeScrollItemPattern");
        Absent<IScrollPattern>(framework, "InitializeScrollPattern");
        Absent<IInvokePattern>(framework, "InitializeInvokePattern");
        Absent<ITogglePattern>(framework, "InitializeTogglePattern");
        Absent<ISelectionItemPattern>(framework, "InitializeSelectionItemPattern");

        var element = new AutomationElement(framework.Object);
        fake = new Fake { Framework = framework, Element = element, Name = name, Id = issue ? Cache.Add(element) : "" };
        return fake;
    }

    private static void Absent<T>(Mock<FrameworkAutomationElementBase> framework, string initializer)
        where T : class, FlaUI.Core.Patterns.Infrastructure.IPattern
    {
        var pattern = new Mock<IAutomationPattern<T>>();
        pattern.SetupGet(p => p.PatternOrDefault).Returns((T)null!);
        pattern.SetupGet(p => p.IsSupported).Returns(false);
        T? none = null;
        pattern.Setup(p => p.TryGetPattern(out none)).Returns(false);
        pattern.SetupGet(p => p.Pattern).Throws(new FlaUI.Core.Exceptions.PatternNotSupportedException());
        framework.Protected().Setup<IAutomationPattern<T>>(initializer).Returns(pattern.Object);
    }

    /// <summary>The element offers <paramref name="instance"/> (the later setup wins over the absent default).</summary>
    private static void Offer<T>(Fake fake, string initializer, T instance)
        where T : class, FlaUI.Core.Patterns.Infrastructure.IPattern
    {
        var pattern = new Mock<IAutomationPattern<T>>();
        pattern.SetupGet(p => p.PatternOrDefault).Returns(instance);
        pattern.SetupGet(p => p.Pattern).Returns(instance);
        pattern.SetupGet(p => p.IsSupported).Returns(true);
        T? some = instance;
        pattern.Setup(p => p.TryGetPattern(out some)).Returns(true);
        fake.Framework.Protected().Setup<IAutomationPattern<T>>(initializer).Returns(pattern.Object);
    }

    private static AutomationProperty<T> Prop<T>(Fake fake, PropertyId id) => new(id, fake.Framework.Object);

    private static Func<object> Throws(Exception ex) => () => throw ex;

    // ---- ExpandCollapsePattern --------------------------------------------------------------------

    private sealed class ExpandCollapseFake
    {
        public required Mock<IExpandCollapsePattern> Pattern { get; init; }
        /// <summary>How many state reads had happened when Expand/Collapse was called (-1 = never called).</summary>
        public int StateReadsAtCall { get; set; } = -1;
    }

    /// <param name="initial">The state before the call.</param>
    /// <param name="after">What the state reads after Expand/Collapse; null = Expanded/Collapsed.</param>
    /// <param name="rereadThrows">After the call, the state read throws.</param>
    private ExpandCollapseFake OfferExpandCollapse(Fake fake, ExpandCollapseState initial,
        ExpandCollapseState? after = null, Exception? rereadThrows = null)
    {
        var id = Lib.ExpandCollapse.ExpandCollapseState;
        fake.Props[id.Id] = () => (int)initial;
        var mock = new Mock<IExpandCollapsePattern>();
        var result = new ExpandCollapseFake { Pattern = mock };
        mock.SetupGet(p => p.ExpandCollapseState).Returns(Prop<ExpandCollapseState>(fake, id));

        void Transition(ExpandCollapseState natural)
        {
            result.StateReadsAtCall = fake.ReadsOf(id);
            var next = after ?? natural;
            fake.Props[id.Id] = rereadThrows is null ? () => (int)next : Throws(rereadThrows);
        }

        mock.Setup(p => p.Expand()).Callback(() => Transition(ExpandCollapseState.Expanded));
        mock.Setup(p => p.Collapse()).Callback(() => Transition(ExpandCollapseState.Collapsed));
        Offer(fake, "InitializeExpandCollapsePattern", mock.Object);
        return result;
    }

    // ---- RangeValuePattern ------------------------------------------------------------------------

    /// <param name="minimum">A double, or <see cref="NotSupported"/>.</param>
    /// <param name="maximum">A double, or <see cref="NotSupported"/>.</param>
    /// <param name="snap">What the control's value becomes for a requested value; null = exactly that.</param>
    /// <param name="rereadThrows">After SetValue, the value read throws.</param>
    private Mock<IRangeValuePattern> OfferRange(Fake fake, object minimum, object maximum, double current = 0,
        bool readOnly = false, Func<double, double>? snap = null, Exception? rereadThrows = null)
    {
        var ids = Lib.RangeValue;
        fake.Props[ids.Minimum.Id] = () => minimum;
        fake.Props[ids.Maximum.Id] = () => maximum;
        fake.Props[ids.Value.Id] = () => current;
        fake.Props[ids.IsReadOnly.Id] = () => readOnly;
        var mock = new Mock<IRangeValuePattern>();
        mock.SetupGet(p => p.Minimum).Returns(Prop<double>(fake, ids.Minimum));
        mock.SetupGet(p => p.Maximum).Returns(Prop<double>(fake, ids.Maximum));
        mock.SetupGet(p => p.Value).Returns(Prop<double>(fake, ids.Value));
        mock.SetupGet(p => p.IsReadOnly).Returns(Prop<bool>(fake, ids.IsReadOnly));
        mock.SetupGet(p => p.SmallChange).Returns(Prop<double>(fake, ids.SmallChange));
        mock.SetupGet(p => p.LargeChange).Returns(Prop<double>(fake, ids.LargeChange));
        mock.Setup(p => p.SetValue(It.IsAny<double>())).Callback((double v) =>
        {
            var landed = snap?.Invoke(v) ?? v;
            fake.Props[ids.Value.Id] = rereadThrows is null ? () => landed : Throws(rereadThrows);
        });
        Offer(fake, "InitializeRangeValuePattern", mock.Object);
        return mock;
    }

    // ---- ValuePattern -----------------------------------------------------------------------------

    /// <param name="land">What the control's value becomes for a requested string; null = exactly that.</param>
    private Mock<IValuePattern> OfferValue(Fake fake, bool readOnly, string current = "old", Func<string, string>? land = null)
    {
        var ids = Lib.Value;
        fake.Props[ids.IsReadOnly.Id] = () => readOnly;
        fake.Props[ids.Value.Id] = () => current;
        var mock = new Mock<IValuePattern>();
        mock.SetupGet(p => p.IsReadOnly).Returns(Prop<bool>(fake, ids.IsReadOnly));
        mock.SetupGet(p => p.Value).Returns(Prop<string>(fake, ids.Value));
        mock.Setup(p => p.SetValue(It.IsAny<string>())).Callback((string v) =>
        {
            var landed = land is null ? v : land(v);
            fake.Props[ids.Value.Id] = () => landed;
        });
        Offer(fake, "InitializeValuePattern", mock.Object);
        return mock;
    }

    // ---- ScrollItemPattern / ScrollPattern ---------------------------------------------------------

    /// <summary>Offscreen with empty bounds before; <paramref name="onScroll"/> sets what is read after.</summary>
    private Mock<IScrollItemPattern> OfferScrollItem(Fake fake, Action? onScroll = null)
    {
        SetGeometry(fake, offscreen: true, 0, 0, 0, 0);
        var mock = new Mock<IScrollItemPattern>();
        mock.Setup(p => p.ScrollIntoView()).Callback(() => onScroll?.Invoke());
        Offer(fake, "InitializeScrollItemPattern", mock.Object);
        return mock;
    }

    private void SetGeometry(Fake fake, bool offscreen, double left, double top, double width, double height)
    {
        fake.Props[Lib.Element.IsOffscreen.Id] = () => offscreen;
        fake.Props[Lib.Element.BoundingRectangle.Id] = () => new[] { left, top, width, height };
    }

    /// <summary>
    /// The fake offers ScrollPattern. Round 2 (D): "scrollable" is the pattern AND an axis that
    /// scrolls (UiClassifier.IsScrollable), so the axis properties are real properties over the fake.
    /// </summary>
    private void Scrollable(Fake fake, bool vertical = true, bool horizontal = false)
    {
        var ids = Lib.Scroll;
        fake.Props[ids.VerticallyScrollable.Id] = () => vertical;
        fake.Props[ids.HorizontallyScrollable.Id] = () => horizontal;
        var mock = new Mock<IScrollPattern>();
        mock.SetupGet(p => p.VerticallyScrollable).Returns(Prop<bool>(fake, ids.VerticallyScrollable));
        mock.SetupGet(p => p.HorizontallyScrollable).Returns(Prop<bool>(fake, ids.HorizontallyScrollable));
        Offer(fake, "InitializeScrollPattern", mock.Object);
    }

    /// <summary>How many times the service asked the <c>ParentOf</c> seam for a parent.</summary>
    private int ParentCalls { get; set; }

    /// <summary>Points the service's parent walk at the given child → parent links (by reference).</summary>
    private void Parents(params (Fake Child, Fake? Parent)[] links)
    {
        _svc.ParentOf = e =>
        {
            ParentCalls++;
            foreach (var (child, parent) in links)
                if (ReferenceEquals(child.Element, e)) return parent?.Element;
            return null;
        };
    }

    private static IEnumerable<string> IdsIn(string message) => Regex.Matches(message, @"\bel_\d+\b").Select(m => m.Value);

    private void NoInputAndNoFocus(Fake fake)
    {
        fake.Focuses.Should().Be(0, "R8: none of the four verbs focuses the control");
        _input.VerifyNoOtherCalls();
    }

    // ============================================================================================
    // R1–R3: call-level checks, before the element is resolved
    // ============================================================================================

    [Fact]
    public async Task R1_Unknown_action_lists_the_ten_verbs_in_order()
    {
        Func<Task> act = () => _svc.InteractAsync("el_999999", "hover", null);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("action");
        ex.Message.Should().Contain("'hover'")
            .And.Contain("expected click|invoke|toggle|select|focus|type|expand|collapse|set_value|scroll_into_view");
    }

    [Fact]
    public void R1_InteractActions_is_exactly_the_ten_verbs_in_order()
    {
        var list = (string[])typeof(UIAutomationService)
            .GetField("InteractActions", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        list.Should().Equal(TenVerbs);
    }

    [Theory]
    [InlineData("expand", null)]
    [InlineData("collapse", null)]
    [InlineData("set_value", "5")]
    [InlineData("set_value", "")]
    [InlineData("scroll_into_view", null)]
    [InlineData("EXPAND", null)]   // case-insensitive, as every other verb
    [InlineData("Collapse", null)]
    [InlineData("Set_Value", "abc")]   // a bad number is the element's business, not the call's
    [InlineData("SCROLL_INTO_VIEW", null)]
    public async Task R1_Each_new_verb_passes_the_up_front_check_and_reaches_the_id_lookup(string action, string? value)
    {
        Func<Task> act = () => _svc.InteractAsync("el_999999", action, value);

        (await act.Should().ThrowAsync<KeyNotFoundException>(
            "the call is well-formed, so the next thing that can fail is the never-issued id")).Which.Message
            .Should().Contain("el_999999");
    }

    [Fact]
    public async Task R2_Set_value_without_a_value_is_the_value_error_not_the_lookup_miss()
    {
        Func<Task> act = () => _svc.InteractAsync("el_999999", "set_value", null);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("value");
        ex.Message.Should().Contain("'set_value' requires a value");
    }

    [Fact]
    public async Task R2_Set_value_without_a_value_on_a_disabled_control_never_reads_the_element()
    {
        var fake = Arrange(isEnabled: () => false);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "SET_VALUE", null);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("value");
        fake.IsEnabledReads.Should().Be(0, "a call that is wrong on its face is refused before the element is looked at");
    }

    [Theory]
    [InlineData("expand", "x")]
    [InlineData("collapse", "x")]
    [InlineData("scroll_into_view", "x")]
    // Round 2 (G) re-pin: "" is accepted as "no value" (model clients send it), so the "" rows moved
    // to G_An_empty_value_is_no_value; whitespace is still a value.
    [InlineData("expand", " ")]
    [InlineData("Scroll_Into_View", "	")]
    [InlineData("collapse", "  ")]
    public async Task R3_A_verb_that_takes_no_value_refuses_one_before_the_id_lookup(string action, string value)
    {
        Func<Task> act = () => _svc.InteractAsync("el_999999", action, value);

        var ex = (await act.Should().ThrowAsync<ArgumentException>(
            "a stray value is an error about the call, so it outranks the unknown id")).Which;
        ex.ParamName.Should().Be("value");
        ex.Message.Should().Contain(action.ToLowerInvariant())
            .And.MatchRegex("(?i)(takes no|does not take a|doesn't take a|without a|no) value");
    }

    [Theory]
    [InlineData("expand")]
    [InlineData("collapse")]
    [InlineData("scroll_into_view")]
    public async Task R3_A_stray_value_on_a_disabled_control_is_the_value_error(string action)
    {
        var fake = Arrange(isEnabled: () => false);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, action, "x");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("value");
        fake.IsEnabledReads.Should().Be(0);
    }

    // ============================================================================================
    // R4–R8: expand / collapse
    // ============================================================================================

    [Theory]
    [InlineData("expand")]
    [InlineData("collapse")]
    public async Task R4_No_ExpandCollapsePattern_is_the_D10_pattern_refusal(string verb)
    {
        var fake = Arrange("Documents", TreeItemType);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, verb, null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Be(UiRefusals.PatternNotSupported("ExpandCollapsePattern", "TreeItem", "Documents").Message);
        NoInputAndNoFocus(fake);
    }

    [Theory]
    [InlineData("expand")]
    [InlineData("collapse")]
    public async Task R4_No_ExpandCollapsePattern_on_a_dead_element_is_reported_gone(string verb)
    {
        var fake = Arrange("Documents", TreeItemType, processId: () => 0);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, verb, null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain($"Element '{fake.Id}' is no longer available");
        ex.Message.Should().NotContain("ExpandCollapsePattern");
    }

    [Theory]
    [InlineData("expand", ExpandCollapseState.Expanded, "already Expanded")]
    [InlineData("collapse", ExpandCollapseState.Collapsed, "already Collapsed")]
    public async Task R5_Already_in_the_requested_state_calls_nothing_and_says_so(string verb, ExpandCollapseState state, string detail)
    {
        var fake = Arrange("Documents", TreeItemType);
        var ec = OfferExpandCollapse(fake, state);

        var result = await _svc.InteractAsync(fake.Id, verb, null);

        result.ElementId.Should().Be(fake.Id);
        result.Action.Should().Be(verb);
        result.Method.Should().Be("ExpandCollapsePattern");
        result.Detail.Should().Be(detail);
        ec.Pattern.Verify(p => p.Expand(), Times.Never);
        ec.Pattern.Verify(p => p.Collapse(), Times.Never);
        NoInputAndNoFocus(fake);
    }

    [Theory]
    [InlineData("expand")]
    [InlineData("collapse")]
    public async Task R6_A_leaf_node_is_refused_naming_the_control_and_calls_nothing(string verb)
    {
        var fake = Arrange("readme.txt", TreeItemType);
        var ec = OfferExpandCollapse(fake, ExpandCollapseState.LeafNode);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, verb, null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain("'readme.txt'").And.Contain("TreeItem")
            .And.ContainEquivalentOf("leaf").And.Contain(verb, "it says there is nothing to " + verb);
        ec.Pattern.Verify(p => p.Expand(), Times.Never);
        ec.Pattern.Verify(p => p.Collapse(), Times.Never);
        NoInputAndNoFocus(fake);
    }

    [Theory]
    [InlineData("expand", ExpandCollapseState.Collapsed, null, "now Expanded")]
    [InlineData("expand", ExpandCollapseState.PartiallyExpanded, null, "now Expanded")]
    [InlineData("collapse", ExpandCollapseState.Expanded, null, "now Collapsed")]
    [InlineData("collapse", ExpandCollapseState.PartiallyExpanded, null, "now Collapsed")]
    // The Detail is the state RE-READ after the call, not the state asked for.
    [InlineData("expand", ExpandCollapseState.Collapsed, ExpandCollapseState.PartiallyExpanded, "now PartiallyExpanded")]
    [InlineData("collapse", ExpandCollapseState.Expanded, ExpandCollapseState.PartiallyExpanded, "now PartiallyExpanded")]
    public async Task R7_Otherwise_the_pattern_method_is_called_once_and_the_detail_is_the_re_read_state(
        string verb, ExpandCollapseState initial, ExpandCollapseState? after, string detail)
    {
        var fake = Arrange("Documents", TreeItemType);
        var ec = OfferExpandCollapse(fake, initial, after);

        var result = await _svc.InteractAsync(fake.Id, verb.ToUpperInvariant(), null);

        if (verb == "expand")
        {
            ec.Pattern.Verify(p => p.Expand(), Times.Once);
            ec.Pattern.Verify(p => p.Collapse(), Times.Never);
        }
        else
        {
            ec.Pattern.Verify(p => p.Collapse(), Times.Once);
            ec.Pattern.Verify(p => p.Expand(), Times.Never);
        }
        ec.StateReadsAtCall.Should().BeGreaterThan(0, "R5: the state is read BEFORE the pattern method is called");
        result.ElementId.Should().Be(fake.Id);
        result.Action.Should().Be(verb, "the result carries the lower-cased verb");
        result.Method.Should().Be("ExpandCollapsePattern");
        result.Detail.Should().Be(detail);
        NoInputAndNoFocus(fake);
    }

    [Theory]
    [InlineData("expand", ExpandCollapseState.Collapsed)]
    [InlineData("collapse", ExpandCollapseState.Expanded)]
    public async Task R7_A_re_read_that_fails_leaves_the_detail_null_and_the_call_succeeds(string verb, ExpandCollapseState initial)
    {
        var fake = Arrange("Documents", TreeItemType);
        OfferExpandCollapse(fake, initial, rereadThrows: FlaUiShaped.From(FlaUiShaped.E_FAIL));

        var result = await _svc.InteractAsync(fake.Id, verb, null);

        result.Method.Should().Be("ExpandCollapsePattern");
        result.Detail.Should().BeNull();
        NoInputAndNoFocus(fake);
    }

    // ============================================================================================
    // R9–R16: set_value
    // ============================================================================================

    private const double Min = -7.25;
    private const double Max = 180.5;

    /// <summary>A slider with a range AND a writable ValuePattern, so a fallback to the latter is visible.</summary>
    private (Fake Fake, Mock<IRangeValuePattern> Range, Mock<IValuePattern> Value) Slider(
        object? minimum = null, object? maximum = null, bool readOnly = false,
        Func<double, double>? snap = null, Exception? rereadThrows = null)
    {
        var fake = Arrange("Volume", SliderType);
        var range = OfferRange(fake, minimum ?? Min, maximum ?? Max, current: 10, readOnly, snap, rereadThrows);
        var value = OfferValue(fake, readOnly: false, current: "10");
        return (fake, range, value);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("3,5")]        // not a decimal comma (invariant), and not a thousands separator (Float has no AllowThousands)
    [InlineData("1,000")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e999")]      // overflows to +Infinity
    [InlineData("0x10")]
    [InlineData("12abc")]
    public async Task R9_A_value_that_is_not_a_finite_number_is_refused_naming_the_control_and_the_range(string value)
    {
        var (fake, range, valuePattern) = Slider();

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", value);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain("'Volume'").And.ContainEquivalentOf("number")
            .And.Contain("-7.25", "the range is quoted when it is known").And.Contain("180.5");
        range.Verify(p => p.SetValue(It.IsAny<double>()), Times.Never);
        valuePattern.Verify(p => p.SetValue(It.IsAny<string>()), Times.Never, "a range control never falls back to text");
        NoInputAndNoFocus(fake);
    }

    [Fact]
    public async Task R9_A_non_number_on_a_range_control_whose_range_is_unknown_is_still_refused()
    {
        var (fake, range, _) = Slider(minimum: NotSupported, maximum: NotSupported);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "loud");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("'Volume'").And.ContainEquivalentOf("number");
        range.Verify(p => p.SetValue(It.IsAny<double>()), Times.Never);
    }

    [Theory]
    [InlineData("-8")]
    [InlineData("-7.26")]
    [InlineData("181")]
    [InlineData("180.51")]
    [InlineData("1000")]
    public async Task R10_Out_of_range_is_refused_naming_minimum_maximum_and_the_rejected_value(string value)
    {
        var (fake, range, valuePattern) = Slider();

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", value);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain("-7.25").And.Contain("180.5").And.Contain(value);
        range.Verify(p => p.SetValue(It.IsAny<double>()), Times.Never);
        valuePattern.Verify(p => p.SetValue(It.IsAny<string>()), Times.Never);
        NoInputAndNoFocus(fake);
    }

    [Theory]
    [InlineData("-7.25", Min)]     // exactly the minimum
    [InlineData("180.5", Max)]     // exactly the maximum
    [InlineData("1.8050e2", Max)]  // Float: exponent allowed
    [InlineData(" 42 ", 42.0)]     // Float: leading/trailing white allowed
    [InlineData("+3", 3.0)]
    [InlineData("-0.5", -0.5)]
    [InlineData(".5", 0.5)]
    public async Task R10_R12_A_finite_number_within_the_inclusive_range_is_set_once(string value, double expected)
    {
        var (fake, range, valuePattern) = Slider();

        var result = await _svc.InteractAsync(fake.Id, "set_value", value);

        range.Verify(p => p.SetValue(expected), Times.Once);
        range.Verify(p => p.SetValue(It.IsAny<double>()), Times.Once);
        valuePattern.Verify(p => p.SetValue(It.IsAny<string>()), Times.Never);
        result.Method.Should().Be("RangeValuePattern");
        result.Action.Should().Be("set_value");
        NoInputAndNoFocus(fake);
    }

    [Fact]
    public async Task R11_A_read_only_range_is_refused_with_no_fallback_to_ValuePattern()
    {
        var (fake, range, valuePattern) = Slider(readOnly: true);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "50");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain("'Volume'").And.ContainEquivalentOf("read-only");
        range.Verify(p => p.SetValue(It.IsAny<double>()), Times.Never);
        valuePattern.Verify(p => p.SetValue(It.IsAny<string>()), Times.Never, "R11: no fallback to ValuePattern");
        NoInputAndNoFocus(fake);
    }

    [Fact]
    public async Task R12_The_detail_is_the_value_re_read_after_the_set_not_the_one_requested()
    {
        var fake = Arrange("Volume", SliderType);
        var range = OfferRange(fake, 0.0, 100.0, current: 10, snap: v => Math.Round(v / 20) * 20);   // 37 → 40

        var result = await _svc.InteractAsync(fake.Id, "set_value", "37");

        range.Verify(p => p.SetValue(37), Times.Once);
        result.Method.Should().Be("RangeValuePattern");
        result.Detail.Should().Be("now 40 (range 0 to 100)");
        NoInputAndNoFocus(fake);
    }

    [Fact]
    public async Task R12_A_re_read_that_fails_leaves_the_detail_null_and_the_set_still_happened()
    {
        var (fake, range, _) = Slider(rereadThrows: FlaUiShaped.From(FlaUiShaped.E_FAIL));

        var result = await _svc.InteractAsync(fake.Id, "set_value", "50");

        range.Verify(p => p.SetValue(50), Times.Once);
        result.Method.Should().Be("RangeValuePattern");
        result.Detail.Should().BeNull();
    }

    [Theory]
    [InlineData("min")]
    [InlineData("max")]
    [InlineData("both")]
    public async Task R13_A_range_whose_bounds_are_not_supported_is_not_range_checked(string unsupported)
    {
        var (fake, range, _) = Slider(
            minimum: unsupported is "min" or "both" ? NotSupported : 0.0,
            maximum: unsupported is "max" or "both" ? NotSupported : 100.0);
        // Within whatever bound IS known, so only an implementation that reads an unsupported bound
        // as 0 (ValueOrDefault) refuses it.
        var value = unsupported == "max" ? "1000" : unsupported == "min" ? "-1000" : "5000";

        var result = await _svc.InteractAsync(fake.Id, "set_value", value);

        range.Verify(p => p.SetValue(double.Parse(value, CultureInfo.InvariantCulture)), Times.Once);
        result.Method.Should().Be("RangeValuePattern");
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("")]        // an empty string clears the field
    [InlineData("042")]     // the raw string: a text control is not parsed as a number
    [InlineData("3,5")]
    public async Task R14_Without_a_range_a_writable_ValuePattern_gets_the_raw_string_and_no_focus(string value)
    {
        var fake = Arrange("Path", EditType);
        var valuePattern = OfferValue(fake, readOnly: false);

        var result = await _svc.InteractAsync(fake.Id, "set_value", value);

        valuePattern.Verify(p => p.SetValue(value), Times.Once);
        valuePattern.Verify(p => p.SetValue(It.IsAny<string>()), Times.Once);
        result.Method.Should().Be("ValuePattern");
        // Round 2 (A) re-pin: was "replaced the whole value"; the write is now verified by reading
        // the value back, and the Detail is what the control shows.
        result.Detail.Should().Be($"now '{value}'");
        result.Action.Should().Be("set_value");
        NoInputAndNoFocus(fake);   // unlike `type`: no Focus(), no keyboard fallback
    }

    [Fact]
    public async Task R15_Without_a_range_a_read_only_ValuePattern_is_refused_and_nothing_is_typed()
    {
        var fake = Arrange("Path", EditType);
        var valuePattern = OfferValue(fake, readOnly: true);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "C:/temp");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain("'Path'").And.ContainEquivalentOf("read-only");
        valuePattern.Verify(p => p.SetValue(It.IsAny<string>()), Times.Never);
        NoInputAndNoFocus(fake);
    }

    [Fact]
    public async Task R16_Neither_pattern_is_the_D10_refusal_naming_both()
    {
        var fake = Arrange("Logo", ButtonType);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "5");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Be(UiRefusals.PatternNotSupported("RangeValuePattern or ValuePattern", "Button", "Logo").Message);
        NoInputAndNoFocus(fake);
    }

    [Fact]
    public async Task R16_Neither_pattern_on_a_dead_element_is_reported_gone()
    {
        var fake = Arrange("Logo", ButtonType, processId: () => 0);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "5");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain($"Element '{fake.Id}' is no longer available");
    }

    // ---- R9/R10/R12 under a decimal-comma culture on the STA thread -----------------------------

    /// <summary>Puts the service's STA worker under de-DE (decimal comma, '.' = group separator).</summary>
    private async Task GermanStaAsync()
    {
        var onSta = typeof(UIAutomationService).GetMethod("OnStaAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(typeof(string));
        Func<string> setCulture = () =>
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            return CultureInfo.CurrentCulture.Name;
        };
        var name = await (Task<string>)onSta.Invoke(_svc, [setCulture, CancellationToken.None])!;
        name.Should().Be("de-DE", "precondition: the STA worker now formats and parses with a decimal comma");
    }

    [Fact]
    public async Task R9_R12_Parsing_and_the_detail_are_invariant_whatever_the_thread_culture()
    {
        await GermanStaAsync();
        var fake = Arrange("Volume", SliderType);
        var range = OfferRange(fake, 0.5, 100.5, current: 1);

        var result = await _svc.InteractAsync(fake.Id, "set_value", "37.5");

        range.Verify(p => p.SetValue(37.5), Times.Once, "'37.5' is 37.5 invariant (de-DE would read 375 or refuse it)");
        result.Detail.Should().Be("now 37.5 (range 0.5 to 100.5)");
    }

    [Fact]
    public async Task R9_A_decimal_comma_is_refused_even_under_a_decimal_comma_culture()
    {
        await GermanStaAsync();
        var (fake, range, _) = Slider();

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "3,5");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("'Volume'").And.ContainEquivalentOf("number");
        range.Verify(p => p.SetValue(It.IsAny<double>()), Times.Never, "de-DE would have read 3,5 as 3.5");
    }

    // ============================================================================================
    // Harness self-checks (green by design): the fake answers the way FlaUI would, so a red row
    // above is about the service and never about the harness.
    // ============================================================================================

    [Fact]
    public void Harness_pattern_properties_read_through_FlaUI_and_follow_the_pattern_calls()
    {
        var fake = Arrange("Documents", TreeItemType);
        var ec = OfferExpandCollapse(fake, ExpandCollapseState.Collapsed);
        var range = OfferRange(fake, 0.0, NotSupported, current: 10, snap: v => v + 1);
        OfferValue(fake, readOnly: true, current: "abc");
        SetGeometry(fake, offscreen: false, 100, 200, 41, 21);
        var el = fake.Element;

        el.ControlType.ToString().Should().Be("TreeItem");
        el.Name.Should().Be("Documents");
        el.Patterns.ExpandCollapse.PatternOrDefault!.ExpandCollapseState.Value.Should().Be(ExpandCollapseState.Collapsed);
        ec.Pattern.Object.Expand();
        el.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value.Should().Be(ExpandCollapseState.Expanded);

        var rv = el.Patterns.RangeValue.PatternOrDefault!;
        rv.Minimum.Value.Should().Be(0.0);
        rv.Maximum.TryGetValue(out _).Should().BeFalse("NotSupported reads as not supported");
        rv.Maximum.ValueOrDefault.Should().Be(0.0, "which is why an implementation must not use ValueOrDefault for a bound");
        rv.IsReadOnly.Value.Should().BeFalse();
        range.Object.SetValue(36);
        rv.Value.Value.Should().Be(37);

        el.Patterns.Value.PatternOrDefault!.IsReadOnly.Value.Should().BeTrue();
        el.Patterns.Value.PatternOrDefault!.Value.Value.Should().Be("abc");
        el.IsOffscreen.Should().BeFalse();
        el.BoundingRectangle.Should().Be(new System.Drawing.Rectangle(100, 200, 41, 21));
    }

    [Fact]
    public void Harness_patterns_are_absent_unless_offered_and_ids_resolve_to_the_fake()
    {
        var fake = Arrange();
        var p = fake.Element.Patterns;

        p.ExpandCollapse.PatternOrDefault.Should().BeNull();
        p.RangeValue.PatternOrDefault.Should().BeNull();
        p.Value.PatternOrDefault.Should().BeNull();
        p.ScrollItem.PatternOrDefault.Should().BeNull();
        p.Scroll.IsSupported.Should().BeFalse();
        Scrollable(fake);
        Arrange().Element.Patterns.Scroll.IsSupported.Should().BeFalse("a fresh fake is not scrollable");
        Cache.Get(fake.Id).Should().BeSameAs(fake.Element);
        _svc.CachedElementCount.Should().Be(2);
        Arrange(issue: false);
        _svc.CachedElementCount.Should().Be(2, "an ancestor fake is not issued an id");
    }

    [Fact]
    public void Harness_an_offered_scroll_pattern_makes_the_fake_scrollable()
    {
        var fake = Arrange("Files", ListType);
        Scrollable(fake);

        fake.Element.Patterns.Scroll.IsSupported.Should().BeTrue();
        fake.Element.Patterns.Scroll.Pattern.VerticallyScrollable.Value.Should().BeTrue("the default fake scrolls vertically");
        fake.Element.Patterns.Scroll.Pattern.HorizontallyScrollable.Value.Should().BeFalse();
        fake.Element.ControlType.ToString().Should().Be("List");
    }

    [Fact]
    public async Task R10_The_range_in_the_refusal_is_formatted_invariant_whatever_the_thread_culture()
    {
        await GermanStaAsync();
        var (fake, _, _) = Slider();

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "1000");

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.Message.Should().Contain("-7.25").And.Contain("180.5").And.NotContain("-7,25").And.NotContain("180,5");
    }

    // ============================================================================================
    // R17–R18: scroll_into_view
    // ============================================================================================

    [Fact]
    public async Task R17_ScrollIntoView_is_called_once_and_the_detail_is_the_on_screen_centre_re_read()
    {
        var fake = Arrange("Row 40", ListItemType);

        var scrollItem = OfferScrollItem(fake, onScroll: () => SetGeometry(fake, offscreen: false, 100, 200, 41, 21));

        var result = await _svc.InteractAsync(fake.Id, "scroll_into_view", null);

        scrollItem.Verify(p => p.ScrollIntoView(), Times.Once);
        result.ElementId.Should().Be(fake.Id);
        result.Action.Should().Be("scroll_into_view");
        result.Method.Should().Be("ScrollItemPattern");
        result.Detail.Should().Be("now on screen, centre (120,210)");
        NoInputAndNoFocus(fake);
    }

    [Theory]
    [InlineData(true, 100.0, 200.0, 40.0, 20.0)]    // still reports offscreen
    [InlineData(false, 0.0, 0.0, 0.0, 0.0)]         // not offscreen, but no bounds to be on screen with
    [InlineData(true, 0.0, 0.0, 0.0, 0.0)]
    public async Task R17_A_control_that_is_still_not_visibly_on_screen_says_so(bool offscreen, double l, double t, double w, double h)
    {
        var fake = Arrange("Row 40", ListItemType);

        var scrollItem = OfferScrollItem(fake, onScroll: () => SetGeometry(fake, offscreen, l, t, w, h));

        var result = await _svc.InteractAsync(fake.Id, "scroll_into_view", null);

        scrollItem.Verify(p => p.ScrollIntoView(), Times.Once);
        result.Method.Should().Be("ScrollItemPattern");
        result.Detail.Should().Be("the control still reports offscreen");
    }

    [Fact]
    public async Task R17_A_re_read_that_fails_leaves_the_detail_null_and_the_call_succeeds()
    {
        var fake = Arrange("Row 40", ListItemType);
        var failure = FlaUiShaped.From(FlaUiShaped.E_FAIL);

        var scrollItem = OfferScrollItem(fake, onScroll: () =>
        {
            fake.Props[Lib.Element.IsOffscreen.Id] = Throws(failure);
            fake.Props[Lib.Element.BoundingRectangle.Id] = Throws(failure);
        });

        var result = await _svc.InteractAsync(fake.Id, "scroll_into_view", null);

        scrollItem.Verify(p => p.ScrollIntoView(), Times.Once);
        result.Method.Should().Be("ScrollItemPattern");
        result.Detail.Should().BeNull();
    }

    [Fact]
    public async Task R17_A_disabled_element_is_not_refused_it_can_still_be_brought_into_view()
    {
        var fake = Arrange("Row 40", ListItemType, isEnabled: () => false);

        var scrollItem = OfferScrollItem(fake, onScroll: () => SetGeometry(fake, offscreen: false, 0, 0, 10, 10));

        var result = await _svc.InteractAsync(fake.Id, "scroll_into_view", null);

        scrollItem.Verify(p => p.ScrollIntoView(), Times.Once, "scroll_into_view does not act on the control (acts: false)");
        result.Method.Should().Be("ScrollItemPattern");
        NoInputAndNoFocus(fake);
    }

    [Fact]
    public async Task R17_R18_A_disabled_element_without_ScrollItemPattern_gets_the_pattern_refusal_not_disabled()
    {
        var fake = Arrange("Row 40", ListItemType, isEnabled: () => false);
        Parents((fake, null));

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("ScrollItemPattern").And.NotContainEquivalentOf("disabled");
    }

    /// <summary>R18 (a) and (b): what every ScrollItemPattern refusal says.</summary>
    private static void ShouldBeTheScrollItemRefusal(Exception ex, string type, string name)
    {
        ex.Should().BeOfType<InvalidOperationException>();
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain("ScrollItemPattern").And.Contain(type).And.Contain($"'{name}'");
        ex.Message.Should().MatchRegex(@"\bscroll\b", "(b) it names the `scroll` tool as the way out");
        ex.Message.Should().MatchRegex("(?i)scrollable|container", "(b) what to scroll: the nearest scrollable container");
        ex.Message.Should().ContainEquivalentOf("snapshot", "(b) and to take a new snapshot afterwards");
    }

    [Fact]
    public async Task R18_No_ScrollItemPattern_names_the_nearest_scrollable_ancestor_by_a_usable_id()
    {
        var target = Arrange("Row 40", ListItemType);
        var group = Arrange("Group A", GroupType, issue: false);
        // A DataGrid, so its type cannot be satisfied by the target's own "ListItem".
        var list = Arrange("Files", DataGridType, issue: false);
        var pane = Arrange("Outer", PaneType, issue: false);
        Scrollable(list);
        Scrollable(pane);   // also scrollable, but further away
        Parents((target, group), (group, list), (list, pane), (pane, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ShouldBeTheScrollItemRefusal(ex, "ListItem", "Row 40");
        var named = IdsIn(ex.Message).Where(id => id != target.Id).Distinct().ToArray();
        named.Should().ContainSingle("(c) exactly one ancestor is named");
        Cache.Get(named[0]).Should().BeSameAs(list.Element, "the id resolves to the NEAREST scrollable ancestor");
        ex.Message.Should().Contain("DataGrid").And.Contain("'Files'", "(c) the ancestor's type and name");
        ex.Message.Should().NotContain("'Outer'");
        _svc.CachedElementCount.Should().Be(2, "one id minted for the named ancestor, none for the ancestors walked past");
        NoInputAndNoFocus(target);
    }

    [Fact]
    public async Task R18_No_scrollable_ancestor_is_the_same_refusal_without_an_id()
    {
        var target = Arrange("Row 40", ListItemType);
        var group = Arrange("Group A", GroupType, issue: false);
        var pane = Arrange("Outer", PaneType, issue: false);
        Parents((target, group), (group, pane), (pane, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ShouldBeTheScrollItemRefusal(ex, "ListItem", "Row 40");
        IdsIn(ex.Message).Should().OnlyContain(id => id == target.Id, "no ancestor id to give");
        _svc.CachedElementCount.Should().Be(1, "nothing minted for ancestors that were not named");
        ParentCalls.Should().BeGreaterThan(0, "precondition: the walk ran");
    }

    public static TheoryData<string> ParentFailures => new() { "first", "second" };

    [Theory]
    [MemberData(nameof(ParentFailures))]
    public async Task R18_A_parent_walk_that_throws_is_the_same_refusal_without_an_id_never_a_masked_fault(string where)
    {
        var target = Arrange("Row 40", ListItemType);
        var group = Arrange("Group A", GroupType, issue: false);
        var failure = FlaUiShaped.From(FlaUiShaped.RPC_S_SERVER_UNAVAILABLE);
        _svc.ParentOf = e =>
        {
            ParentCalls++;
            if (where == "first" || !ReferenceEquals(e, target.Element)) throw failure;
            return group.Element;
        };

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ShouldBeTheScrollItemRefusal(ex, "ListItem", "Row 40");
        IdsIn(ex.Message).Should().OnlyContain(id => id == target.Id);
        ParentCalls.Should().BeGreaterThan(0, "precondition: the walk ran into the failure");
    }

    [Fact]
    public async Task R18_An_ancestor_whose_scroll_probe_throws_is_skipped_and_the_walk_goes_on()
    {
        var target = Arrange("Row 40", ListItemType);
        var broken = Arrange("Broken", GroupType, issue: false);
        broken.Framework.Protected().Setup<IAutomationPattern<IScrollPattern>>("InitializeScrollPattern")
            .Throws(FlaUiShaped.From(FlaUiShaped.E_FAIL));
        var list = Arrange("Files", ListType, issue: false);
        Scrollable(list);
        Parents((target, broken), (broken, list), (list, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ShouldBeTheScrollItemRefusal(ex, "ListItem", "Row 40");
        var named = IdsIn(ex.Message).Where(id => id != target.Id).Distinct().ToArray();
        named.Should().ContainSingle();
        Cache.Get(named[0]).Should().BeSameAs(list.Element);
    }

    [Fact]
    public async Task R18_An_endless_parent_chain_is_cut_off_at_25_levels()
    {
        // A provider whose parent walk never reaches the root (a cycle, or a broken tree) must not
        // hang the one STA thread every element tool shares.
        var target = Arrange("Row 40", ListItemType);
        _svc.ParentOf = _ =>
        {
            ParentCalls++;
            return ParentCalls > 1000 ? null : Arrange("Loop", GroupType, issue: false).Element;
        };

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ShouldBeTheScrollItemRefusal(ex, "ListItem", "Row 40");
        ParentCalls.Should().BeInRange(1, 25, "the walk is bounded to 25 levels");
        _svc.CachedElementCount.Should().Be(1);
    }

    [Fact]
    public async Task R18_A_self_referencing_parent_terminates()
    {
        var target = Arrange("Row 40", ListItemType);
        _svc.ParentOf = e => { ParentCalls++; return e; };

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        await act.Should().ThrowAsync<InvalidOperationException>();
        ParentCalls.Should().BeLessThanOrEqualTo(25);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(25, true)]    // the 25th ancestor is within 25 levels
    [InlineData(26, false)]   // the 26th is not
    public async Task R18_The_walk_reaches_exactly_25_ancestors(int depth, bool found)
    {
        var target = Arrange("Row 40", ListItemType);
        var chain = new List<Fake> { target };
        for (int i = 1; i <= depth; i++) chain.Add(Arrange($"Level {i}", i == depth ? ListType : GroupType, issue: false));
        Scrollable(chain[^1]);
        _svc.ParentOf = e =>
        {
            ParentCalls++;
            var at = chain.FindIndex(f => ReferenceEquals(f.Element, e));
            return at >= 0 && at + 1 < chain.Count ? chain[at + 1].Element : null;
        };

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        var named = IdsIn(ex.Message).Where(id => id != target.Id).Distinct().ToArray();
        if (found)
        {
            named.Should().ContainSingle();
            Cache.Get(named[0]).Should().BeSameAs(chain[^1].Element);
            ex.Message.Should().Contain($"'Level {depth}'");
        }
        else
        {
            named.Should().BeEmpty();
            ex.Message.Should().NotContain($"'Level {depth}'");
        }
    }

    [Fact]
    public async Task R18_No_ScrollItemPattern_on_a_dead_element_is_reported_gone_and_names_no_ancestor()
    {
        var target = Arrange("Row 40", ListItemType, processId: () => 0);
        var list = Arrange("Files", ListType, issue: false);
        Scrollable(list);
        Parents((target, list), (list, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain($"Element '{target.Id}' is no longer available");
        ex.Message.Should().NotContain("'Files'");
        _svc.CachedElementCount.Should().Be(1);
    }

    // ============================================================================================
    // All refusals: FlaUI/COM failures from the new pattern calls go through Refusing
    // ============================================================================================

    [Theory]
    [InlineData("expand")]
    [InlineData("collapse")]
    public async Task Refusing_ElementNotEnabled_from_the_pattern_call_says_the_control_or_the_item(string verb)
    {
        var fake = Arrange("Documents", TreeItemType);
        var ec = OfferExpandCollapse(fake, verb == "expand" ? ExpandCollapseState.Collapsed : ExpandCollapseState.Expanded);
        var thrown = FlaUiShaped.From(FlaUiShaped.UIA_E_ELEMENTNOTENABLED);
        ec.Pattern.Setup(p => p.Expand()).Throws(thrown);
        ec.Pattern.Setup(p => p.Collapse()).Throws(thrown);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, verb, null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().ContainEquivalentOf("or the item being acted on").And.Contain(verb);
        ex.InnerException.Should().BeSameAs(thrown);
    }

    [Fact]
    public async Task Refusing_Element_gone_mid_call_is_reported_gone()
    {
        var alive = true;
        var fake = Arrange("Documents", TreeItemType, processId: () => alive ? 4242 : 0);
        var ec = OfferExpandCollapse(fake, ExpandCollapseState.Expanded);
        ec.Pattern.Setup(p => p.Collapse()).Callback(() => alive = false)
            .Throws(new FlaUI.Core.Exceptions.ElementNotAvailableException());

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "collapse", null);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain($"Element '{fake.Id}' is no longer available");
    }

    [Fact]
    public async Task Refusing_Not_supported_HRESULT_from_ScrollIntoView_is_translated()
    {
        var fake = Arrange("Row 40", ListItemType);
        var scrollItem = OfferScrollItem(fake);
        scrollItem.Setup(p => p.ScrollIntoView()).Throws(FlaUiShaped.From(FlaUiShaped.UIA_E_NOTSUPPORTED));

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain("does not support what scroll_into_view needs");
    }

    public static TheoryData<string> TimeoutPaths => new() { "expand", "set_value_range", "set_value_text", "scroll_into_view" };

    [Theory]
    [MemberData(nameof(TimeoutPaths))]
    public async Task Refusing_A_timeout_from_the_pattern_call_propagates_untouched_without_re_reading_the_element(string path)
    {
        var timeout = FlaUiShaped.From(FlaUiShaped.UIA_E_TIMEOUT);
        timeout.Should().BeOfType<TimeoutException>("precondition");
        Fake fake;
        string verb;
        string? value = null;
        switch (path)
        {
            case "expand":
                fake = Arrange("Documents", TreeItemType);
                OfferExpandCollapse(fake, ExpandCollapseState.Collapsed).Pattern.Setup(p => p.Expand()).Throws(timeout);
                verb = "expand";
                break;
            case "set_value_range":
                fake = Arrange("Volume", SliderType);
                OfferRange(fake, 0.0, 100.0).Setup(p => p.SetValue(It.IsAny<double>())).Throws(timeout);
                (verb, value) = ("set_value", "5");
                break;
            case "set_value_text":
                fake = Arrange("Path", EditType);
                OfferValue(fake, readOnly: false).Setup(p => p.SetValue(It.IsAny<string>())).Throws(timeout);
                (verb, value) = ("set_value", "x");
                break;
            default:
                fake = Arrange("Row 40", ListItemType);
                OfferScrollItem(fake).Setup(p => p.ScrollIntoView()).Throws(timeout);
                verb = "scroll_into_view";
                break;
        }

        Func<Task> act = () => _svc.InteractAsync(fake.Id, verb, value);

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(timeout);
        fake.ProcessIdReads.Should().Be(0, "R23: a hung target is not probed again");
    }

    [Fact]
    public async Task Refusing_An_unrecognised_fault_from_a_live_target_stays_masked()
    {
        // Round 2 (B) re-pin: this used set_value, whose provider faults are now a named rejection
        // (B_*). The masking contract itself is unchanged, so it is pinned on a verb B does not touch.
        var fake = Arrange("Row 40", ListItemType);
        var thrown = FlaUiShaped.From(FlaUiShaped.E_FAIL);
        OfferScrollItem(fake).Setup(p => p.ScrollIntoView()).Throws(thrown);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "scroll_into_view", null);

        (await act.Should().ThrowAsync<Win32Exception>()).Which.Should().BeSameAs(thrown);
    }

    // ============================================================================================
    // GREEN close-out: the decisions taken on the points the RED matrix left open (D1–D7), and
    // the branches the RED rows did not reach.
    // ============================================================================================

    // ---- D1: an ExpandCollapse state that cannot be read before the call ------------------------

    [Theory]
    [InlineData("expand", "now Expanded")]
    [InlineData("collapse", "now Collapsed")]
    public async Task D1_An_unreadable_state_skips_the_leaf_and_already_checks_and_calls_the_method_once(string verb, string detail)
    {
        var fake = Arrange("Documents", TreeItemType);
        // The initial state is irrelevant: it is removed, so the read before the call answers
        // "not supported" (TryGetValue false). The call itself makes the state readable again.
        var ec = OfferExpandCollapse(fake, ExpandCollapseState.Collapsed);
        fake.Props.Remove(Lib.ExpandCollapse.ExpandCollapseState.Id);

        var result = await _svc.InteractAsync(fake.Id, verb, null);

        if (verb == "expand")
        {
            ec.Pattern.Verify(p => p.Expand(), Times.Once);
            ec.Pattern.Verify(p => p.Collapse(), Times.Never);
        }
        else
        {
            ec.Pattern.Verify(p => p.Collapse(), Times.Once);
            ec.Pattern.Verify(p => p.Expand(), Times.Never);
        }
        ec.StateReadsAtCall.Should().BeGreaterThan(0, "precondition: the state WAS asked for before the call, and could not be read");
        result.Method.Should().Be("ExpandCollapsePattern");
        result.Detail.Should().Be(detail, "the re-read after the call still says what the control shows");
        NoInputAndNoFocus(fake);
    }

    [Fact]
    public async Task D1_An_unreadable_state_that_stays_unreadable_succeeds_with_a_null_detail()
    {
        var fake = Arrange("Documents", TreeItemType);
        var ec = OfferExpandCollapse(fake, ExpandCollapseState.Collapsed);
        fake.Props.Remove(Lib.ExpandCollapse.ExpandCollapseState.Id);
        ec.Pattern.Setup(p => p.Expand());   // the state never becomes readable

        var result = await _svc.InteractAsync(fake.Id, "expand", null);

        ec.Pattern.Verify(p => p.Expand(), Times.Once);
        result.Method.Should().Be("ExpandCollapsePattern");
        result.Detail.Should().BeNull("a re-read of a property the provider does not support is no detail, not a failure");
    }

    // ---- D2: a read-only flag the provider does not report is treated as writable ---------------

    [Fact]
    public async Task D2_A_range_whose_IsReadOnly_is_not_supported_is_written()
    {
        var fake = Arrange("Volume", SliderType);
        var range = OfferRange(fake, 0.0, 100.0, current: 10);
        fake.Props.Remove(Lib.RangeValue.IsReadOnly.Id);

        var result = await _svc.InteractAsync(fake.Id, "set_value", "25");

        range.Verify(p => p.SetValue(25), Times.Once);
        result.Method.Should().Be("RangeValuePattern");
        result.Detail.Should().Be("now 25 (range 0 to 100)");
        NoInputAndNoFocus(fake);
    }

    [Fact]
    public async Task D2_A_ValuePattern_whose_IsReadOnly_is_not_supported_is_written()
    {
        var fake = Arrange("Path", EditType);
        var valuePattern = OfferValue(fake, readOnly: false);
        fake.Props.Remove(Lib.Value.IsReadOnly.Id);

        var result = await _svc.InteractAsync(fake.Id, "set_value", "C:/temp");

        valuePattern.Verify(p => p.SetValue("C:/temp"), Times.Once);
        result.Method.Should().Be("ValuePattern");
        NoInputAndNoFocus(fake);
    }

    // ---- D3: on a range control, not-a-number, then out-of-range, then read-only ----------------

    [Theory]
    [InlineData("loud")]
    [InlineData("1000")]
    [InlineData("50")]
    public async Task H_On_a_read_only_range_the_read_only_refusal_comes_first(string value)
    {
        // Round 2 (H) re-pin: this was D3, "the value errors come first". Reversed: a read-only
        // progress bar given "abc" is told it is read-only, since no value would ever be set.
        var (fake, range, valuePattern) = Slider(readOnly: true);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", value);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain("'Volume'").And.ContainEquivalentOf("read-only")
            .And.NotContain("needs a number");
        range.Verify(p => p.SetValue(It.IsAny<double>()), Times.Never);
        valuePattern.Verify(p => p.SetValue(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task D3_A_non_number_is_the_number_error_not_the_range_error()
    {
        var (fake, _, _) = Slider();

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "loud");

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("value");
        ex.Message.Should().Be(
            "set_value on Slider 'Volume' needs a number between -7.25 and 180.5, with '.' as the decimal point; got 'loud'. (Parameter 'value')");
    }

    [Fact]
    public async Task D3_Out_of_range_message_is_exact_and_quotes_the_trimmed_value()
    {
        var (fake, _, _) = Slider();

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", " 1000 ");

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("value");
        ex.Message.Should().Be(
            "set_value on Slider 'Volume' needs a number between -7.25 and 180.5; 1000 is outside that range. (Parameter 'value')");
    }

    // ---- D4: only the bound the provider reports is checked, and the message says which ---------

    [Theory]
    // min known, max not: below the min is refused "of at least"
    [InlineData(0.0, null, "-1", "needs a number of at least 0; -1 is outside that range")]
    // max known, min not: above the max is refused "of at most"
    [InlineData(null, 100.0, "100.5", "needs a number of at most 100; 100.5 is outside that range")]
    public async Task D4_With_one_bound_known_only_that_bound_is_checked_and_named(double? min, double? max, string value, string fragment)
    {
        var (fake, range, _) = Slider(minimum: min is { } l ? l : NotSupported, maximum: max is { } h ? h : NotSupported);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", value);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("value");
        ex.Message.Should().Contain(fragment).And.NotContain("between");
        range.Verify(p => p.SetValue(It.IsAny<double>()), Times.Never);
    }

    [Theory]
    [InlineData("min", "set_value on Slider 'Volume' needs a number of at most 100, with '.' as the decimal point; got 'loud'.")]
    [InlineData("max", "set_value on Slider 'Volume' needs a number of at least 0, with '.' as the decimal point; got 'loud'.")]
    [InlineData("both", "set_value on Slider 'Volume' needs a number, with '.' as the decimal point; got 'loud'.")]
    public async Task D4_The_number_error_quotes_whatever_bounds_are_known(string unsupported, string message)
    {
        var (fake, _, _) = Slider(
            minimum: unsupported is "min" or "both" ? NotSupported : 0.0,
            maximum: unsupported is "max" or "both" ? NotSupported : 100.0);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "loud");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Be(message + " (Parameter 'value')");
    }

    // ---- D5: the range detail without both bounds ----------------------------------------------

    [Theory]
    [InlineData("min", "1000", "now 1000")]
    [InlineData("max", "-1000", "now -1000")]
    [InlineData("both", "5000", "now 5000")]
    public async Task D5_The_detail_has_no_range_suffix_unless_both_bounds_are_known(string unsupported, string value, string detail)
    {
        var (fake, _, _) = Slider(
            minimum: unsupported is "min" or "both" ? NotSupported : -5000.0,
            maximum: unsupported is "max" or "both" ? NotSupported : 5000.0);

        var result = await _svc.InteractAsync(fake.Id, "set_value", value);

        result.Detail.Should().Be(detail);
    }

    // ---- D6: the scroll_into_view refusal names the target's own id after the control ----------

    [Fact]
    public async Task D6_The_scroll_refusal_names_the_target_by_its_id_after_the_control_with_an_ancestor()
    {
        var target = Arrange("Row 40", ListItemType);
        var list = Arrange("Files", ListType, issue: false);
        Scrollable(list);
        Parents((target, list), (list, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        var listId = IdsIn(ex.Message).Single(id => id != target.Id);
        ex.Message.Should().Be(
            $"ScrollItemPattern not supported on ListItem 'Row 40' ({target.Id}). Scroll its container instead: " +
            $"scroll with element_id {listId} (List 'Files'), then take a new snapshot.");
    }

    [Fact]
    public async Task D6_The_scroll_refusal_names_the_target_by_its_id_after_the_control_without_an_ancestor()
    {
        var target = Arrange("Row 40", ListItemType);
        Parents((target, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(
            $"ScrollItemPattern not supported on ListItem 'Row 40' ({target.Id}). Scroll the nearest scrollable container " +
            "instead with the scroll tool (find_element with kind:scrollable lists them), then take a new snapshot.");
    }

    [Fact]
    public async Task D6_The_walk_starts_at_the_target_and_never_names_the_target_itself_even_when_it_is_scrollable()
    {
        // A scrollable control without ScrollItemPattern (a list that is itself a scroll host):
        // the refusal names the nearest scrollable ANCESTOR, not the element the caller already has.
        var target = Arrange("Inner", ListType);
        Scrollable(target);
        Parents((target, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().NotContain("scroll with element_id");
        _svc.CachedElementCount.Should().Be(1, "the target is not re-issued as its own container");
    }

    [Fact]
    public async Task R18_A_parent_walk_that_returns_the_target_itself_never_names_the_target_even_when_it_scrolls()
    {
        // The self-reference guard is what stops a provider that answers "my parent is me" from
        // turning a scrollable target into its own container; the 25-level bound alone would not.
        var target = Arrange("Inner", ListType);
        Scrollable(target);
        _svc.ParentOf = e => { ParentCalls++; return e; };

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().NotContain("scroll with element_id");
        ParentCalls.Should().Be(1, "a parent that is the element itself ends the walk at once");
    }

    [Theory]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("-Infinity")]
    public async Task R9_A_non_finite_number_is_refused_even_where_no_bound_would_catch_it(string value)
    {
        // With both bounds unknown nothing but the finiteness check stands between ±Infinity and
        // the provider: the range check cannot refuse what it has no bound for.
        var (fake, range, _) = Slider(minimum: NotSupported, maximum: NotSupported);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", value);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain($"got '{value}'");
        range.Verify(p => p.SetValue(It.IsAny<double>()), Times.Never);
    }

    // ---- D7: a cancellation is never swallowed by a re-read or by the parent walk ---------------

    public static TheoryData<string> RereadPaths => new() { "expand", "set_value_range", "scroll_into_view" };

    [Theory]
    [MemberData(nameof(RereadPaths))]
    public async Task D7_A_cancellation_from_the_re_read_propagates_it_is_not_a_null_detail(string path)
    {
        var cancelled = new OperationCanceledException("cancelled during the re-read");
        Fake fake;
        string verb;
        string? value = null;
        switch (path)
        {
            case "expand":
                fake = Arrange("Documents", TreeItemType);
                OfferExpandCollapse(fake, ExpandCollapseState.Collapsed, rereadThrows: cancelled);
                verb = "expand";
                break;
            case "set_value_range":
                fake = Arrange("Volume", SliderType);
                OfferRange(fake, 0.0, 100.0, rereadThrows: cancelled);
                (verb, value) = ("set_value", "5");
                break;
            default:
                fake = Arrange("Row 40", ListItemType);
                OfferScrollItem(fake, onScroll: () => fake.Props[Lib.Element.BoundingRectangle.Id] = Throws(cancelled));
                verb = "scroll_into_view";
                break;
        }

        Func<Task> act = () => _svc.InteractAsync(fake.Id, verb, value);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(cancelled);
    }

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    public async Task D7_A_cancellation_from_the_parent_walk_propagates_it_is_not_no_ancestor(string where)
    {
        var target = Arrange("Row 40", ListItemType);
        var group = Arrange("Group A", GroupType, issue: false);
        var cancelled = new OperationCanceledException("cancelled during the parent walk");
        _svc.ParentOf = e =>
        {
            ParentCalls++;
            if (where == "first" || !ReferenceEquals(e, target.Element)) throw cancelled;
            return group.Element;
        };

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(cancelled);
        _svc.CachedElementCount.Should().Be(1, "nothing minted on the way to a cancellation");
    }

    // ---- The default ParentOf seam -------------------------------------------------------------

    [Fact]
    public void Default_ParentOf_on_an_element_UIA_cannot_walk_is_no_parent_not_a_fault()
    {
        // The default seam is the control-view walker. A fake element has no native element
        // behind it, so the walker fails; the seam's contract is that any failure is "no parent".
        var fake = Arrange("Row 40", ListItemType);

        AutomationElement? parent = null;
        var act = () => { parent = _svc.ParentOf(fake.Element); };

        act.Should().NotThrow();
        parent.Should().BeNull();
    }

    // ---- set_value: the two dead-element paths and the case-insensitive verb ---------------------

    [Fact]
    public async Task R14_Set_value_verb_is_case_insensitive_and_the_result_carries_it_lower_cased()
    {
        var fake = Arrange("Path", EditType);
        var valuePattern = OfferValue(fake, readOnly: false);

        var result = await _svc.InteractAsync(fake.Id, "Set_Value", "x");

        valuePattern.Verify(p => p.SetValue("x"), Times.Once);
        result.Action.Should().Be("set_value");
    }

    // ============================================================================================
    // RED round 2 (review-agent findings + the FG defect): decisions A–J.
    // ============================================================================================

    private static readonly string LongName = new('N', 3000);
    private static string Clipped(string s) => s.Length <= 80 ? s : s[..80] + "...";

    // ---- A: set_value through ValuePattern is a verified write ----------------------------------

    [Fact]
    public async Task A_A_value_the_control_normalised_is_a_success_reporting_what_it_shows()
    {
        var fake = Arrange("Path", EditType);
        var valuePattern = OfferValue(fake, readOnly: false, current: "old", land: v => v.ToUpperInvariant());

        var result = await _svc.InteractAsync(fake.Id, "set_value", "abc");

        valuePattern.Verify(p => p.SetValue("abc"), Times.Once);
        result.Method.Should().Be("ValuePattern");
        result.Detail.Should().Be("now 'ABC'", "the after-read differs from the before-read: the control took it and normalised it");
    }

    [Fact]
    public async Task A_A_call_that_left_the_value_unchanged_is_refused_quoting_what_the_control_still_shows()
    {
        // The WinForms TrackBar / drop-down-list case: SetValue returns OK and nothing moves.
        var fake = Arrange("Path", EditType);
        var valuePattern = OfferValue(fake, readOnly: false, current: "old", land: _ => "old");

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "zzz");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain("Edit 'Path'", "it names the control")
            .And.MatchRegex("(?i)did not change|didn't change|unchanged", "it says the value did not change")
            .And.Contain("'old'", "it quotes the value the control still shows");
        valuePattern.Verify(p => p.SetValue("zzz"), Times.Once, "the call was made; only its effect is missing");
        NoInputAndNoFocus(fake);
    }

    [Fact]
    public async Task A_Setting_the_value_the_control_already_shows_is_a_success()
    {
        var fake = Arrange("Path", EditType);
        OfferValue(fake, readOnly: false, current: "old", land: _ => "old");

        var result = await _svc.InteractAsync(fake.Id, "set_value", "old");

        result.Method.Should().Be("ValuePattern");
        result.Detail.Should().Be("now 'old'");
    }

    public static TheoryData<string> UnreadableValuePaths => new() { "before_throws", "after_throws", "not_supported" };

    [Theory]
    [MemberData(nameof(UnreadableValuePaths))]
    public async Task A_When_either_read_fails_the_set_is_a_success_with_the_unverified_detail(string path)
    {
        var fake = Arrange("Path", EditType);
        var valuePattern = OfferValue(fake, readOnly: false, current: "old");
        var valueId = Lib.Value.Value.Id;
        var failure = FlaUiShaped.From(FlaUiShaped.E_FAIL);
        switch (path)
        {
            case "before_throws":
                fake.Props[valueId] = Throws(failure);   // the SetValue callback makes it readable again
                break;
            case "after_throws":
                valuePattern.Setup(p => p.SetValue(It.IsAny<string>())).Callback(() => fake.Props[valueId] = Throws(failure));
                break;
            default:
                // Never readable, and the set changes nothing readable: must not become the
                // "did not change" refusal, which needs a readable after-read.
                fake.Props.Remove(valueId);
                valuePattern.Setup(p => p.SetValue(It.IsAny<string>()));
                break;
        }

        var result = await _svc.InteractAsync(fake.Id, "set_value", "zzz");

        valuePattern.Verify(p => p.SetValue("zzz"), Times.Once);
        result.Method.Should().Be("ValuePattern");
        result.Detail.Should().Be("replaced the whole value");
    }

    [Fact]
    public async Task A_A_password_field_is_never_read_and_its_value_never_reported()
    {
        var fake = Arrange("Password", EditType);
        fake.Props[Lib.Element.IsPassword.Id] = () => true;
        // If it WERE read, "unchanged" would make this the did-not-change refusal quoting the secret.
        var valuePattern = OfferValue(fake, readOnly: false, current: "hunter2", land: _ => "hunter2");

        var result = await _svc.InteractAsync(fake.Id, "set_value", "s3cret-new");

        valuePattern.Verify(p => p.SetValue("s3cret-new"), Times.Once);
        result.Method.Should().Be("ValuePattern");
        result.Detail.Should().Be("replaced the whole value");
        fake.ReadsOf(Lib.Value.Value).Should().Be(0, "a password value is never read, before or after");
        NoInputAndNoFocus(fake);
    }

    public static TheoryData<string> PasswordRejections => new() { "argument", "win32" };

    [Theory]
    [MemberData(nameof(PasswordRejections))]
    public async Task A_B_A_password_field_rejection_never_echoes_the_value(string kind)
    {
        var fake = Arrange("Password", EditType);
        fake.Props[Lib.Element.IsPassword.Id] = () => true;
        Exception thrown = kind == "argument" ? new ArgumentException("Value is not valid.") : FlaUiShaped.From(0x80020005);
        OfferValue(fake, readOnly: false, current: "hunter2").Setup(p => p.SetValue(It.IsAny<string>())).Throws(thrown);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "s3cret-new");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("Edit 'Password'").And.Contain("rejected")
            .And.NotContain("s3cret-new", "a password value never appears in any error")
            .And.NotContain("hunter2");
        fake.ReadsOf(Lib.Value.Value).Should().Be(0);
    }

    [Theory]
    [InlineData("a\u0007b", "now 'ab'")]           // sanitised (UiText.Sanitize drops C0 controls)
    [InlineData("  padded  ", "now 'padded'")]      // and trims
    public async Task A_The_detail_is_the_after_read_sanitised(string shown, string detail)
    {
        var fake = Arrange("Path", EditType);
        OfferValue(fake, readOnly: false, current: "old", land: _ => shown);

        var result = await _svc.InteractAsync(fake.Id, "set_value", "anything");

        result.Detail.Should().Be(detail);
    }

    [Theory]
    [InlineData(80, false)]
    [InlineData(81, true)]
    [InlineData(3000, true)]
    public async Task A_C_The_detail_is_clipped_to_80_characters(int length, bool clipped)
    {
        var shown = new string('x', length);
        var fake = Arrange("Path", EditType);
        OfferValue(fake, readOnly: false, current: "old", land: _ => shown);

        var result = await _svc.InteractAsync(fake.Id, "set_value", shown);

        result.Detail.Should().Be(clipped ? $"now '{new string('x', 80)}...'" : $"now '{shown}'");
    }

    // ---- B: a provider rejection from SetValue is a named refusal ------------------------------

    public static TheoryData<string, string> Rejections => new()
    {
        { "text", "argument" }, { "text", "win32" }, { "range", "argument" }, { "range", "win32" },
    };

    /// <param name="dies">When true the control's process goes away inside the SetValue call that throws.</param>
    private (Fake Fake, string Value, string Control) RejectingControl(string pattern, Exception thrown, bool dies = false)
    {
        var alive = true;
        Func<object> processId = () => alive ? 4242 : 0;
        if (pattern == "text")
        {
            var fake = Arrange("Path", EditType, processId: processId);
            OfferValue(fake, readOnly: false).Setup(p => p.SetValue(It.IsAny<string>()))
                .Callback(() => alive = !dies).Throws(thrown);
            return (fake, "x", "Edit 'Path'");
        }
        var slider = Arrange("Volume", SliderType, processId: processId);
        OfferRange(slider, 0.0, 100.0).Setup(p => p.SetValue(It.IsAny<double>()))
            .Callback(() => alive = !dies).Throws(thrown);
        return (slider, "5", "Slider 'Volume'");
    }

    private static Exception Rejection(string kind)
        => kind == "argument" ? new ArgumentException("Value is not valid.") : FlaUiShaped.From(0x80020005);   // DISP_E_TYPEMISMATCH

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task B_A_provider_rejection_on_a_live_control_is_a_refusal_naming_the_control_the_value_and_the_reason(string pattern, string kind)
    {
        var thrown = Rejection(kind);
        if (kind == "win32") thrown.Should().BeOfType<Win32Exception>("precondition: FlaUI's shape for an unmapped HRESULT");
        var (fake, value, control) = RejectingControl(pattern, thrown);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", value);

        var ex = (await act.Should().ThrowExactlyAsync<InvalidOperationException>(
            "never the masked fault, never a bare provider ArgumentException")).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Be($"{control} rejected the value '{value}': {thrown.Message}");
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task B_A_rejection_from_a_control_that_died_during_the_call_is_gone(string pattern, string kind)
    {
        var (fake, value, _) = RejectingControl(pattern, Rejection(kind), dies: true);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", value);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain($"Element '{fake.Id}' is no longer available").And.NotContain("rejected");
    }

    [Fact]
    public async Task B_The_echoed_value_in_a_rejection_is_clipped()
    {
        var value = new string('v', 300);
        var fake = Arrange("Path", EditType);
        OfferValue(fake, readOnly: false).Setup(p => p.SetValue(It.IsAny<string>())).Throws(new ArgumentException("Too long."));

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", value);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Be($"Edit 'Path' rejected the value '{new string('v', 80)}...': Too long.");
    }

    // ---- C: long names and echoed values are clipped, so what follows them survives the cap -----

    public static TheoryData<string> DescribedRefusals => new()
    {
        "leaf", "value_read_only", "range_read_only", "number", "out_of_range", "unchanged", "rejected",
        "disabled", "scroll_target", "scroll_ancestor",
    };

    [Theory]
    [MemberData(nameof(DescribedRefusals))]
    public async Task C_A_control_name_in_a_refusal_is_clipped_to_80_characters(string path)
    {
        Fake fake;
        string verb = "set_value";
        string? value = "5";
        switch (path)
        {
            case "leaf":
                fake = Arrange(LongName, TreeItemType);
                OfferExpandCollapse(fake, ExpandCollapseState.LeafNode);
                (verb, value) = ("expand", null);
                break;
            case "value_read_only":
                fake = Arrange(LongName, EditType);
                OfferValue(fake, readOnly: true);
                break;
            case "range_read_only":
                fake = Arrange(LongName, SliderType);
                OfferRange(fake, 0.0, 100.0, readOnly: true);
                break;
            case "number":
                fake = Arrange(LongName, SliderType);
                OfferRange(fake, 0.0, 100.0);
                value = "loud";
                break;
            case "out_of_range":
                fake = Arrange(LongName, SliderType);
                OfferRange(fake, 0.0, 100.0);
                value = "1000";
                break;
            case "unchanged":
                fake = Arrange(LongName, EditType);
                OfferValue(fake, readOnly: false, land: _ => "old");
                value = "zzz";
                break;
            case "rejected":
                fake = Arrange(LongName, EditType);
                OfferValue(fake, readOnly: false).Setup(p => p.SetValue(It.IsAny<string>())).Throws(new ArgumentException("bad"));
                break;
            case "disabled":
                fake = Arrange(LongName, EditType, isEnabled: () => false);
                OfferValue(fake, readOnly: false);
                break;
            case "scroll_target":
                fake = Arrange(LongName, ListItemType);
                Parents((fake, null));
                (verb, value) = ("scroll_into_view", null);
                break;
            default:
                fake = Arrange("Row 40", ListItemType);
                var ancestor = Arrange(LongName, ListType, issue: false);
                Scrollable(ancestor);
                Parents((fake, ancestor), (ancestor, null));
                (verb, value) = ("scroll_into_view", null);
                break;
        }

        Func<Task> act = () => _svc.InteractAsync(fake.Id, verb, value);

        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        ex.Should().Match<Exception>(e => e is InvalidOperationException || e is ArgumentException, "precondition: a refusal");
        ex.Message.Should().Contain($"'{new string('N', 80)}...'").And.NotContain(new string('N', 81));
    }

    [Theory]
    [InlineData(80, false)]
    [InlineData(81, true)]
    public async Task C_The_value_echoed_by_the_number_error_is_clipped_to_80_characters(int length, bool clipped)
    {
        var value = new string('a', length);
        var (fake, _, _) = Slider();

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", value);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain(clipped ? $"got '{new string('a', 80)}...'" : $"got '{value}'");
        if (clipped) message.Should().NotContain(new string('a', 81));
    }

    [Fact]
    public async Task C_The_value_echoed_by_the_out_of_range_error_is_clipped()
    {
        var value = new string('0', 3000) + "1000";   // parses as 1000: out of range
        var (fake, _, _) = Slider();

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", value);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain($"{Clipped(value)} is outside").And.NotContain(new string('0', 81));
    }

    public static TheoryData<string> CapSurvivors => new() { "scroll_ancestor_id", "out_of_range_bounds", "number_bounds", "rejection_reason" };

    [Theory]
    [MemberData(nameof(CapSurvivors))]
    public async Task C_What_follows_a_3000_character_name_survives_ToolErrors_MessageFor(string path)
    {
        Fake fake;
        string verb = "set_value";
        string? value;
        string[] mustSurvive;
        switch (path)
        {
            case "scroll_ancestor_id":
                fake = Arrange(LongName, ListItemType);
                var ancestor = Arrange(LongName + "A", ListType, issue: false);
                Scrollable(ancestor);
                Parents((fake, ancestor), (ancestor, null));
                (verb, value) = ("scroll_into_view", null);
                mustSurvive = ["scroll with element_id el_", "snapshot"];
                break;
            case "out_of_range_bounds":
                fake = Arrange(LongName, SliderType);
                OfferRange(fake, Min, Max);
                value = new string('0', 3000) + "1000";
                mustSurvive = ["-7.25", "180.5"];
                break;
            case "number_bounds":
                fake = Arrange(LongName, SliderType);
                OfferRange(fake, Min, Max);
                value = new string('q', 3000);
                mustSurvive = ["-7.25", "180.5", "decimal point"];
                break;
            default:
                fake = Arrange(LongName, EditType);
                OfferValue(fake, readOnly: false).Setup(p => p.SetValue(It.IsAny<string>()))
                    .Throws(new ArgumentException("the provider's reason"));
                value = new string('v', 3000);
                mustSurvive = ["rejected the value", "the provider's reason"];
                break;
        }

        Func<Task> act = () => _svc.InteractAsync(fake.Id, verb, value);

        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        var seen = ToolErrors.MessageFor(ex);
        foreach (var part in mustSurvive) seen.Should().Contain(part);
        if (path == "scroll_ancestor_id")
        {
            var named = IdsIn(seen).Where(id => id != fake.Id).Distinct().ToArray();
            named.Should().ContainSingle("the ancestor's id is in what the client sees");
        }
    }

    // ---- D: a scrollable ancestor must have an axis that scrolls --------------------------------

    [Fact]
    public async Task D_An_ancestor_with_ScrollPattern_but_no_scrollable_axis_is_walked_past()
    {
        var target = Arrange("Row 40", ListItemType);
        var inert = Arrange("Static grid", DataGridType, issue: false);
        Scrollable(inert, vertical: false, horizontal: false);
        var list = Arrange("Files", ListType, issue: false);
        Scrollable(list);
        Parents((target, inert), (inert, list), (list, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        var named = IdsIn(ex.Message).Where(id => id != target.Id).Distinct().ToArray();
        named.Should().ContainSingle();
        Cache.Get(named[0]).Should().BeSameAs(list.Element, "the next REALLY scrollable ancestor");
        ex.Message.Should().NotContain("'Static grid'");
        _svc.CachedElementCount.Should().Be(2);
    }

    [Fact]
    public async Task D_A_horizontally_scrollable_ancestor_counts()
    {
        var target = Arrange("Row 40", ListItemType);
        var strip = Arrange("Tabs strip", PaneType, issue: false);
        Scrollable(strip, vertical: false, horizontal: true);
        Parents((target, strip), (strip, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("scroll with element_id").And.Contain("'Tabs strip'");
    }

    [Fact]
    public async Task D_Only_inert_scroll_hosts_above_is_the_refusal_without_an_id()
    {
        var target = Arrange("Row 40", ListItemType);
        var inert = Arrange("Static grid", DataGridType, issue: false);
        Scrollable(inert, vertical: false, horizontal: false);
        Parents((target, inert), (inert, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().NotContain("scroll with element_id");
        _svc.CachedElementCount.Should().Be(1);
    }

    // ---- E: a timeout in the ancestor walk propagates; nothing further is read or minted --------

    [Theory]
    [InlineData("parent_first")]
    [InlineData("parent_second")]
    [InlineData("probe_pattern")]
    [InlineData("probe_axis")]
    public async Task E_A_timeout_in_the_walk_propagates_untouched_with_no_further_read_of_the_target(string where)
    {
        var target = Arrange("Row 40", ListItemType);
        var group = Arrange("Group A", GroupType, issue: false);
        var timeout = FlaUiShaped.From(FlaUiShaped.UIA_E_TIMEOUT);
        timeout.Should().BeOfType<TimeoutException>("precondition");
        int? readsAtTimeout = null;
        Exception Hang() { readsAtTimeout ??= target.ProcessIdReads; return timeout; }

        switch (where)
        {
            case "parent_first":
            case "parent_second":
                _svc.ParentOf = e =>
                {
                    ParentCalls++;
                    if (where == "parent_first" || !ReferenceEquals(e, target.Element)) throw Hang();
                    return group.Element;
                };
                break;
            case "probe_pattern":
                group.Framework.Protected().Setup<IAutomationPattern<IScrollPattern>>("InitializeScrollPattern")
                    .Returns(() => throw Hang());
                group.Props[Lib.Scroll.VerticallyScrollable.Id] = () => throw Hang();
                group.Props[Lib.Scroll.HorizontallyScrollable.Id] = () => throw Hang();
                Parents((target, group), (group, null));
                break;
            default:
                // The pattern is there; reading whether an axis scrolls hangs.
                Scrollable(group);
                group.Props[Lib.Scroll.VerticallyScrollable.Id] = () => throw Hang();
                group.Props[Lib.Scroll.HorizontallyScrollable.Id] = () => throw Hang();
                Parents((target, group), (group, null));
                break;
        }

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(timeout);
        readsAtTimeout.Should().NotBeNull("precondition: the walk reached the hang");
        target.ProcessIdReads.Should().Be(readsAtTimeout!.Value, "a hung provider is not probed again after the timeout");
        _svc.CachedElementCount.Should().Be(1, "no id minted");
    }

    // ---- F: no ScrollItemPattern, but already on screen, is a success that does nothing ---------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task F_No_ScrollItemPattern_and_already_on_screen_is_a_success_with_no_walk(bool disabled)
    {
        var target = Arrange("Row 40", ListItemType, isEnabled: disabled ? () => false : null);
        SetGeometry(target, offscreen: false, 100, 200, 41, 21);
        var list = Arrange("Files", ListType, issue: false);
        Scrollable(list);
        Parents((target, list), (list, null));

        var result = await _svc.InteractAsync(target.Id, "scroll_into_view", null);

        result.ElementId.Should().Be(target.Id);
        result.Action.Should().Be("scroll_into_view");
        result.Method.Should().Be("None");
        result.Detail.Should().Be("already on screen, centre (120,210)");
        ParentCalls.Should().Be(0, "no walk for an element already in view");
        _svc.CachedElementCount.Should().Be(1, "nothing minted");
        NoInputAndNoFocus(target);
    }

    [Theory]
    [InlineData("offscreen")]
    [InlineData("zero_size")]       // empty = no area (ElementTarget's rule), not System.Drawing's all-zero IsEmpty
    [InlineData("zero_height")]
    [InlineData("all_zero")]
    [InlineData("unreadable")]      // geometry not supported: not proven on screen
    public async Task F_Off_screen_empty_or_unreadable_geometry_without_ScrollItemPattern_is_still_the_refusal(string geometry)
    {
        var target = Arrange("Row 40", ListItemType);
        switch (geometry)
        {
            case "offscreen": SetGeometry(target, offscreen: true, 100, 200, 41, 21); break;
            case "zero_size": SetGeometry(target, offscreen: false, 100, 200, 0, 0); break;
            case "zero_height": SetGeometry(target, offscreen: false, 100, 200, 41, 0); break;
            case "all_zero": SetGeometry(target, offscreen: false, 0, 0, 0, 0); break;
            default: break;   // Arrange leaves IsOffscreen and BoundingRectangle unsupported
        }
        var list = Arrange("Files", ListType, issue: false);
        Scrollable(list);
        Parents((target, list), (list, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ShouldBeTheScrollItemRefusal(ex, "ListItem", "Row 40");
        ex.Message.Should().Contain("scroll with element_id");
    }

    [Fact]
    public async Task F_A_dead_element_that_looks_on_screen_is_gone_not_already_on_screen()
    {
        var target = Arrange("Row 40", ListItemType, processId: () => 0);
        SetGeometry(target, offscreen: false, 100, 200, 41, 21);

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain($"Element '{target.Id}' is no longer available");
    }

    [Fact]
    public async Task F_With_ScrollItemPattern_an_on_screen_element_is_still_scrolled()
    {
        // F is only for elements WITHOUT the pattern: with it, ScrollIntoView runs as before (it may
        // also bring a partly visible element fully into view).
        var target = Arrange("Row 40", ListItemType);
        var scrollItem = OfferScrollItem(target);
        SetGeometry(target, offscreen: false, 100, 200, 41, 21);

        var result = await _svc.InteractAsync(target.Id, "scroll_into_view", null);

        scrollItem.Verify(p => p.ScrollIntoView(), Times.Once);
        result.Method.Should().Be("ScrollItemPattern");
    }

    // ---- G: an empty value is no value for the verbs that take none -----------------------------

    [Theory]
    [InlineData("expand")]
    [InlineData("Collapse")]
    [InlineData("scroll_into_view")]
    public async Task G_An_empty_value_is_no_value_and_reaches_the_id_lookup(string action)
    {
        Func<Task> act = () => _svc.InteractAsync("el_999999", action, "");

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain("el_999999");
    }

    [Fact]
    public async Task G_An_empty_value_on_expand_expands()
    {
        var fake = Arrange("Documents", TreeItemType);
        var ec = OfferExpandCollapse(fake, ExpandCollapseState.Collapsed);

        var result = await _svc.InteractAsync(fake.Id, "expand", "");

        ec.Pattern.Verify(p => p.Expand(), Times.Once);
        result.Detail.Should().Be("now Expanded");
    }

    // ---- H: degenerate bounds are unknown bounds ---------------------------------------------

    [Theory]
    [InlineData("min_nan", "-1000", "now -1000")]
    [InlineData("max_nan", "1000", "now 1000")]
    [InlineData("both_nan", "5000", "now 5000")]
    [InlineData("inverted", "5000", "now 5000")]
    public async Task H_A_NaN_or_inverted_bound_is_unknown_so_it_is_not_checked_and_not_reported(string bounds, string value, string detail)
    {
        var (fake, range, _) = DegenerateSlider(bounds);

        var result = await _svc.InteractAsync(fake.Id, "set_value", value);

        range.Verify(p => p.SetValue(double.Parse(value, CultureInfo.InvariantCulture)), Times.Once);
        result.Detail.Should().Be(detail, "no '(range …)' suffix: both bounds are not known");
    }

    [Theory]
    [InlineData("min_nan", "of at most 100")]
    [InlineData("max_nan", "of at least 0")]
    [InlineData("both_nan", "")]
    [InlineData("inverted", "")]
    public async Task H_The_number_error_quotes_only_real_bounds(string bounds, string quoted)
    {
        var (fake, _, _) = DegenerateSlider(bounds);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "loud");

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain($"needs a number{(quoted.Length > 0 ? " " + quoted : "")}, with '.' as the decimal point")
            .And.NotContain("NaN").And.NotContain("between");
    }

    [Fact]
    public async Task H_With_a_NaN_minimum_the_known_maximum_is_still_checked()
    {
        var (fake, range, _) = DegenerateSlider("min_nan");

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "101");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("of at most 100; 101 is outside");
        range.Verify(p => p.SetValue(It.IsAny<double>()), Times.Never);
    }

    private (Fake, Mock<IRangeValuePattern>, Mock<IValuePattern>) DegenerateSlider(string bounds) => bounds switch
    {
        "min_nan" => Slider(minimum: double.NaN, maximum: 100.0),
        "max_nan" => Slider(minimum: 0.0, maximum: double.NaN),
        "both_nan" => Slider(minimum: double.NaN, maximum: double.NaN),
        _ => Slider(minimum: 100.0, maximum: 0.0),   // minimum > maximum
    };

    // ============================================================================================
    // GREEN round 2: the coordinator's settlement of the open points, pinned.
    // ============================================================================================

    [Fact]
    public async Task S1_An_IsPassword_read_that_throws_is_treated_as_a_password_never_read_never_echoed()
    {
        var fake = Arrange("Password", EditType);
        fake.Props[Lib.Element.IsPassword.Id] = Throws(FlaUiShaped.From(FlaUiShaped.E_FAIL));
        var valuePattern = OfferValue(fake, readOnly: false, current: "hunter2", land: _ => "hunter2");

        var result = await _svc.InteractAsync(fake.Id, "set_value", "s3cret-new");

        valuePattern.Verify(p => p.SetValue("s3cret-new"), Times.Once);
        result.Detail.Should().Be("replaced the whole value");
        fake.ReadsOf(Lib.Value.Value).Should().Be(0);
    }

    [Fact]
    public async Task S1_An_IsPassword_read_that_throws_makes_a_rejection_quote_no_value()
    {
        var fake = Arrange("Password", EditType);
        fake.Props[Lib.Element.IsPassword.Id] = Throws(FlaUiShaped.From(FlaUiShaped.E_FAIL));
        OfferValue(fake, readOnly: false).Setup(p => p.SetValue(It.IsAny<string>()))
            .Throws(new ArgumentException("bad value s3cret-new"));

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "s3cret-new");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be("Edit 'Password' rejected the value.");
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("win32")]
    public async Task S2_A_password_rejection_is_exactly_the_bare_sentence_and_keeps_the_provider_exception(string kind)
    {
        var fake = Arrange("Password", EditType);
        fake.Props[Lib.Element.IsPassword.Id] = () => true;
        // A provider message that echoes the value: it must not reach the caller for a password.
        Exception thrown = kind == "argument" ? new ArgumentException("'s3cret-new' is not valid") : FlaUiShaped.From(0x80020005);
        OfferValue(fake, readOnly: false).Setup(p => p.SetValue(It.IsAny<string>())).Throws(thrown);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "s3cret-new");

        var ex = (await act.Should().ThrowExactlyAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Be("Edit 'Password' rejected the value.");
        ex.InnerException.Should().BeSameAs(thrown);
        ToolErrors.MessageFor(ex).Should().NotContain("s3cret-new");
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task S2_A_rejection_keeps_the_provider_exception_as_its_inner_exception(string pattern, string kind)
    {
        var thrown = Rejection(kind);
        var (fake, value, _) = RejectingControl(pattern, thrown);

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", value);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.InnerException.Should().BeSameAs(thrown);
    }

    [Fact]
    public async Task S3_The_unchanged_check_compares_raw_strings_not_sanitised_ones()
    {
        // "x " asked for; the control trims it and still shows "x", as before. Sanitised, the two
        // would be equal and this would pass as a success; raw, the value did not change.
        var fake = Arrange("Path", EditType);
        OfferValue(fake, readOnly: false, current: "x", land: v => v.Trim());

        Func<Task> act = () => _svc.InteractAsync(fake.Id, "set_value", "x ");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be("Edit 'Path' accepted the call but its value did not change: it still shows 'x'.");
    }

    [Fact]
    public async Task S3_A_raw_change_the_sanitised_quote_cannot_show_is_still_a_success()
    {
        // before "ab", after "a\u0007b": raw they differ (the control changed), sanitised they would not.
        var fake = Arrange("Path", EditType);
        OfferValue(fake, readOnly: false, current: "ab", land: _ => "a\u0007b");

        var result = await _svc.InteractAsync(fake.Id, "set_value", "zzz");

        result.Detail.Should().Be("now 'ab'");
    }

    public static TheoryData<string> TimedOutReads => new() { "is_password", "before", "after", "is_offscreen", "bounds" };

    [Theory]
    [MemberData(nameof(TimedOutReads))]
    public async Task S4_A_timeout_from_a_read_propagates_untouched_and_the_target_is_not_probed_again(string read)
    {
        var timeout = FlaUiShaped.From(FlaUiShaped.UIA_E_TIMEOUT);
        int? readsAtTimeout = null;
        Fake fake = null!;
        Func<object> Hang() => () => { readsAtTimeout ??= fake.ProcessIdReads; throw timeout; };
        string verb;
        string? value = null;

        if (read is "is_offscreen" or "bounds")
        {
            fake = Arrange("Row 40", ListItemType);
            SetGeometry(fake, offscreen: false, 100, 200, 41, 21);
            fake.Props[read == "is_offscreen" ? Lib.Element.IsOffscreen.Id : Lib.Element.BoundingRectangle.Id] = Hang();
            verb = "scroll_into_view";
        }
        else
        {
            fake = Arrange("Path", EditType);
            var valuePattern = OfferValue(fake, readOnly: false, current: "old");
            var valueId = Lib.Value.Value.Id;
            if (read == "is_password") fake.Props[Lib.Element.IsPassword.Id] = Hang();
            else if (read == "before") fake.Props[valueId] = Hang();
            else valuePattern.Setup(p => p.SetValue(It.IsAny<string>())).Callback(() => fake.Props[valueId] = Hang());
            (verb, value) = ("set_value", "zzz");
        }

        Func<Task> act = () => _svc.InteractAsync(fake.Id, verb, value);

        (await act.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(timeout);
        readsAtTimeout.Should().NotBeNull("precondition: the read was reached");
        fake.ProcessIdReads.Should().Be(readsAtTimeout!.Value, "a hung provider is not probed again (R23)");
        _svc.CachedElementCount.Should().Be(1);
    }

    [Fact]
    public async Task S4_Geometry_that_throws_an_ordinary_fault_is_unreadable_so_the_refusal_stands()
    {
        var target = Arrange("Row 40", ListItemType);
        SetGeometry(target, offscreen: false, 100, 200, 41, 21);
        target.Props[Lib.Element.IsOffscreen.Id] = Throws(FlaUiShaped.From(FlaUiShaped.E_FAIL));
        Parents((target, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ShouldBeTheScrollItemRefusal(ex, "ListItem", "Row 40");
    }

    [Theory]
    [InlineData(100.0, 200.0, 0.0, 0.0)]    // no area at a non-zero origin (Rectangle.IsEmpty is false here)
    [InlineData(100.0, 200.0, 41.0, 0.0)]
    [InlineData(100.0, 200.0, 0.0, 21.0)]
    public async Task S5_After_ScrollIntoView_bounds_without_area_are_still_offscreen(double l, double t, double w, double h)
    {
        var fake = Arrange("Row 40", ListItemType);
        OfferScrollItem(fake, onScroll: () => SetGeometry(fake, offscreen: false, l, t, w, h));

        var result = await _svc.InteractAsync(fake.Id, "scroll_into_view", null);

        result.Detail.Should().Be("the control still reports offscreen");
    }

    [Fact]
    public async Task S4_An_IsOffscreen_the_provider_cannot_answer_is_not_on_screen_even_with_real_bounds()
    {
        // "Provably on screen" needs BOTH answers: good bounds alone do not make it so.
        var target = Arrange("Row 40", ListItemType);
        SetGeometry(target, offscreen: false, 100, 200, 41, 21);
        target.Props.Remove(Lib.Element.IsOffscreen.Id);
        Parents((target, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ShouldBeTheScrollItemRefusal(ex, "ListItem", "Row 40");
        ParentCalls.Should().BeGreaterThan(0, "the walk ran: it was not treated as already on screen");
    }

    [Fact]
    public async Task S5_Without_ScrollItemPattern_a_zero_width_element_is_not_already_on_screen()
    {
        var target = Arrange("Row 40", ListItemType);
        SetGeometry(target, offscreen: false, 100, 200, 0, 21);
        Parents((target, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task S6_An_axis_property_that_is_not_supported_does_not_make_an_ancestor_scrollable()
    {
        var target = Arrange("Row 40", ListItemType);
        var odd = Arrange("Odd host", PaneType, issue: false);
        Scrollable(odd);
        odd.Props.Remove(Lib.Scroll.VerticallyScrollable.Id);
        odd.Props.Remove(Lib.Scroll.HorizontallyScrollable.Id);
        Parents((target, odd), (odd, null));

        Func<Task> act = () => _svc.InteractAsync(target.Id, "scroll_into_view", null);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().NotContain("scroll with element_id");
    }
}
