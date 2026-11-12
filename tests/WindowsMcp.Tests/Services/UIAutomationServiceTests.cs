using System.Text;
using FlaUI.Core.Definitions;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "UIAutomation")]
// Serialised with every other class that opens a Notepad window, moves the pointer or reads the
// desktop (see DesktopCollection): two NotepadFixture instances launching at the same time each
// see the other's new window in their inventory diff and pick the wrong one.
[Collection(DesktopCollection.Name)]
public class UIAutomationServiceTests : IClassFixture<NotepadFixture>
{
    private readonly NotepadFixture _np;

    // xUnit builds this class once per test, so this hands the desktop back to Notepad before
    // EVERY test. It matters more since D-5: get_state and the default find scope both root at the
    // FOREGROUND window, so a test that runs while another app holds the foreground silently
    // inspects that app instead of Notepad — under a full parallel suite run these tests were
    // resolving VS Code's document and asserting against it.
    public UIAutomationServiceTests(NotepadFixture np)
    {
        _np = np;
        _np.BringToForeground();
    }

    // A-2 (R2): the service now takes the window inventory too. Real WindowService here — these
    // tests already need the live desktop, and a mock would hide a wiring break in the snapshot's
    // header and root list.
    private static UIAutomationService NewService() => new(new InputService(), new WindowService());

    [Fact]
    public async Task GetStateAsync_returns_tree_with_notepad_root()
    {
        using var svc = NewService();
        var state = await svc.GetStateAsync();
        state.Root.Name.Should().NotBeNullOrEmpty();
        state.Children.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FindElementAsync_finds_notepad_text_area()
    {
        using var svc = NewService();
        var matches = await svc.FindElementAsync("", FindKind.Text);
        matches.Matches.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Concurrency_50_parallel_calls_no_deadlock()
    {
        using var svc = NewService();
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => svc.GetStateAsync()).ToArray();
        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(r => r.Root.Should().NotBeNull());
    }

    // ---- D-2: interact_element ---------------------------------------------------------------

    [Fact]
    public async Task InteractAsync_type_enters_text_into_the_document()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);
        var stamp = $"d2-{Guid.NewGuid():N}"[..12];

        var result = await svc.InteractAsync(id, "type", stamp);

