using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// D-10 R11 through the REAL service and real UIA: <c>UiRefusalsTests</c> proves the translation,
/// these prove the service routes what the target throws through it. The targets are Win32
/// controls this process owns (<see cref="OwnedControlsWindow"/>): disabled ones, which must be
/// refused, and destroyed ones, whose ids outlived them. Nothing is clicked or typed: the input
/// mock is STRICT, so a physical click or a keystroke fails the test instead of happening.
/// <para>
/// Round 3 (R24c) correction: the round-2 comment said "no foreground, no focus". That was false —
/// an acting verb on a DISABLED Win32 control moved the foreground onto the control before the
/// refusal came back (a 5 ms foreground logger showed it), which is what made
/// <c>UIAutomationServiceElementCacheIntegrationTests</c> fail intermittently in parallel. R24a makes
/// "the foreground is unchanged" a REQUIREMENT, asserted below with <c>GetForegroundWindow</c>
/// before and after every acting verb; until it holds, these rows can move the foreground to the
/// fixture window (which then closes). Integration, in <see cref="DesktopCollection"/>.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
// A window of ours appears and closes, and (until R24a holds) an acting verb on a disabled control
// can move the foreground: DesktopCollection's membership rule.
[Collection(DesktopCollection.Name)]
public sealed class UIAutomationServiceRefusalIntegrationTests : IDisposable
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly OwnedControlsWindow _window = new();
    private readonly Mock<IInputService> _input = new(MockBehavior.Strict);
    private readonly UIAutomationService _svc;

    public UIAutomationServiceRefusalIntegrationTests()
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
        // Retried for the same reason as UIAutomationServiceWaitReleaseIntegrationTests.MatchCountAsync:
        // a desktop enumeration that fails mid-walk is skipped by design (D-5) and finds nothing.
        for (int attempt = 0; attempt < 5 && hit is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(200);
            var found = await _svc.FindElementAsync(name, FindKind.Any, FindScope.Window, _window.Title, includeOffscreen: true);
            hit = found.Matches.FirstOrDefault(m => m.Name == name);
        }
        hit.Should().NotBeNull($"precondition: UIA sees the fixture control '{name}'");
        return hit!;
    }

    // ---- element not enabled ------------------------------------------------------------------

    /// <summary>
    /// R28 (found in round 3): the fixture's control NAMES contain the word "disabled"
    /// ("&lt;token&gt; disabled button"), so a message that merely quotes the name — "TogglePattern
    /// not supported on Button '… disabled button'" — satisfied every round-2 "says it is disabled"
    /// check. The name is cut out before the word is looked for.
    /// </summary>
    private static string Unnamed(Exception ex, string name) => ex.Message.Replace(name, "<name>", StringComparison.Ordinal);

    [Theory]
    [InlineData("invoke")]
    [InlineData("click")]   // click prefers InvokePattern: the refusal must come from the pattern, not a physical click
    public async Task Interact_on_a_disabled_button_is_a_caller_facing_refusal_saying_it_is_disabled(string action)
    {
        var button = await FindAsync(_window.DisabledButton);
        button.IsEnabled.Should().BeFalse("precondition: the fixture button is disabled");
        var foreground = GetForegroundWindow();

        Func<Task> act = () => _svc.InteractAsync(button.ElementId, action, null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>(
            "UIA_E_ELEMENTNOTENABLED surfaced as FlaUI's ElementNotEnabledException is masked by the SDK")).Which;
        var foregroundAfter = GetForegroundWindow();
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        Unnamed(ex, _window.DisabledButton).Should().ContainEquivalentOf("disabled");
        ex.Message.Should().Contain(action, "the refusal names the action that was refused");
        foregroundAfter.Should().Be(foreground, "R24a: refused before anything moved the foreground");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Interact_toggle_on_a_disabled_checkbox_is_a_caller_facing_refusal_saying_it_is_disabled()
    {
        var box = await FindAsync(_window.DisabledCheckBox);
        box.IsEnabled.Should().BeFalse("precondition: the fixture check box is disabled");
        var foreground = GetForegroundWindow();

        Func<Task> act = () => _svc.InteractAsync(box.ElementId, "toggle", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        var foregroundAfter = GetForegroundWindow();
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        Unnamed(ex, _window.DisabledCheckBox).Should().ContainEquivalentOf("disabled").And.Contain("toggle");
        // R28: "changed nothing" has two halves. Round 2 checked only the check state; the refusal
        // also moved the foreground onto the control (R24a).
        (await _svc.GetElementAsync(box.ElementId)).IsChecked.Should().NotBe(true, "a refused toggle did not toggle");
        foregroundAfter.Should().Be(foreground, "a refused toggle did not move the foreground either");
        _input.VerifyNoOtherCalls();
    }

    // ---- D-10 R24a: refused BEFORE any pattern call or focus ---------------------------------
    //
    // An acting verb on a target that reports IsEnabled == false is refused up front with the same
    // "disabled" refusal, so the foreground window is unchanged afterwards. focus is NOT exempt:
    // focusing a disabled control is refused too.

    public static TheoryData<string> ActingPaths => new()
    {
        "interact_invoke", "interact_toggle", "interact_click", "interact_select", "interact_select_item",
        "interact_type", "interact_focus", "focus",
    };

    private static string ActionOf(string path) => path switch
    {
        "interact_select_item" => "select",
        "focus" => "focus",
        _ => path["interact_".Length..],
    };

    [Theory]
    [MemberData(nameof(ActingPaths))]
    public async Task An_acting_verb_on_a_disabled_control_is_refused_before_it_moves_the_foreground(string path)
    {
        var button = await FindAsync(_window.DisabledButton);
        button.IsEnabled.Should().BeFalse("precondition: the fixture button is disabled");
        var foreground = GetForegroundWindow();

        Func<Task> act = () => Call(path, button.ElementId);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>(
            "the gate refuses a disabled target before any pattern call, Expand, child search, Focus or input")).Which;
        var foregroundAfter = GetForegroundWindow();
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        Unnamed(ex, _window.DisabledButton).Should().ContainEquivalentOf("disabled",
            "the refusal says the control is disabled, not merely quotes a name that contains the word");
        ex.Message.Should().Contain(ActionOf(path), "the refusal names the action that was refused");
        foregroundAfter.Should().Be(foreground, "a refusal must have no side effect: the foreground is where it was");
        _input.VerifyNoOtherCalls();
    }

    // The R24a gate is for verbs that ACT; reading a disabled control is not refused as disabled.

    [Fact]
    public async Task Get_text_on_a_disabled_control_reads_it()
    {
        var button = await FindAsync(_window.DisabledButton);

        (await _svc.GetTextAsync(button.ElementId)).Should().Be(_window.DisabledButton);
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Get_table_on_a_disabled_button_is_the_missing_GridPattern_not_disabled()
    {
        var button = await FindAsync(_window.DisabledButton);

        Func<Task> act = () => _svc.GetTableAsync(button.ElementId);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("GridPattern");
        Unnamed(ex, _window.DisabledButton).Should().NotContainEquivalentOf("disabled");
        _input.VerifyNoOtherCalls();
    }

    // ---- element gone ------------------------------------------------------------------------

    // GREEN round 2: every interact action, not just invoke. invoke/toggle/select reach the
    // MissingPattern liveness probe; click, select-by-name, focus and type do NOT — they end in
    // ClickPoint's "no on-screen bounds" refusal, FindFirstDescendant's "No item named" miss,
    // Focus(), and the keyboard fallback, each of which describes a dead control as a live one
    // (or, for click/type, tries to inject input — the STRICT input mock turns that into a test
    // failure instead of a real click or keystroke).
    public static TheoryData<string> GoneLookups => new()
    {
        "interact_invoke", "interact_toggle", "interact_select", "interact_select_item",
        "interact_click", "interact_focus", "interact_type",
        "get_text", "get_table", "focus",
    };

    private Task Call(string path, string id) => path switch
    {
        "interact_invoke" => _svc.InteractAsync(id, "invoke", null),
        "interact_toggle" => _svc.InteractAsync(id, "toggle", null),
        "interact_select" => _svc.InteractAsync(id, "select", null),
        "interact_select_item" => _svc.InteractAsync(id, "select", "wmcp-no-such-item"),
        "interact_click" => _svc.InteractAsync(id, "click", null),
        "interact_focus" => _svc.InteractAsync(id, "focus", null),
        "interact_type" => _svc.InteractAsync(id, "type", "wmcp"),
        "get_text" => _svc.GetTextAsync(id),
        "get_table" => _svc.GetTableAsync(id),
        "focus" => _svc.FocusAsync(id),
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "no such path"),
    };

    [Theory]
    [MemberData(nameof(GoneLookups))]
    public async Task A_call_on_an_element_destroyed_since_its_id_was_issued_says_it_is_no_longer_available(string path)
    {
        var button = await FindAsync(_window.EnabledButton);
        _window.Close();
        _window.Exists.Should().BeFalse("precondition: the window and its controls are destroyed");

        Func<Task> act = () => Call(path, button.ElementId);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>(
            "an id whose element died is a refusal the caller acts on (take a new snapshot), not a crash")).Which;
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
        ex.Message.Should().Contain(button.ElementId, "the caller is told which id died");
        ex.Message.Should().ContainEquivalentOf("no longer available");
        ex.Message.Should().Contain("snapshot", "and that the remedy is a new snapshot");
        _input.VerifyNoOtherCalls();
    }
}

