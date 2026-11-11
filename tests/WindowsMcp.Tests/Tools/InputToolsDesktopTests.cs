using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// B phase 2 on a live desktop: the four verbs driven end to end through the REAL services against
/// the Notepad fixture. Every mocked test above proves the tool asks for the right thing; only
/// these prove the thing actually happens (the <c>disk_inspect mode:reclaimable</c> lesson in
/// CLAUDE.md).
/// <para>
/// These INJECT INPUT, so they are UIAutomation-category and in <see cref="DesktopCollection"/>
/// (roadmap C10): they move the pointer, type, drag and open a Notepad window. Every one targets
/// the fixture's window BY ELEMENT ID or by an explicit point inside it, never "whatever has
/// focus". Keys-mode assertions use SHORT ASCII only - per-keystroke Unicode injection is
/// unreliable on this desktop (recorded in PR #20); the long-text case rides the paste path,
/// which is a single Ctrl+V and does not care.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class InputToolsDesktopTests : IClassFixture<NotepadFixture>, IDisposable
{
    private readonly UIAutomationService _uia;
    private readonly ClipboardService _clipboard;
    private readonly InputTools _tools;

    public InputToolsDesktopTests(NotepadFixture np)
    {
        np.BringToForeground();
        _clipboard = new ClipboardService();
        _uia = new UIAutomationService(new InputService(_clipboard), new WindowService());
        _tools = new InputTools(new InputService(_clipboard), _clipboard, _uia);
    }

    public void Dispose()
    {
        _uia.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The editor inside the NOTEPAD window (D-5 scope=window), not the foreground's document: if
    /// focus slipped, a foreground search returns another app's Document and every assertion below
    /// would then be about that app.
    /// </summary>
    private async Task<string> EditorIdAsync()
    {
        var found = await _uia.FindElementAsync("", FindKind.Text, FindScope.Window, "Notepad");
        var editor = found.Matches.FirstOrDefault(m => m.ControlType is "Document" or "Edit")
            ?? throw new Xunit.Sdk.XunitException("No Document/Edit element in the Notepad window");
        return editor.ElementId;
    }

    private async Task<string> ReadEditorAsync(string id)
    {
        await Task.Delay(400);   // injected keystrokes are asynchronous to the UIA read
        return await _uia.GetTextAsync(id);
    }

    // ---- B-1: type ----------------------------------------------------------------------------

    [Fact]
    public async Task Type_with_clear_replaces_what_the_editor_held()
    {
        var id = await EditorIdAsync();
        await _tools.Type("something that must not survive", element_id: id, clear: true);
        var stamp = $"b1{Guid.NewGuid():N}"[..10];

        var json = InputVerb.Json(await _tools.Type(stamp, element_id: id, clear: true));

        InputVerb.Str(json, "method").Should().Be("keys", "ten characters is far below the paste threshold");
        var text = await ReadEditorAsync(id);
        text.Should().Contain(stamp);
        text.Should().NotContain("must not survive", "clear = ctrl+a then backspace, not append");
    }

    [Fact]
    public async Task Type_with_press_enter_leaves_the_caret_on_a_new_line()
    {
        var id = await EditorIdAsync();

        await _tools.Type("alpha", element_id: id, clear: true, press_enter: true);
        await _tools.Type("beta");

        var text = await ReadEditorAsync(id);
        text.Should().MatchRegex("alpha[\r\n]+beta",
            "press_enter submits the line, so the next text starts on the following one");
    }

    [Fact]
    public async Task Type_with_caret_end_appends_instead_of_inserting_at_the_caret()
    {
        var id = await EditorIdAsync();
        await _tools.Type("abc", element_id: id, clear: true);

        await _tools.Type("XYZ", element_id: id, caret: "end");

        (await ReadEditorAsync(id)).Should().Contain("abcXYZ");
    }

    [Fact]
    public async Task Type_pastes_five_thousand_characters_intact_and_gives_the_clipboard_back()
    {
        // The headline of C8: 5 000 characters at 5 ms would be 25 seconds of injection and some
        // apps drop keys along the way. One Ctrl+V is the whole gesture - and the user's clipboard
        // has to be exactly where they left it afterwards.
        var id = await EditorIdAsync();
        var sentinel = $"clipboard-sentinel-{Guid.NewGuid():N}";
        await _clipboard.SetTextAsync(sentinel);
        var payload = string.Concat(Enumerable.Range(0, 500).Select(i => $"line{i:D4}-abcdefg "));
        payload.Length.Should().BeGreaterThan(5000);

        var json = InputVerb.Json(await _tools.Type(payload, element_id: id, clear: true));

        InputVerb.Str(json, "method").Should().Be("paste");
        InputVerb.Flag(json, "clipboardRestored").Should().BeTrue();
        var text = await ReadEditorAsync(id);
        text.Should().Contain("line0000-abcdefg").And.Contain("line0499-abcdefg",
            "the first and the last of 5 000 characters both arrived");
        (await _clipboard.GetTextAsync()).Should().Be(sentinel,
            "pasting behind the user's back is only safe if the clipboard is put back");
    }

    // ---- B-4: click ---------------------------------------------------------------------------

    [Fact]
    public async Task Click_by_element_id_gives_the_editor_keyboard_focus()
    {
        var id = await EditorIdAsync();
        var editor = await _uia.GetElementAsync(id);
        var bounds = editor.Bounds!;

        var json = InputVerb.Json(await _tools.Click(element_id: id));

        InputVerb.Str(json, "action").Should().Be("click");
        // D-11 (R19): GetElementAsync answers under the caller's own id instead of minting a fresh
        // one per call, so the live echo IS the id this test passed in - the same thing the mocked
        // InputToolsClickTests assert. Before D-11 this test asserted the opposite and documented
        // the leak as if it were the contract.
        var echoed = InputVerb.Str(json, "elementId");
        echoed.Should().Be(id, "the tool echoes the id it was given; nothing is re-minted");
        var echoedInfo = await _uia.GetElementAsync(echoed);
        echoedInfo.ElementId.Should().Be(id);
        echoedInfo.ControlType.Should().Be(editor.ControlType);
        echoedInfo.Bounds.Should().Be(bounds, "the echoed id has to resolve to the control that was clicked");
        InputVerb.Str(json, "name").Should().Be(editor.Name,
            "the model asked for an id and gets back which control it actually hit");
        int x = InputVerb.Num(json, "x"), y = InputVerb.Num(json, "y");
        (x, y).Should().Be((bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2),
            "click(element_id) aims at the element's centre");
        var focused = await _uia.AssertElementAsync(id, "focused");
        focused.Pass.Should().BeTrue($"the click landed at ({x},{y}), observed: {focused.Observed}");
    }

    [Fact]
    public async Task Click_with_zero_clicks_parks_the_pointer_on_the_element_without_pressing_anything()
    {
        var id = await EditorIdAsync();
        var element = await _uia.GetElementAsync(id);
        var expected = (element.Bounds!.X + element.Bounds.Width / 2, element.Bounds.Y + element.Bounds.Height / 2);

        var json = InputVerb.Json(await _tools.Click(element_id: id, clicks: 0));

        InputVerb.Str(json, "action").Should().Be("hover");
        (InputVerb.Num(json, "x"), InputVerb.Num(json, "y")).Should().Be(expected,
            "the point it reports is the centre of the bounds it read, never a remembered one");
        var cursor = await new InputService().GetCursorPositionAsync();
        (cursor.X, cursor.Y).Should().Be(expected, "clicks:0 moves the pointer and nothing else");
    }

    // ---- B-3: scroll --------------------------------------------------------------------------

    [Fact]
    public async Task Scroll_with_no_coordinates_scrolls_whatever_is_under_the_cursor()
    {
        // The parity point: `scroll(direction:"down")` with no point at all. The pointer is parked
        // on the editor first with clicks:0, which is also B-4's hover path.
        var id = await EditorIdAsync();
        var payload = string.Concat(Enumerable.Range(0, 300).Select(i => $"line {i}\n"));
        await _tools.Type(payload, element_id: id, clear: true);
        await Task.Delay(500);
        // Typing left the caret - and the view - at the BOTTOM of those 300 lines, i.e. vertical
        // percent 100, where a downward wheel has nowhere left to go and no assertion could ever
        // observe one. Ctrl+Home puts the view back at the top first (the editor still holds the
        // focus the type above gave it); the assertions are then against the percent READ below,
        // never a literal.
        await _tools.Shortcut("ctrl+home");
        await Task.Delay(400);
        await _tools.Click(element_id: id, clicks: 0);
        double Percent(SnapshotResult snap) =>
            snap.Scrollable.First(s => s.ControlType is "Document" or "Edit").Scroll.VerticalPercent;
        var before = Percent(await _uia.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground)));
        before.Should().BeLessThan(100,
            "ctrl+home scrolled the editor back to the top, so a downward wheel has somewhere to go");

        var json = InputVerb.Json(await _tools.Scroll("down", amount: 8));

        InputVerb.Str(json, "target").Should().Be("cursor");
        await Task.Delay(400);
        var after = Percent(await _uia.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground)));
        after.Should().BeGreaterThan(before, "the wheel turned under the cursor, on the editor");
    }

    // ---- B-2: drag ----------------------------------------------------------------------------

    [Fact]
    public async Task Drag_across_a_line_of_text_selects_it()
    {
        // Today's press-jump-release drag selects nothing in many controls because they never see
        // an intermediate WM_MOUSEMOVE. Reading the selection back through Ctrl+C is the only
        // observation that distinguishes "dragged" from "clicked twice".
        var id = await EditorIdAsync();
        await _tools.Type("The quick brown fox jumps over the lazy dog", element_id: id, clear: true);
        await Task.Delay(400);
        var element = await _uia.GetElementAsync(id);
        var bounds = element.Bounds!;
        var restore = await _clipboard.GetTextAsync();
        await _clipboard.SetTextAsync("no selection was copied");

        await _tools.Drag(
            from_x: bounds.X + 8, from_y: bounds.Y + 12,
            to_x: bounds.X + 160, to_y: bounds.Y + 12,
            duration_ms: 400, steps: 25);
        await Task.Delay(300);
        await _tools.Shortcut("ctrl+c");
        await Task.Delay(400);

        var copied = await _clipboard.GetTextAsync();
        try
        {
            copied.Should().NotBeNullOrWhiteSpace();
            copied.Should().NotBe("no selection was copied", "nothing was selected, so Ctrl+C copied nothing");
            "The quick brown fox jumps over the lazy dog".Should().Contain(copied!.Trim(),
                "the selection is a run of the line that was dragged across");
        }
        finally
        {
            if (restore is not null) await _clipboard.SetTextAsync(restore);
        }
    }
}