        // Modern (XAML) Notepad has no writable ValuePattern on its editor, so this usually proves
        // the keyboard fallback; classic Notepad takes the ValuePattern path. Both must land the text.
        result.Method.Should().BeOneOf("ValuePattern", "Keyboard");
        await Task.Delay(300);   // let injected keystrokes reach the control
        (await svc.GetTextAsync(id)).Should().Contain(stamp);
    }

    [Fact]
    public async Task InteractAsync_focus_gives_the_document_keyboard_focus()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);

        var result = await svc.InteractAsync(id, "focus", null);

        result.Method.Should().Be("Focus");
        var focused = _np.Automation.FocusedElement();
        (focused.ControlType is ControlType.Document or ControlType.Edit)
            .Should().BeTrue($"the focused element was {focused.ControlType} '{focused.Name}'");
    }

    [Fact]
    public async Task InteractAsync_click_falls_back_to_a_physical_click_on_a_document()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);

        // A Document supports none of Invoke / SelectionItem / Toggle, so this must be the fallback.
        var result = await svc.InteractAsync(id, "click", null);

        result.Method.Should().Be("PhysicalClick");
        result.Detail.Should().MatchRegex(@"^\(-?\d+,-?\d+\)$");
    }

    [Fact]
    public async Task InteractAsync_toggle_names_the_unsupported_pattern_and_the_control()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);

        Func<Task> act = () => svc.InteractAsync(id, "toggle", null);

        // D-10 (R1): the live sibling of UiRefusalsTests - the service really throws the refusal
        // the factory builds, as a caller-facing type, naming the pattern and the control type.
        var ex = (await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("TogglePattern not supported on *")).Which;
        ex.Message.Should().MatchRegex("(Document|Edit) '", "the control type and the quoted name follow the pattern");
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
    }

    // ---- D-4: assert_element ----------------------------------------------------------------

    [Fact]
    public async Task AssertElementAsync_exists_enabled_visible_pass_on_the_document()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);

        foreach (var state in new[] { "exists", "enabled", "visible" })
        {
            var result = await svc.AssertElementAsync(id, state);
            result.Pass.Should().BeTrue($"{state}: observed {result.Observed}");
            result.State.Should().Be(state);
        }
    }

    [Fact]
    public async Task AssertElementAsync_focused_passes_on_the_focused_document_and_names_the_focus_owner_otherwise()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);
        await svc.FocusAsync(id);
        await Task.Delay(200);

        var onDocument = await svc.AssertElementAsync(id, "focused");
        onDocument.Pass.Should().BeTrue(onDocument.Observed);

        // A title-bar / toolbar button never holds keyboard focus while the document does. (The
        // top-level Window of a XAML app does report HasKeyboardFocus, so it is no use here.)
        var button = FindInTree(await svc.GetStateAsync(), "Button")
            ?? throw new Xunit.Sdk.XunitException("No Button within three levels of the Notepad window");
        var onButton = await svc.AssertElementAsync(button.ElementId, "focused");
        onButton.Pass.Should().BeFalse($"observed {onButton.Observed}");
        onButton.Observed.Should().StartWith("focus is on");
    }

    private static ElementInfo? FindInTree(ElementTree t, string controlType)
    {
        if (t.Root.ControlType == controlType) return t.Root;
        foreach (var c in t.Children)
            if (FindInTree(c, controlType) is { } hit) return hit;
        return null;
    }

    [Fact]
    public async Task AssertElementAsync_value_compares_exactly_and_quotes_the_actual_value()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);
        var stamp = $"d4-{Guid.NewGuid():N}"[..12];
        await svc.InteractAsync(id, "type", stamp);
        await Task.Delay(300);
        var text = await svc.GetTextAsync(id);   // other tests type into the same document

        var match = await svc.AssertElementAsync(id, "value", text);
        match.Pass.Should().BeTrue(match.Observed);

        var mismatch = await svc.AssertElementAsync(id, "value", "not-" + stamp);
        mismatch.Pass.Should().BeFalse();
        mismatch.Observed.Should().StartWith("value is '").And.Contain(stamp);
    }

    [Fact]
    public async Task AssertElementAsync_checked_names_the_missing_pattern_on_a_document()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);

        var result = await svc.AssertElementAsync(id, "checked");

        result.Pass.Should().BeFalse();
        result.Observed.Should().StartWith("no TogglePattern on");
    }

    [Fact]
    public async Task AssertElementAsync_reports_a_stale_element_as_no_longer_available()
    {
        using var svc = NewService();

        // A throwaway classic Win32 window (Character Map is single-process, multi-instance, and
        // present on every Windows edition) so killing it cannot disturb the shared Notepad.
        using var proc = System.Diagnostics.Process.Start("charmap.exe");
        string id;
        try
        {
            for (int i = 0; i < 40 && proc.MainWindowHandle == IntPtr.Zero; i++) { await Task.Delay(150); proc.Refresh(); }
            proc.MainWindowHandle.Should().NotBe(IntPtr.Zero, "charmap.exe must open a window");
            _np.Automation.FromHandle(proc.MainWindowHandle).SetForeground();

            // get_state roots at the foreground window; wait until that is Character Map.
            ElementTree state = await svc.GetStateAsync();
            for (int i = 0; i < 20 && !state.Root.Name.Contains("Character Map", StringComparison.OrdinalIgnoreCase); i++)
            {
                await Task.Delay(150);
                state = await svc.GetStateAsync();
            }
            state.Root.Name.Should().Contain("Character Map", "the throwaway window must be in the foreground for this test");
            id = state.Root.ElementId;
            var alive = await svc.AssertElementAsync(id, "exists");
            alive.Pass.Should().BeTrue(alive.Observed);

            proc.Kill();
            proc.WaitForExit();
            await Task.Delay(500);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { /* already gone */ }
            _np.BringToForeground();
        }

        // A dead Win32 window's element does not throw: UIA answers with defaults (ControlType
        // Pane, ProcessId 0), so the service's liveness probe must not rely on an exception.
        var gone = await svc.AssertElementAsync(id, "exists");
        gone.Pass.Should().BeFalse();
        gone.Observed.Should().Be("element no longer available");
        (await svc.AssertElementAsync(id, "enabled")).Observed.Should().Be("element no longer available");
    }

    /// <summary>
    /// The service-issued id of Notepad's editor. get_state is rooted at the foreground window
    /// three levels deep, which reaches classic Notepad's Document; modern (XAML) Notepad nests its
    /// RichEditBox deeper, so fall back to a name search, then to any Document/Edit.
    /// </summary>

    // ---- D-5 / D-6 / D-7: the find path, live ------------------------------------------------

    // The regression this whole item was filed for: on a busy desktop one element that dies
    // between the walk and the property read used to fail the entire call.
    [Theory]
    [InlineData(FindScope.Foreground)]
    [InlineData(FindScope.Desktop)]
    public async Task FindElementAsync_any_survives_a_busy_desktop(FindScope scope)
    {
        using var svc = NewService();
        for (int i = 0; i < 10; i++)
        {
            var result = await svc.FindElementAsync("", FindKind.Any, scope);
            result.Matches.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task FindElementAsync_window_scope_targets_that_window_by_substring()
    {
        using var svc = NewService();

        var scoped = await svc.FindElementAsync("", FindKind.Text, FindScope.Window, "Notepad");

        scoped.Matches.Should().NotBeEmpty("'Notepad' must match 'Untitled - Notepad' by substring");
    }

    [Fact]
    public async Task FindElementAsync_unmatched_window_names_the_open_windows()
    {
        using var svc = NewService();
        Func<Task> act = () => svc.FindElementAsync("", FindKind.Any, FindScope.Window, "zzqxv-no-such-window");

        (await act.Should().ThrowAsync<KeyNotFoundException>()).WithMessage("*Open windows:*");
    }

    // D-6: kind=interactive used to be Button|CheckBox|Hyperlink|MenuItem, so the editor — an Edit
    // on classic Notepad, a Document on the modern one — was invisible to it. Foreground scope, so
    // the assertion is about Notepad rather than whichever window the desktop walk reached first.
    [Fact]
    public async Task FindElementAsync_interactive_finds_the_editor()
    {
        using var svc = NewService();

        var result = await svc.FindElementAsync("", FindKind.Interactive);

        result.Matches.Should().Contain(m => m.ControlType == "Edit" || m.ControlType == "Document");
    }

    // Regression: the kind filter is pushed into the UIA condition, which only covers DESCENDANTS.
    // The walk's own roots have to be filtered client-side, or every window Pane counts as a match
    // for every kind and fills the 20-result cap before any real content is reached.
    [Fact]
    public async Task FindElementAsync_interactive_never_returns_a_window_root_pane()
    {
        using var svc = NewService();

        var result = await svc.FindElementAsync("", FindKind.Interactive, FindScope.Desktop);

        result.Matches.Should().NotContain(m => m.ControlType == "Pane" || m.ControlType == "Window");
    }

    // D-7: off-screen elements used to crowd out on-screen ones inside the 20-result cap.
    [Fact]
    public async Task FindElementAsync_drops_offscreen_results_by_default()
    {
        using var svc = NewService();

        var visible = await svc.FindElementAsync("", FindKind.Text, FindScope.Desktop);
        var all = await svc.FindElementAsync("", FindKind.Text, FindScope.Desktop, null, includeOffscreen: true);

        visible.Matches.Should().OnlyContain(
            m => !m.IsOffscreen || m.ControlType == "Edit",
            "only the documented Edit exception may be off-screen");
        all.Matches.Length.Should().BeGreaterThanOrEqualTo(visible.Matches.Length);
    }

    [Fact]
    public async Task WaitForAsync_returns_text_that_appears_after_the_first_poll()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);
        var stamp = $"d5-{Guid.NewGuid():N}"[..10];

        var waiting = svc.WaitForAsync(stamp, timeoutMs: 15000, intervalMs: 250, FindKind.Text);
        await Task.Delay(500);
        await svc.InteractAsync(id, "type", stamp);

        (await waiting).Should().NotBeNull("the text appeared while the wait was polling");
    }

    // ---- A-13: unicode hygiene, end to end ---------------------------------------------------

    // A codicon-style private use glyph and an emoji, written as code points so this file stays
    // ASCII and nothing can normalise them on the way to the compiler.
    private static readonly string PuaGlyph = ((char)0xE0B0).ToString();   // U+E0B0, powerline/VS Code
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600); // grinning face, a valid pair

    // These two are the ONLY proof that the UIA read sites are wired to UiText.Sanitize: an
    // AutomationElement cannot be faked or mocked (sealed, COM-backed), so nothing headless can
    // observe TryGetName / TryGetValue / GetTextAsync. They type the glyph and the emoji into the
    // editor with InputService.TypeAsync (SendInput KEYEVENTF_UNICODE - the only way a PUA glyph
    // and the two halves of a surrogate pair get into a control) and read them back through the
    // paths the model uses.
    //
    // Modern (XAML) Notepad's editor is a Document whose text may surface as ElementInfo.Value
    // (classic Notepad's Edit carries a ValuePattern) or only through the TextPattern that
    // GetTextAsync falls back on, and the same string can also land on an element's Name. The
    // first test is therefore tolerant of WHICH carrier holds it: it searches every element from
    // both get_state and find_element, and every Name and Value on them.

    [Fact]
    public async Task Element_name_and_value_carry_no_private_use_glyph_and_keep_the_emoji()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);
        var marker = $"a13-{Guid.NewGuid():N}"[..10];

        await svc.InteractAsync(id, "focus", null);
        await new InputService().TypeAsync($"{PuaGlyph}left-{marker} {Emoji} right");
        await WaitForTypedTextAsync(svc, id, marker);

        var carriers = await TextCarriersContainingAsync(svc, marker);

        carriers.Should().NotBeEmpty("the typed text must surface on some element's Name or Value");
        carriers.Should().OnlyContain(t => PrivateUseCodePoints(t).Length == 0,
            "A-13 strips U+E000-U+F8FF in TryGetName and TryGetValue before the model sees the name");
        carriers.Should().Contain(t => t.Contains(Emoji),
            "sanitising must not break a valid surrogate pair - the emoji has to survive intact");
    }

    [Fact]
    public async Task GetTextAsync_strips_private_use_glyphs_and_keeps_the_emoji()
    {
        using var svc = NewService();
        var id = await FindNotepadDocumentIdAsync(svc);
        var marker = $"a13-{Guid.NewGuid():N}"[..10];

        await svc.InteractAsync(id, "focus", null);
        await new InputService().TypeAsync($"{PuaGlyph}left-{marker} {Emoji} right");
        await WaitForTypedTextAsync(svc, id, marker);

        var text = await svc.GetTextAsync(id);

        text.Should().Contain(marker, "get_text must read back what was typed into the editor");
        PrivateUseCodePoints(text).Should().BeEmpty(
            "A-13 sanitises the text GetTextAsync returns, so no icon-font glyph reaches the model");
        text.Should().Contain(Emoji, "the emoji must survive intact");
    }

    /// <summary>
    /// SendInput returns before the control has processed the keystrokes, and on a loaded box a
    /// fixed delay raced the read (a run saw the text cut off right after the marker). Poll until
    /// the LAST word of the typed string is visible, so the whole string, emoji included, is there.
    /// </summary>
    private static async Task WaitForTypedTextAsync(UIAutomationService svc, string id, string marker)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            string text;
            try { text = await svc.GetTextAsync(id); } catch { continue; }
            if (text.Contains(marker) && text.Contains("right")) return;
        }
        // Fall through: the assertions below say precisely what is missing.
    }

    /// <summary>
    /// Every string the model could read the typed text from: the Name and Value of every element
    /// in the get_state tree plus every find_element match in the Notepad window.
    /// </summary>
    private static async Task<string[]> TextCarriersContainingAsync(UIAutomationService svc, string marker)
    {
        var infos = new List<ElementInfo>();
        Collect(await svc.GetStateAsync(), infos);
        infos.AddRange((await svc.FindElementAsync("", FindKind.Text, FindScope.Window, "Notepad")).Matches);

        return infos
            .SelectMany(i => new[] { i.Name, i.Value ?? "" })
            .Where(t => t.Contains(marker))
            .ToArray();

        static void Collect(ElementTree node, List<ElementInfo> into)
        {
            into.Add(node.Root);
            foreach (var child in node.Children) Collect(child, into);
        }
    }

    private static int[] PrivateUseCodePoints(string s) =>
        s.EnumerateRunes()
            .Where(r => (r.Value >= 0xE000 && r.Value <= 0xF8FF)          // BMP private use area
                     || (r.Value >= 0xF0000 && r.Value <= 0xFFFFD)        // plane 15
                     || (r.Value >= 0x100000 && r.Value <= 0x10FFFD))     // plane 16
            .Select(r => r.Value)
            .ToArray();

    private static async Task<string> FindNotepadDocumentIdAsync(UIAutomationService svc)
    {
        // Resolve the editor inside the NOTEPAD window explicitly (D-5 scope=window) rather than
        // trusting the foreground: if focus slipped, a foreground-scoped search returns another
        // app's Document and the test then asserts against the wrong window.
        var inWindow = await svc.FindElementAsync("", FindKind.Text, FindScope.Window, "Notepad");
        var doc = inWindow.Matches.FirstOrDefault(m => m.ControlType is "Document" or "Edit")
            ?? throw new Xunit.Sdk.XunitException("No Document/Edit element in the Notepad window");
        return doc.ElementId;
    }
}