/// <summary>
/// D-11 R18 through the REAL service: an element wait (<c>element_exists</c>,
/// <c>element_enabled</c>, and the pre-B-6 <c>WaitForAsync(text, …)</c>) runs a find every poll,
/// and every find mints up to 20 non-snapshot ids. Since D-11 they no longer leak, but they push
/// the caller's own <c>find_element</c> / <c>get_state</c> ids out of the LRU (10 ms × 120 s ≫ 5 000).
/// The fixture gives a deterministic multi-match: the window and three children all carry the
/// token, and exactly two children are disabled. Read-only: nothing is clicked, typed or focused.
/// </summary>
[Trait("Category", "Integration")]
// A window of ours appears and closes: DesktopCollection's membership rule (it rewrites the
// pixels a capture-comparing class may be in the middle of).
[Collection(DesktopCollection.Name)]
public sealed class UIAutomationServiceWaitReleaseIntegrationTests : IDisposable
{
    private readonly OwnedControlsWindow _window = new();

    public void Dispose() => _window.Dispose();

    private static UIAutomationService NewService(UiTreeOptions? options = null) =>
        new(new Mock<IInputService>().Object, new Mock<IWindowService>().Object, options);

    private WaitRequest Element(WaitCondition condition, string text, int timeoutMs, int intervalMs = 10) =>
        new(condition, text, timeoutMs, intervalMs, FindKind.Any, FindScope.Window, _window.Title, IncludeOffscreen: true);

