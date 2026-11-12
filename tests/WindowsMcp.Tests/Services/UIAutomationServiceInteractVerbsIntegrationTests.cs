using System.Reflection;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-2 through the REAL service and real UIA, on the controls this process owns
/// (<see cref="OwnedControlsWindow"/>: two disabled Win32 buttons and an enabled one). What a
/// fake element cannot prove: that the real pattern lookups for the new verbs come back "absent"
/// on a real control and become the right refusal, that the DEFAULT <c>ParentOf</c> walks the
/// real control-view tree (and survives a destroyed element), and that scroll_into_view on a
/// disabled control is not refused as disabled. Nothing is clicked, typed, expanded or set — no
/// fixture control offers ExpandCollapse, RangeValue, Value or ScrollItem, which each row checks
/// as a precondition — and the input mock is STRICT.
/// <para>
/// The success paths (a tree node expanding, a slider moving, a list row scrolling into view, a
/// real scrollable ancestor named) are in <see cref="UIAutomationServiceInteractVerbsRealPatternTests"/>
/// below, on their own fixture (<see cref="OwnedPatternControlsWindow"/>): adding children to
/// <see cref="OwnedControlsWindow"/> would break the exact-match counts other tests rely on.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(DesktopCollection.Name)]
public sealed class UIAutomationServiceInteractVerbsIntegrationTests : IDisposable
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly OwnedControlsWindow _window = new();
    private readonly Mock<IInputService> _input = new(MockBehavior.Strict);
    private readonly UIAutomationService _svc;

    public UIAutomationServiceInteractVerbsIntegrationTests()
    {
        _svc = new UIAutomationService(_input.Object, new Mock<IWindowService>().Object);
    }

    public void Dispose()
    {
        _svc.Dispose();
        _window.Dispose();
    }

    private async Task<ElementInfo> FindAsync(string name)
    {
        _window.Exists.Should().BeTrue("precondition: the fixture window was created");
        ElementInfo? hit = null;
        for (int attempt = 0; attempt < 5 && hit is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(200);
            var found = await _svc.FindElementAsync(name, FindKind.Any, FindScope.Window, _window.Title, includeOffscreen: true);
            hit = found.Matches.FirstOrDefault(m => m.Name == name);
        }
        hit.Should().NotBeNull($"precondition: UIA sees the fixture control '{name}'");
        return hit!;
    }

    private AutomationElement ElementOf(string id) => ((ElementCache<AutomationElement>)typeof(UIAutomationService)
        .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(_svc)!).Get(id);

    /// <summary>The row's premise: a Win32 push button offers none of the four patterns.</summary>
    private void ShouldOfferNoneOfTheNewPatterns(string id)
    {
        var el = ElementOf(id);
        el.Patterns.ExpandCollapse.IsSupported.Should().BeFalse("precondition: a Win32 button has no ExpandCollapsePattern");
        el.Patterns.RangeValue.IsSupported.Should().BeFalse("precondition: no RangeValuePattern");
        el.Patterns.Value.IsSupported.Should().BeFalse("precondition: no ValuePattern");
        el.Patterns.ScrollItem.IsSupported.Should().BeFalse("precondition: no ScrollItemPattern");
    }

    [Fact]
    public async Task Expand_on_a_real_control_without_the_pattern_is_the_D10_refusal_and_touches_nothing()
    {
        var button = await FindAsync(_window.EnabledButton);
        ShouldOfferNoneOfTheNewPatterns(button.ElementId);
        var foreground = GetForegroundWindow();

        Func<Task> act = () => _svc.InteractAsync(button.ElementId, "expand", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Be(UiRefusals.PatternNotSupported("ExpandCollapsePattern", "Button", _window.EnabledButton).Message);
        GetForegroundWindow().Should().Be(foreground);
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Set_value_on_a_real_control_with_neither_pattern_is_the_D10_refusal_and_types_nothing()
    {
        var button = await FindAsync(_window.EnabledButton);
        ShouldOfferNoneOfTheNewPatterns(button.ElementId);

        Func<Task> act = () => _svc.InteractAsync(button.ElementId, "set_value", "5");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Be(UiRefusals.PatternNotSupported("RangeValuePattern or ValuePattern", "Button", _window.EnabledButton).Message);
        _input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // acts: false — a disabled control is not refused as disabled
    public async Task Scroll_into_view_without_ScrollItemPattern_on_a_real_on_screen_control_is_already_in_view(bool disabled)
    {
        // Round 2 (F) re-pin: this was "…walks_the_real_tree_and_refuses_with_the_scroll_advice". A
        // control already on screen has nothing to scroll to, so it is a success that does nothing.
        // The real walk-and-refuse path is UIAutomationServiceInteractVerbsRealPatternTests'
        // Scroll_into_view_without_ScrollItemPattern_names_the_real_scrollable_parent_by_a_usable_id.
        var name = disabled ? _window.DisabledButton : _window.EnabledButton;
        var button = await FindAsync(name);
        ShouldOfferNoneOfTheNewPatterns(button.ElementId);
        button.IsOffscreen.Should().BeFalse("precondition: the fixture button is on screen");
        var b = button.Bounds;
        b.Should().NotBeNull();
        (b!.Width > 0 && b.Height > 0).Should().BeTrue("precondition: it has an area");
        var cachedBefore = _svc.CachedElementCount;

        var result = await _svc.InteractAsync(button.ElementId, "scroll_into_view", null);

        result.Method.Should().Be("None");
        result.Detail.Should().Be($"already on screen, centre ({b.X + b.Width / 2},{b.Y + b.Height / 2})");
        _svc.CachedElementCount.Should().Be(cachedBefore, "nothing minted");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Default_ParentOf_returns_the_real_parent_window()
    {
        var button = await FindAsync(_window.EnabledButton);

        var parent = _svc.ParentOf(ElementOf(button.ElementId));

        parent.Should().NotBeNull();
        parent!.Name.Should().Be(_window.Title, "a Win32 child's control-view parent is its top-level window");
    }

    [Fact]
    public async Task Default_ParentOf_on_a_destroyed_element_is_no_parent_not_a_fault()
    {
        var button = await FindAsync(_window.EnabledButton);
        var el = ElementOf(button.ElementId);
        _window.Close();
        _window.Exists.Should().BeFalse("precondition: the fixture window is gone");

        var act = () => _svc.ParentOf(el);

        // Whether UIA answers a dead element's parent with an error or a default is the platform's
        // choice; what is pinned is that the seam never throws (an error IS "no parent").
        act.Should().NotThrow("any exception from the walker is 'no parent'");
    }
}

/// <summary>
/// E-2 GREEN: the SUCCESS paths of the four verbs through the real service, real UIA and the
/// real Win32 / common-control proxies (<see cref="OwnedPatternControlsWindow"/>: a trackbar, two
/// EDITs, a short LISTBOX, a TreeView and a plain button inside a parent with a scroll bar). The
/// fake-element rows in <see cref="UIAutomationServiceInteractVerbsTests"/> prove the logic; these
/// prove the pattern reads and calls it relies on behave that way on a real provider — the lesson
/// of <c>DiskServiceReclaimableIntegrationTests</c>. Every check of the outcome is read from the
/// CONTROL with a window message, not back through UIA. Every action is a window message to this
/// process's own controls, and the input mock is STRICT: no pointer movement, no keystroke (the
/// parity item's "Done when").
/// <para>
/// FOREGROUND (observed while writing these, 2026-11-12): UIA's Win32 proxies ACTIVATE the
/// control's top-level window on <c>ValuePattern.SetValue</c> (EDIT), <c>RangeValuePattern.SetValue</c>
/// (trackbar) and <c>ExpandCollapsePattern.Expand</c> (TreeView item) — a raw FlaUI call outside the
/// service does the same, so it is the provider, not the service. <c>ScrollItemPattern.ScrollIntoView</c>
/// on a LISTBOX row does not. That is a CHARACTERISATION, not a requirement, so no row asserts on
/// the foreground after an acting verb (round 2, decision I: the description stops promising "no
/// focus change" and says a provider may bring the control's window forward — pinned in
/// UIAutomationToolsTests). Only scroll_into_view, which moves only the view, keeps a foreground
/// assertion.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
// A window of ours appears, closes, and is acted on through UIA: DesktopCollection's rule.
[Collection(DesktopCollection.Name)]
public sealed class UIAutomationServiceInteractVerbsRealPatternTests : IDisposable
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly OwnedPatternControlsWindow _window = new();
    private readonly Mock<IInputService> _input = new(MockBehavior.Strict);
    private readonly UIAutomationService _svc;

    public UIAutomationServiceInteractVerbsRealPatternTests()
    {
        _svc = new UIAutomationService(_input.Object, new Mock<IWindowService>().Object);
    }

    public void Dispose()
    {
        _svc.Dispose();
        _window.Dispose();
    }

    private ElementCache<AutomationElement> Cache => (ElementCache<AutomationElement>)typeof(UIAutomationService)
        .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!
        .GetValue(_svc)!;

    /// <summary>Runs <paramref name="work"/> on the service's own STA thread with its own automation.</summary>
    private Task<T> OnStaAsync<T>(Func<FlaUI.UIA3.UIA3Automation, T> work)
    {
        var automation = (FlaUI.UIA3.UIA3Automation)typeof(UIAutomationService)
            .GetField("_automation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_svc)!;
        var onSta = typeof(UIAutomationService).GetMethod("OnStaAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(typeof(T));
        Func<T> bound = () => work(automation);
        return (Task<T>)onSta.Invoke(_svc, [bound, CancellationToken.None])!;
    }

    /// <summary>
    /// Issues an id for the control behind <paramref name="hwnd"/> (or the descendant of it named
    /// <paramref name="child"/>) exactly as a snapshot would: an element of the service's own
    /// automation, put in its cache.
    /// </summary>
    private Task<string> IssueAsync(IntPtr hwnd, string? child = null)
    {
        _window.Exists.Should().BeTrue("precondition: the fixture window was created");
        hwnd.Should().NotBe(IntPtr.Zero, "precondition: the fixture control was created");
        return OnStaAsync(automation =>
        {
            var el = automation.FromHandle(hwnd);
            if (child is not null)
                el = el.FindFirstDescendant(cf => cf.ByName(child))
                    ?? throw new InvalidOperationException($"precondition: UIA sees no '{child}' under the control");
            return Cache.Add(el);
        });
    }

    private Task<T> ReadAsync<T>(string id, Func<AutomationElement, T> read) => OnStaAsync(_ => read(Cache.Get(id)));

    // ---- set_value: RangeValuePattern on a real trackbar ---------------------------------------

    [Fact]
    public async Task Set_value_on_a_real_trackbar_moves_it_and_the_detail_is_the_value_the_control_reports()
    {
        var id = await IssueAsync(_window.Slider);
        (await ReadAsync(id, el => el.Patterns.RangeValue.IsSupported)).Should().BeTrue("precondition: a trackbar offers RangeValuePattern");
        _window.SliderPosition.Should().Be(OwnedPatternControlsWindow.SliderStart, "precondition");

        var result = await _svc.InteractAsync(id, "set_value", "30");

        result.Method.Should().Be("RangeValuePattern");
        // The fixture's range is 0..100 on purpose: the Win32 trackbar proxy reports RangeValue as a
        // PERCENTAGE of the trackbar's own range (a 5..50 trackbar read as 0..100, and set_value 30
        // landed on position 18 and re-read as 28), so only a 0..100 trackbar maps 1:1.
        result.Detail.Should().Be($"now 30 (range {OwnedPatternControlsWindow.SliderMin} to {OwnedPatternControlsWindow.SliderMax})");
        _window.SliderPosition.Should().Be(30, "the control itself moved");
        _input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("-1")]   // one below the real minimum
    [InlineData("101")]  // one above the real maximum
    [InlineData("abc")]
    public async Task Set_value_on_a_real_trackbar_outside_its_real_range_is_refused_and_it_does_not_move(string value)
    {
        var id = await IssueAsync(_window.Slider);

        Func<Task> act = () => _svc.InteractAsync(id, "set_value", value);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("value");
        ex.Message.Should().Contain($"between {OwnedPatternControlsWindow.SliderMin} and {OwnedPatternControlsWindow.SliderMax}",
            "the bounds are the ones the real provider reports");
        _window.SliderPosition.Should().Be(OwnedPatternControlsWindow.SliderStart);
        _input.VerifyNoOtherCalls();
    }

    // ---- set_value: ValuePattern on real EDITs -------------------------------------------------

    [Fact]
    public async Task Set_value_on_a_real_edit_replaces_its_text_through_ValuePattern()
    {
        var id = await IssueAsync(_window.Edit);
        (await ReadAsync(id, el => (el.Patterns.RangeValue.IsSupported, el.Patterns.Value.IsSupported)))
            .Should().Be((false, true), "precondition: an EDIT offers ValuePattern and no RangeValuePattern");

        var result = await _svc.InteractAsync(id, "set_value", "new text 42");

        result.Method.Should().Be("ValuePattern");
        // Round 2 (A) re-pin: was "replaced the whole value"; the write is verified by reading back.
        result.Detail.Should().Be("now 'new text 42'");
        OwnedPatternControlsWindow.TextOf(_window.Edit).Should().Be("new text 42");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Set_value_on_a_real_combo_that_ignores_the_value_is_refused_quoting_what_it_still_shows()
    {
        // A drop-down-list COMBOBOX's ValuePattern answers OK to any string and only moves for an
        // item's name: the review's "succeeds with the value unchanged" case, on a real provider.
        var id = await IssueAsync(_window.Combo);
        (await ReadAsync(id, el => (el.Patterns.RangeValue.IsSupported, el.Patterns.Value.Pattern.IsReadOnly.Value, el.Patterns.Value.Pattern.Value.Value)))
            .Should().Be((false, false, "Apple"), "precondition: a writable ValuePattern showing the first item");

        Func<Task> act = () => _svc.InteractAsync(id, "set_value", "zzz");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().MatchRegex("(?i)did not change|didn't change|unchanged").And.Contain("'Apple'");
        OwnedPatternControlsWindow.TextOf(_window.Combo).Should().Be("Apple", "the control did not move");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Set_value_on_a_real_combo_reports_the_item_it_normalised_to()
    {
        var id = await IssueAsync(_window.Combo);

        var result = await _svc.InteractAsync(id, "set_value", "banana");

        result.Method.Should().Be("ValuePattern");
        result.Detail.Should().Be("now 'Banana'", "the provider matched the item case-insensitively; the Detail is what it shows");
        OwnedPatternControlsWindow.TextOf(_window.Combo).Should().Be("Banana");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Set_value_on_a_real_password_edit_sets_it_and_never_reports_its_value()
    {
        var id = await IssueAsync(_window.PasswordEdit);
        (await ReadAsync(id, el => el.Properties.IsPassword.Value)).Should().BeTrue("precondition: ES_PASSWORD reports IsPassword");

        var result = await _svc.InteractAsync(id, "set_value", "s3cret-new");

        result.Method.Should().Be("ValuePattern");
        result.Detail.Should().Be("replaced the whole value");
        OwnedPatternControlsWindow.TextOf(_window.PasswordEdit).Should().Be("s3cret-new", "the control itself took it");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Set_value_on_a_real_read_only_edit_is_refused_and_its_text_is_unchanged()
    {
        var id = await IssueAsync(_window.ReadOnlyEdit);
        (await ReadAsync(id, el => el.Patterns.Value.Pattern.IsReadOnly.Value))
            .Should().BeTrue("precondition: an ES_READONLY edit reports a read-only ValuePattern");

        Func<Task> act = () => _svc.InteractAsync(id, "set_value", "overwritten");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().ContainEquivalentOf("read-only");
        OwnedPatternControlsWindow.TextOf(_window.ReadOnlyEdit).Should().Be(OwnedPatternControlsWindow.ReadOnlyEditText);
        _input.VerifyNoOtherCalls();
    }

    // ---- expand / collapse: ExpandCollapsePattern on a real TreeView ---------------------------

    [Fact]
    public async Task Expand_and_collapse_a_real_tree_node_move_it_and_say_already_when_it_is_there()
    {
        var id = await IssueAsync(_window.Tree, _window.Folder);
        _window.FolderExpanded.Should().BeFalse("precondition: the folder starts collapsed");

        var expanded = await _svc.InteractAsync(id, "expand", null);
        expanded.Method.Should().Be("ExpandCollapsePattern");
        expanded.Detail.Should().Be("now Expanded");
        _window.FolderExpanded.Should().BeTrue("the control itself expanded");

        var again = await _svc.InteractAsync(id, "expand", null);
        again.Detail.Should().Be("already Expanded");
        _window.FolderExpanded.Should().BeTrue("an expand on an expanded node must not toggle it shut");

        var collapsed = await _svc.InteractAsync(id, "collapse", null);
        collapsed.Detail.Should().Be("now Collapsed");
        _window.FolderExpanded.Should().BeFalse();

        var collapsedAgain = await _svc.InteractAsync(id, "collapse", null);
        collapsedAgain.Detail.Should().Be("already Collapsed");
        _window.FolderExpanded.Should().BeFalse();

        _input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("expand")]
    [InlineData("collapse")]
    public async Task Expand_or_collapse_on_a_real_tree_leaf_is_refused_as_a_leaf(string verb)
    {
        var id = await IssueAsync(_window.Tree, _window.Leaf);
        (await ReadAsync(id, el => el.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value))
            .Should().Be(FlaUI.Core.Definitions.ExpandCollapseState.LeafNode, "precondition: a node with no children is a leaf");

        Func<Task> act = () => _svc.InteractAsync(id, verb, null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain($"'{_window.Leaf}'").And.Contain("leaf node");
        _input.VerifyNoOtherCalls();
    }

    // ---- scroll_into_view: ScrollItemPattern on a real list row, and the real ancestor walk ----

    [Fact]
    public async Task Scroll_into_view_on_a_real_list_row_scrolls_the_list_and_the_detail_is_on_screen()
    {
        var last = _window.Row(OwnedPatternControlsWindow.RowCount - 1);
        var id = await IssueAsync(_window.ListBox, last);
        (await ReadAsync(id, el => (el.Patterns.ScrollItem.IsSupported, el.IsOffscreen)))
            .Should().Be((true, true), "precondition: the last row of a short list box offers ScrollItemPattern and starts off-screen");
        _window.ListTopIndex.Should().Be(0, "precondition");
        var foreground = GetForegroundWindow();

        var result = await _svc.InteractAsync(id, "scroll_into_view", null);

        result.Method.Should().Be("ScrollItemPattern");
        result.Detail.Should().StartWith("now on screen, centre (");
        _window.ListTopIndex.Should().BeGreaterThan(0, "the list itself scrolled");
        GetForegroundWindow().Should().Be(foreground,
            $"scroll_into_view moves only the view; the LISTBOX proxy does not activate the window (fixture window is {_window.Handle})");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Scroll_into_view_without_ScrollItemPattern_on_a_real_on_screen_button_is_already_on_screen()
    {
        // Round 2 (F), on a real provider: nothing to scroll, nothing minted, no pointer.
        var id = await IssueAsync(_window.Button);
        var (scrollItem, offscreen, rect) = await ReadAsync(id, el => (el.Patterns.ScrollItem.IsSupported, el.IsOffscreen, el.BoundingRectangle));
        (scrollItem, offscreen).Should().Be((false, false), "precondition: an on-screen push button without ScrollItemPattern");
        var cachedBefore = _svc.CachedElementCount;

        var result = await _svc.InteractAsync(id, "scroll_into_view", null);

        result.Method.Should().Be("None");
        result.Detail.Should().Be($"already on screen, centre ({rect.Left + rect.Width / 2},{rect.Top + rect.Height / 2})");
        _svc.CachedElementCount.Should().Be(cachedBefore);
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Scroll_into_view_without_ScrollItemPattern_names_the_real_scrollable_parent_by_a_usable_id()
    {
        // The 0x0 button: no on-screen point, so F does not apply and the real walk runs. (A button
        // clipped below the client area, or one without WS_VISIBLE, cannot stand in: UIA's Win32
        // proxy reports both IsOffscreen=false with a non-empty rect.)
        var id = await IssueAsync(_window.ZeroButton);
        var (scrollItem, rect) = await ReadAsync(id, el => (el.Patterns.ScrollItem.IsSupported, el.BoundingRectangle));
        scrollItem.Should().BeFalse("precondition: a push button has no ScrollItemPattern");
        (rect.Width <= 0 || rect.Height <= 0).Should().BeTrue($"precondition: the zero-size button has no area ({rect})");
        (await OnStaAsync(a =>
        {
            var scroll = a.FromHandle(_window.Handle).Patterns.Scroll;
            return scroll.IsSupported && scroll.Pattern.VerticallyScrollable.Value;
        })).Should().BeTrue("precondition: the window offers ScrollPattern AND scrolls vertically (D)");

        Func<Task> act = () => _svc.InteractAsync(id, "scroll_into_view", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain($"Button '{_window.ZeroButtonName}' ({id})");
        var named = System.Text.RegularExpressions.Regex.Match(ex.Message, @"scroll with element_id (el_\d+)");
        named.Success.Should().BeTrue($"the refusal names the container's id: {ex.Message}");
        (await ReadAsync(named.Groups[1].Value, el => el.Name)).Should().Be(_window.Title,
            "the id resolves to the real parent window, walked through the default ParentOf");
        // "an id to pass to scroll": scroll(element_id) aims at ElementTarget.CentreOf of what
        // GetElementAsync returns for it, so that must resolve to an on-screen point.
        var info = await _svc.GetElementAsync(named.Groups[1].Value);
        var centre = () => ElementTarget.CentreOf(info);
        centre.Should().NotThrow("the named container is a usable scroll target");
        _input.VerifyNoOtherCalls();
    }
}