// Separate class so it doesn't need the NotepadFixture — Dispose tears down before any
// UIA call, so this test does not need a live desktop session and is Unit-trait safe.
[Trait("Category", "Unit")]
public class UIAutomationServiceUnitTests
{
    [Fact]
    public async Task GetStateAsync_throws_after_dispose()
    {
        var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);
        svc.Dispose();
        Func<Task> act = () => svc.GetStateAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    // D-4: the argument rules and the unknown-id rule are decided before any UIA call is made,
    // so these run headless.
    [Theory]
    [InlineData("value", null, "*requires expected*")]
    [InlineData("enabled", "x", "*only used with state=value*")]
    [InlineData("hovering", null, "*Unknown assertion state 'hovering'*")]
    public async Task AssertElementAsync_rejects_bad_arguments(string state, string? expected, string message)
    {
        using var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);
        Func<Task> act = () => svc.AssertElementAsync("el_0", state, expected);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
    }

    [Fact]
    public async Task AssertElementAsync_exists_fails_for_an_unknown_id()
    {
        using var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);

        var result = await svc.AssertElementAsync("el_404", "exists");

        result.Pass.Should().BeFalse();
        result.Observed.Should().Be("unknown element id");
    }

    [Fact]
    public async Task GetElementAsync_on_an_unknown_id_throws_the_KeyNotFoundException_the_input_verbs_let_surface()
    {
        // B-4: every input verb resolves element_id through THIS method and deliberately does not
        // catch what it throws, and every tool test mocks it. This is the non-mocked sibling: the
        // real service, an empty cache, and the exception type the mocks promise - naming the id,
        // because "el_404 is gone, take a new snapshot" is the whole value of the refusal.
        using var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);

        Func<Task> act = () => svc.GetElementAsync("el_404");

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain("el_404");
    }

    // ---- D-11 (R7): every lookup path says "unknown" for an id never issued ----------------------

    public static TheoryData<string> LookupPaths => new()
    {
        "get_element", "get_text", "focus", "interact", "get_table", "assert_enabled",
    };

    private static Task Lookup(UIAutomationService svc, string path, string id) => path switch
    {
        "get_element" => svc.GetElementAsync(id),
        "get_text" => svc.GetTextAsync(id),
        "focus" => svc.FocusAsync(id),
        "interact" => svc.InteractAsync(id, "click", null),
        "get_table" => svc.GetTableAsync(id),
        "assert_enabled" => svc.AssertElementAsync(id, "enabled"),
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "no such lookup path"),
    };

    [Theory]
    [MemberData(nameof(LookupPaths))]
    public async Task Every_lookup_on_a_never_issued_id_says_unknown_rather_than_not_in_cache(string path)
    {
        // D-11: the old "Element 'el_N' not in cache" gave the caller nothing to act on, and it read
        // the same whether the id had expired or had never existed. An empty service has issued
        // nothing, so every id is "unknown" - on every path that resolves an id, not just one.
        using var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);

        Func<Task> act = () => Lookup(svc, path, "el_999999");

        var message = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message;
        message.Should().Contain("el_999999");
        message.Should().ContainEquivalentOf("unknown");
        message.Should().NotContainEquivalentOf("not in cache");
        message.Should().NotContainEquivalentOf("expired", "nothing was ever issued, so nothing can have expired");
    }

    [Fact]
    public async Task AssertElementAsync_other_states_throw_for_an_unknown_id()
    {
        using var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);
        Func<Task> act = () => svc.AssertElementAsync("el_404", "enabled");
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // D-10 R20 (round 3): fed what FlaUI's Com.Call actually throws, not raw COMExceptions. The
    // round-2 row "COMException(0x800706BA) is gone" is deleted: FlaUI never delivers it — an RPC
    // failure (the target process exited) arrives as a Win32Exception with the HRESULT lost, the same
    // shape as E_FAIL, so IsElementGone cannot tell the two apart and must not guess. The liveness
    // probe decides that case (UiRefusals.ProbeSaysGone, UiRefusalsTests).
    [Theory]
    [InlineData(WindowsMcp.Tests.Fixtures.FlaUiShaped.UIA_E_ELEMENTNOTAVAILABLE, true)]
    [InlineData(WindowsMcp.Tests.Fixtures.FlaUiShaped.RPC_S_SERVER_UNAVAILABLE, false)]   // ambiguous: the probe decides
    [InlineData(WindowsMcp.Tests.Fixtures.FlaUiShaped.RPC_E_DISCONNECTED, false)]
    [InlineData(WindowsMcp.Tests.Fixtures.FlaUiShaped.E_FAIL, false)]
    [InlineData(WindowsMcp.Tests.Fixtures.FlaUiShaped.UIA_E_TIMEOUT, false)]               // hung, not gone
    [InlineData(WindowsMcp.Tests.Fixtures.FlaUiShaped.UIA_E_ELEMENTNOTENABLED, false)]
    public void IsElementGone_judges_what_FlaUI_throws(uint hresult, bool gone)
    {
        var ex = WindowsMcp.Tests.Fixtures.FlaUiShaped.From(hresult);
        UIAutomationService.IsElementGone(ex).Should().Be(gone);
        UIAutomationService.IsElementGone(new InvalidOperationException()).Should().BeFalse();
    }

    // ---- D-5: the wait_for retry loop, exercised with a fake poll (no UIA, no desktop) ---------

    private static readonly ElementInfo Hit =
        new("el_1", "Ready", "Text", true, false, new Bounds(0, 0, 10, 10), null, null, null);

    // THE D-5 headline: before this, the first transient UIA failure ended the wait — the one
    // thing a wait exists to absorb.
    [Fact]
    public async Task PollAsync_keeps_polling_after_a_poll_throws()
    {
        var attempts = 0;
        var result = await UIAutomationService.PollAsync(_ =>
        {
            attempts++;
            if (attempts < 3) throw new System.Runtime.InteropServices.COMException("stale", unchecked((int)0x80040201));
            return Task.FromResult<ElementInfo?>(Hit);
        }, timeoutMs: 5000, intervalMs: 1, CancellationToken.None);

        result.Should().BeSameAs(Hit);
        attempts.Should().Be(3);
    }

    // "Never managed to look" must not be reported as "looked and did not find it".
    [Fact]
    public async Task PollAsync_throws_when_every_poll_failed()
    {
        Func<Task> act = () => UIAutomationService.PollAsync(
            _ => throw new InvalidOperationException("provider exploded"),
            timeoutMs: 60, intervalMs: 1, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<TimeoutException>();
        thrown.WithMessage("*provider exploded*");
        thrown.And.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task PollAsync_returns_null_when_clean_polls_find_nothing()
    {
        var result = await UIAutomationService.PollAsync(
            _ => Task.FromResult<ElementInfo?>(null), timeoutMs: 60, intervalMs: 1, CancellationToken.None);

        result.Should().BeNull();
    }

    // timeout_ms:0 means "check now", not "do nothing" — the old loop never polled at all.
    [Fact]
    public async Task PollAsync_polls_at_least_once_with_a_zero_timeout()
    {
        var attempts = 0;
        var result = await UIAutomationService.PollAsync(
            _ => { attempts++; return Task.FromResult<ElementInfo?>(null); },
            timeoutMs: 0, intervalMs: 500, CancellationToken.None);

        attempts.Should().Be(1);
        result.Should().BeNull();
    }

    [Fact]
    public async Task PollAsync_does_not_sleep_when_the_first_poll_hits()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await UIAutomationService.PollAsync(
            _ => Task.FromResult<ElementInfo?>(Hit), timeoutMs: 10000, intervalMs: 5000, CancellationToken.None);
        sw.Stop();

        result.Should().BeSameAs(Hit);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PollAsync_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Func<Task> act = () => UIAutomationService.PollAsync(
            _ => Task.FromResult<ElementInfo?>(null), timeoutMs: 5000, intervalMs: 1, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- D-5: argument rules, decided before any UIA call, so they run headless ---------------

    [Fact]
    public async Task FindElementAsync_rejects_window_scope_without_a_title()
    {
        using var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);
        Func<Task> act = () => svc.FindElementAsync("x", FindKind.Any, FindScope.Window);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*requires windowTitle*");
    }

    [Fact]
    public async Task FindElementAsync_rejects_a_window_title_with_another_scope()
    {
        using var svc = new UIAutomationService(new Mock<IInputService>().Object, new Mock<IWindowService>().Object);
        Func<Task> act = () => svc.FindElementAsync("x", FindKind.Any, FindScope.Desktop, "Notepad");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*only used with scope=window*");
    }

    // ---- D-6: the interactive set is pinned, so a later edit is a visible diff ----------------

    [Fact]
    public void InteractiveControlTypes_matches_the_upstream_set_plus_Document()
    {
        UIAutomationService.InteractiveControlTypes.Should().BeEquivalentTo(new[]
        {
            ControlType.Button, ControlType.ListItem, ControlType.MenuItem, ControlType.Edit,
            ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox, ControlType.Hyperlink,
            ControlType.SplitButton, ControlType.TabItem, ControlType.TreeItem, ControlType.DataItem,
            ControlType.HeaderItem, ControlType.Spinner, ControlType.Slider, ControlType.ScrollBar,
            ControlType.Document,
        });

        // The four types kind=interactive used to be limited to — the regression D-6 fixes.
        UIAutomationService.InteractiveControlTypes.Should()
            .Contain(ControlType.Edit).And.Contain(ControlType.ComboBox).And.Contain(ControlType.ListItem)
            .And.Contain(ControlType.TabItem).And.Contain(ControlType.RadioButton)
            .And.Contain(ControlType.Slider).And.Contain(ControlType.TreeItem);
    }
}

/// <summary>
/// D-11 through the REAL service: <c>ElementCacheTests</c> proves the cache, these prove the
/// service is wired to it — the capacity it is built with, which calls mint, which calls only look
/// up. An <c>AutomationElement</c> cannot be faked, so the ids come from read-only walks of the
/// live desktop (whatever is in front; nothing is clicked, typed or focused) — Integration, like
/// <c>UIAutomationSnapshotIntegrationTests</c>.
/// <para>
/// D-10 R24c: in <see cref="DesktopCollection"/>. Most rows root at <c>get_state</c>'s foreground
/// window and assert on what it contains ("the root is a Window", "the tree has more than one
/// node"); a class running beside this one that moves the foreground (round 2's disabled-control
/// refusals did, see <c>UIAutomationServiceRefusalIntegrationTests</c>) made those rows fail
/// intermittently in parallel runs.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(DesktopCollection.Name)]
public class UIAutomationServiceElementCacheIntegrationTests
{
    private static UIAutomationService NewService(UiTreeOptions options) =>
        new(new InputService(), new WindowService(), options);

    private static List<string> Ids(ElementTree tree)
    {
        var ids = new List<string>();
        Collect(tree, ids);
        return ids;

        static void Collect(ElementTree node, List<string> into)
        {
            if (node.Root.ElementId.StartsWith("el_", StringComparison.Ordinal)) into.Add(node.Root.ElementId);
            foreach (var child in node.Children) Collect(child, into);
        }
    }

    /// <summary>A snapshot of the desktop with its tree, so every walked node's id is in hand.</summary>
    private static async Task<List<string>> SnapshotIdsAsync(UIAutomationService svc, int maxElements)
    {
        var snap = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop, IncludeTree: true, MaxElements: maxElements));
        snap.Tree.Should().NotBeNull("include_tree was asked for");
        var ids = Ids(snap.Tree!);
        ids.Should().NotBeEmpty("a desktop with any window yields at least one snapshot id");
        return ids;
    }

    // ---- R10: get_element answers with the caller's id and mints nothing ----------------------

    [Fact]
    public async Task GetElementAsync_on_a_snapshot_id_answers_with_that_id()
    {
        using var svc = NewService(new UiTreeOptions(20));
        var id = (await SnapshotIdsAsync(svc, 20))[0];

        var info = await svc.GetElementAsync(id);

        info.ElementId.Should().Be(id, "the old code re-minted a second id for an element the caller already holds");
    }

    [Fact]
    public async Task GetElementAsync_on_a_get_state_id_answers_with_that_id()
    {
        using var svc = NewService(new UiTreeOptions(5));
        var id = (await svc.GetStateAsync()).Root.ElementId;

        var info = await svc.GetElementAsync(id);

        info.ElementId.Should().Be(id);
    }

    [Fact]
    public async Task GetElementAsync_does_not_grow_the_cache()
    {
        // The checklist's "get_element on a snapshot id does not grow the cache". Every
        // element-targeted click/type/scroll/drag resolves through GetElementAsync, so before D-11
        // each one added an entry that lived until the process exited.
        using var svc = NewService(new UiTreeOptions(5));
        var snapshotId = (await SnapshotIdsAsync(svc, 20))[0];
        var stateId = (await svc.GetStateAsync()).Root.ElementId;
        var before = svc.CachedElementCount;

        for (int i = 0; i < 10; i++)
        {
            await svc.GetElementAsync(snapshotId);
            await svc.GetElementAsync(stateId);
        }

        svc.CachedElementCount.Should().Be(before, "a lookup is not a mint");
    }

    // ---- R4 / R9 at the service: the cap the service is built with holds ---------------------

    [Fact]
    public async Task Repeated_get_state_leaves_the_cache_at_its_cap_and_the_oldest_id_expires_with_the_remedy()
    {
        // Capacity = Math.Max(MaxCachedElements 10, 2 x MaxElements 3) = 10.
        using var svc = NewService(new UiTreeOptions(3, MaxCachedElements: 10));
        var first = (await svc.GetStateAsync()).Root.ElementId;
        int minted = 1;
        for (int i = 0; i < 30; i++) minted += Ids(await svc.GetStateAsync()).Count;
        minted.Should().BeGreaterThan(10, "precondition: the loop has to mint past the cap for this to prove anything");

        svc.CachedElementCount.Should().BeLessThanOrEqualTo(10);

        Func<Task> act = () => svc.GetElementAsync(first);
        var message = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message;
        message.Should().Contain(first).And.ContainEquivalentOf("expired").And.Contain("snapshot");

        // assert_element(exists) answers a dead id with FAIL, never an exception (D-4).
        var exists = await svc.AssertElementAsync(first, "exists");
        exists.Pass.Should().BeFalse();
    }

    [Fact]
    public async Task GetElementAsync_keeps_a_used_id_alive_through_get_state_churn()
    {
        // LRU, not FIFO, at the service: an element the agent keeps targeting is "used" by every
        // element-targeted input verb (they all resolve through GetElementAsync).
        using var svc = NewService(new UiTreeOptions(3, MaxCachedElements: 10));
        var kept = (await svc.GetStateAsync()).Root.ElementId;

        for (int i = 0; i < 30; i++)
        {
            await svc.GetElementAsync(kept);
            await svc.GetStateAsync();
        }

        (await svc.GetElementAsync(kept)).ElementId.Should().Be(kept);
    }

    [Fact]
    public async Task Snapshot_ids_survive_get_state_churn_and_do_not_count_against_the_cap()
    {
        using var svc = NewService(new UiTreeOptions(3, MaxCachedElements: 10));
        var snapshotIds = await SnapshotIdsAsync(svc, 15);   // a per-call budget may exceed the cache cap

        for (int i = 0; i < 30; i++) await svc.GetStateAsync();

        foreach (var id in snapshotIds)
            (await svc.GetElementAsync(id)).ElementId.Should().Be(id, $"{id} belongs to the live snapshot");
        svc.CachedElementCount.Should().BeLessThanOrEqualTo(10 + snapshotIds.Count);
    }

    [Fact]
    public async Task The_next_snapshot_expires_the_previous_snapshot_ids_with_the_remedy()
    {
        using var svc = NewService(new UiTreeOptions(5));
        var old = (await SnapshotIdsAsync(svc, 5))[0];

        await SnapshotIdsAsync(svc, 5);

        Func<Task> act = () => svc.GetElementAsync(old);
        var message = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message;
        message.Should().Contain(old).And.ContainEquivalentOf("expired").And.Contain("snapshot");
    }

    // ---- R9 at the service: it runs with CapacityFor, not the raw cap -------------------------

    [Fact]
    public async Task One_get_state_never_evicts_its_own_ids_even_under_a_smaller_configured_cap()
    {
        // ElementCacheTests pin CapacityFor; this pins that the service is BUILT with it. A cap of 1
        // under a budget of 8 must run as 16 (2 x budget): built with MaxCachedElements alone, the
        // cache would keep one id of the tree get_state just returned.
        using var svc = NewService(new UiTreeOptions(8, MaxCachedElements: 1));
        var ids = Ids(await svc.GetStateAsync());
        ids.Count.Should().BeGreaterThan(1, "precondition: the foreground tree has more than one node");

        svc.CachedElementCount.Should().Be(ids.Count, "every id the call returned is still held");
        foreach (var id in ids)
            (await svc.GetElementAsync(id)).ElementId.Should().Be(id, $"{id} came from the call that just returned");
    }

    // ---- R10 / R7 on the assert_element path ---------------------------------------------------

    [Fact]
    public async Task AssertElementAsync_and_GetTextAsync_on_a_live_id_answer_from_the_cache_without_minting()
    {
        // The hit branch of AssertOnSta's TryGet (headless tests only ever reach the miss), and the
        // two other read-only lookups that must not grow the cache either.
        using var svc = NewService(new UiTreeOptions(5));
        var id = (await svc.GetStateAsync()).Root.ElementId;
        var before = svc.CachedElementCount;

        var exists = await svc.AssertElementAsync(id, "exists");
        await svc.GetTextAsync(id);

        exists.ElementId.Should().Be(id);
        exists.Observed.Should().NotBe("unknown element id", "the id is live: the miss branch must not have run");
        exists.Observed.Should().NotContainEquivalentOf("expired");
        svc.CachedElementCount.Should().Be(before, "a lookup is not a mint");
    }

    [Fact]
    public async Task AssertElementAsync_other_states_on_an_expired_id_throw_saying_it_expired()
    {
        using var svc = NewService(new UiTreeOptions(5));
        var old = (await SnapshotIdsAsync(svc, 5))[0];
        await SnapshotIdsAsync(svc, 5);

        Func<Task> act = () => svc.AssertElementAsync(old, "enabled");

        var message = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message;
        message.Should().Contain(old).And.ContainEquivalentOf("expired").And.Contain("snapshot");
    }

    /// <summary>
    /// RED (GREEN-pass finding): <c>exists</c> is D-4's no-throw question, so a dead id is a FAIL —
    /// but the cache can now tell an id the server issued and dropped from one it never issued, and
    /// D-11 says an evicted id "answers ... saying it expired and to re-snapshot". "unknown element
    /// id" tells the agent it invented the id; the remedy (take a new snapshot) is lost.
    /// <c>UIAutomationServiceUnitTests.AssertElementAsync_exists_fails_for_an_unknown_id</c> keeps
    /// the never-issued case at "unknown element id".
    /// </summary>
    [Fact]
    public async Task AssertElementAsync_exists_on_an_id_aged_out_of_the_cache_says_expired_not_unknown()
    {
        using var svc = NewService(new UiTreeOptions(3, MaxCachedElements: 10));
        var first = (await svc.GetStateAsync()).Root.ElementId;
        for (int i = 0; i < 30; i++) await svc.GetStateAsync();
        Func<Task> lookup = () => svc.GetElementAsync(first);
        (await lookup.Should().ThrowAsync<KeyNotFoundException>()).Which.Message
            .Should().ContainEquivalentOf("expired", "precondition: the id really aged out");

        var result = await svc.AssertElementAsync(first, "exists");

        result.Pass.Should().BeFalse();
        result.Observed.Should().ContainEquivalentOf("expired", "the server issued this id; it did not make it up");
        result.Observed.Should().Contain("snapshot", "and the remedy is a new snapshot");
        result.Observed.Should().NotContainEquivalentOf("unknown");
    }

    // ---- D-10 (R1) through the real service, without the Notepad fixture ----------------------

    [Fact]
    public async Task InteractAsync_toggle_on_a_window_throws_the_caller_facing_refusal_naming_it()
    {
        // The only other real-element proof of NotSupported() is the UIAutomation-category Notepad
        // test, which headless runs skip. A top-level Window exposes no TogglePattern, so toggle
        // refuses on the STA thread before anything is acted on; the strict input mock proves no
        // click or keystroke is injected either way.
        var input = new Mock<IInputService>(MockBehavior.Strict);
        using var svc = new UIAutomationService(input.Object, new Mock<IWindowService>().Object, new UiTreeOptions(1));
        var root = (await svc.GetStateAsync()).Root;
        root.ControlType.Should().Be("Window", "precondition: get_state roots at the foreground window, which has no TogglePattern");

        Func<Task> act = () => svc.InteractAsync(root.ElementId, "toggle", null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().StartWith("TogglePattern not supported on Window '");
        ToolErrors.IsCallerFacing(ex).Should().BeTrue("the call-tool filter must hand this message to the client");
        input.VerifyNoOtherCalls();
    }

    // ---- D-10 R11 GREEN round 2: what Refusing does NOT translate ------------------------------

    public static TheoryData<string> RefusingPaths => new() { "interact", "get_text", "get_table", "focus" };

    private static Task RefusingCall(UIAutomationService svc, string path, string id) => path switch
    {
        "interact" => svc.InteractAsync(id, "invoke", null),
        "get_text" => svc.GetTextAsync(id),
        "get_table" => svc.GetTableAsync(id),
        "focus" => svc.FocusAsync(id),
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "no such path"),
    };

    /// <summary>
    /// <c>Refusing</c> resolves the id OUTSIDE its try, so an id the cache has dropped is the
    /// cache's own KeyNotFoundException ("expired ... take a new snapshot"), never re-dressed as
    /// UiRefusals.Gone ("no longer available") — the two misses have different remedies, and an
    /// expired id says so in words the caller can act on. The never-issued sibling is
    /// <c>UIAutomationServiceUnitTests.Every_lookup_on_a_never_issued_id_says_unknown_rather_than_not_in_cache</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusingPaths))]
    public async Task Every_element_call_on_an_expired_id_throws_the_caches_KeyNotFound_untranslated(string path)
    {
        var input = new Mock<IInputService>(MockBehavior.Strict);
        // Capacity = max(1, 2 x 1) = 2: the third one-node get_state evicts the first id.
        using var svc = new UIAutomationService(input.Object, new Mock<IWindowService>().Object, new UiTreeOptions(1, MaxCachedElements: 1));
        var first = (await svc.GetStateAsync()).Root.ElementId;
        await svc.GetStateAsync();
        await svc.GetStateAsync();

        Func<Task> act = () => RefusingCall(svc, path, first);

        var ex = (await act.Should().ThrowAsync<KeyNotFoundException>(
            "an expired id is the cache's answer, not a target failure for UiRefusals to translate")).Which;
        ex.GetType().Should().Be(typeof(KeyNotFoundException));
        ex.Message.Should().Contain(first).And.ContainEquivalentOf("expired").And.Contain("snapshot");
        ex.Message.Should().NotContainEquivalentOf("no longer available", "expired is not destroyed");
        input.VerifyNoOtherCalls();
    }

    /// <summary>
    /// D-2's "unknown action" refusal (line "default: throw new ArgumentException") only runs once
    /// the id resolved, so no headless test reached it. It is caller-facing, so Refusing must let it
    /// through unchanged, naming the action and the accepted list.
    /// </summary>
    [Fact]
    public async Task InteractAsync_an_unknown_action_on_a_live_id_is_an_ArgumentException_naming_it_and_the_accepted_list()
    {
        var input = new Mock<IInputService>(MockBehavior.Strict);
        using var svc = new UIAutomationService(input.Object, new Mock<IWindowService>().Object, new UiTreeOptions(1));
        var id = (await svc.GetStateAsync()).Root.ElementId;

        Func<Task> act = () => svc.InteractAsync(id, "hover", null);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("action");
        ex.Message.Should().Contain("'hover'").And.Contain("click|invoke|toggle|select|focus|type");
        input.VerifyNoOtherCalls();
    }

    /// <summary>
    /// type with no value is refused before the element is focused or anything is typed; the
    /// refusal is an ArgumentException naming <c>value</c>, passed through Refusing unchanged.
    /// </summary>
    [Fact]
    public async Task InteractAsync_type_without_a_value_on_a_live_id_is_an_ArgumentException_naming_value()
    {
        var input = new Mock<IInputService>(MockBehavior.Strict);
        using var svc = new UIAutomationService(input.Object, new Mock<IWindowService>().Object, new UiTreeOptions(1));
        var id = (await svc.GetStateAsync()).Root.ElementId;

        Func<Task> act = () => svc.InteractAsync(id, "type", null);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("value");
        ex.Message.Should().Contain("'type' requires a value");
        input.VerifyNoOtherCalls();
    }

    /// <summary>
    /// RED, the snapshot-generation sibling of
    /// <c>AssertElementAsync_exists_on_an_id_aged_out_of_the_cache_says_expired_not_unknown</c>.
    /// </summary>
    [Fact]
    public async Task AssertElementAsync_exists_on_an_id_a_newer_snapshot_replaced_says_expired_not_unknown()
    {
        using var svc = NewService(new UiTreeOptions(5));
        var old = (await SnapshotIdsAsync(svc, 5))[0];
        await SnapshotIdsAsync(svc, 5);

        var result = await svc.AssertElementAsync(old, "exists");

        result.Pass.Should().BeFalse();
        result.Observed.Should().ContainEquivalentOf("expired");
        result.Observed.Should().Contain("snapshot");
        result.Observed.Should().NotContainEquivalentOf("unknown");
    }
}