    private async Task<int> MatchCountAsync(string text)
    {
        // Measured on a separate service so the precondition cannot disturb the cache under test.
        // Retried: a desktop child enumeration that fails mid-walk (another test's window closing)
        // is skipped by design (D-5) and reads as zero matches for that one attempt.
        using var probe = NewService();
        int best = 0;
        for (int attempt = 0; attempt < 5 && best == 0; attempt++)
        {
            if (attempt > 0) await Task.Delay(200);
            best = (await probe.FindElementAsync(text, FindKind.Any, FindScope.Window, _window.Title, includeOffscreen: true)).Matches.Length;
        }
        return best;
    }

    [Fact]
    public async Task A_satisfied_element_wait_keeps_only_the_id_it_returns()
    {
        (await MatchCountAsync(_window.Token)).Should().BeGreaterThanOrEqualTo(3, "precondition: one poll finds several matches");
        using var svc = NewService();
        var before = svc.CachedElementCount;

        var result = await svc.WaitForAsync(Element(WaitCondition.ElementExists, _window.Token, timeoutMs: 2000));

        result.Satisfied.Should().BeTrue();
        svc.CachedElementCount.Should().Be(before + 1, "the matches the result does not carry are released");
        var resolved = await svc.GetElementAsync(result.Element!.ElementId);
        resolved.ElementId.Should().Be(result.Element.ElementId, "the id a satisfied wait returns resolves");
        resolved.Name.Should().Contain(_window.Token);
    }

    [Fact]
    public async Task A_long_element_enabled_wait_on_disabled_matches_does_not_expire_the_callers_get_state_id()
    {
        // Capacity = max(4, 2 x 1) = 4: room for the held id plus ONE poll's two matches (the LRU
        // evicts at Add time, before any release can run), but not for two polls'. Every poll
        // finds the two disabled controls and is NOT satisfied. Without the release the second
        // poll evicts the caller's get_state id; with it, each poll's ids go back before the next
        // poll is gathered (R21), so the cache never holds more than the held id + one poll.
        (await MatchCountAsync(_window.Token + " disabled")).Should().Be(2, "precondition: exactly the two disabled controls match");
        using var svc = NewService(new UiTreeOptions(1, MaxCachedElements: 4));
        var held = (await svc.GetStateAsync()).Root.ElementId;
        var before = svc.CachedElementCount;

        var result = await svc.WaitForAsync(Element(WaitCondition.ElementEnabled, _window.Token + " disabled", timeoutMs: 1500));

        result.Satisfied.Should().BeFalse("precondition: both matches are disabled");
        result.Attempts.Should().BeGreaterThanOrEqualTo(2, "precondition: two polls mint past the cap without a release");
        // R21 (round 3) replaces round 2's "Be(before)": the final poll's ids are what the
        // timed-out reply describes, so they stay — at most that one poll's two.
        svc.CachedElementCount.Should().BeLessThanOrEqualTo(before + 2,
            "a timed-out wait may leave at most its last poll's ids behind");
        (await svc.GetElementAsync(held)).ElementId.Should().Be(held, "the caller's id survives a wait that never satisfied");
    }

    [Fact]
    public async Task A_timed_out_element_enabled_wait_names_an_id_in_its_detail_that_resolves()
    {
        // R21 headline, live: "found '<token> disabled button' (el_N) but it is disabled" — round 2
        // released el_N with the rest of the final poll, so the id the reply named was dead.
        (await MatchCountAsync(_window.Token + " disabled")).Should().Be(2, "precondition: exactly the two disabled controls match");
        using var svc = NewService();

        var result = await svc.WaitForAsync(Element(WaitCondition.ElementEnabled, _window.Token + " disabled", timeoutMs: 600));

        result.Satisfied.Should().BeFalse("precondition: both matches are disabled");
        var id = System.Text.RegularExpressions.Regex.Match(result.Detail, @"\bel_\d+\b").Value;
        id.Should().NotBeEmpty($"precondition: the element_enabled detail names the disabled match: {result.Detail}");
        var resolved = await svc.GetElementAsync(id);
        resolved.ElementId.Should().Be(id, "every id a reply names must resolve");
        resolved.Name.Should().Contain(_window.Token).And.Contain("disabled");
        resolved.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_long_element_wait_for_text_that_is_never_found_neither_grows_the_cache_nor_expires_the_callers_id()
    {
        // The caller's row as written. Zero matches mint zero ids, so this holds with or without
        // the release; the row above is the one that bites.
        using var svc = NewService(new UiTreeOptions(1, MaxCachedElements: 10));
        var held = (await svc.GetStateAsync()).Root.ElementId;
        var before = svc.CachedElementCount;

        var result = await svc.WaitForAsync(Element(WaitCondition.ElementExists, "wmcp-absent-" + Guid.NewGuid().ToString("N"), timeoutMs: 500));

        result.Satisfied.Should().BeFalse();
        svc.CachedElementCount.Should().Be(before);
        (await svc.GetElementAsync(held)).ElementId.Should().Be(held);
    }

    [Fact]
    public async Task The_pre_B6_wait_keeps_only_the_id_it_returns()
    {
        (await MatchCountAsync(_window.Token)).Should().BeGreaterThanOrEqualTo(3, "precondition: one poll finds several matches");
        using var svc = NewService();
        var before = svc.CachedElementCount;

        var hit = await svc.WaitForAsync(_window.Token, timeoutMs: 2000, intervalMs: 10,
            FindKind.Any, FindScope.Window, _window.Title, includeOffscreen: true);

        hit.Should().NotBeNull();
        svc.CachedElementCount.Should().Be(before + 1, "matches[1..] are never returned, so they are released");
        (await svc.GetElementAsync(hit!.ElementId)).ElementId.Should().Be(hit.ElementId);
    }
}
