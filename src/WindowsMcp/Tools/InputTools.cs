using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class InputTools
{
    private readonly IInputService _input;
    private readonly IClipboardService _clipboard;

    /// <summary>
    /// B-4 (roadmap C1): where <c>element_id</c> is turned into a point. An auto-property rather
    /// than a field only so the stubs below compile warning-free until the resolver lands.
    /// </summary>
    private IUIAutomationService Uia { get; }

    public InputTools(IInputService input, IClipboardService clipboard, IUIAutomationService uia)
    {
        _input = input;
        _clipboard = clipboard;
        Uia = uia;
    }

    private static MouseButton ParseButton(string s) => s.ToLowerInvariant() switch
    {
        "left" or "l" => MouseButton.Left,
        "right" or "r" => MouseButton.Right,
        "middle" or "m" => MouseButton.Middle,
        _ => throw new ArgumentException($"Unknown button '{s}'; expected left|right|middle")
    };

    /// <summary>E-3: the pause between repeated presses and the length of a hold; a seam so tests do not sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>E-3: between two presses of <c>key(repeat:N)</c>, the same pace <c>type</c> defaults to.</summary>
    private static readonly TimeSpan RepeatPace = TimeSpan.FromMilliseconds(5);

    /// <summary>E-3: the keys <c>click(modifiers:…)</c> may hold, in the order they go down.</summary>
    private static readonly string[] ClickModifiers = ["ctrl", "shift", "alt"];

    /// <summary>E-3: Ctrl, Shift, Alt or Win, either side: the keys key(hold_ms) and key(repeat) refuse.</summary>
    private static bool IsModifier(WindowsInput.VirtualKeyCode key) => key is
        WindowsInput.VirtualKeyCode.CONTROL or WindowsInput.VirtualKeyCode.LCONTROL or WindowsInput.VirtualKeyCode.RCONTROL or
        WindowsInput.VirtualKeyCode.SHIFT or WindowsInput.VirtualKeyCode.LSHIFT or WindowsInput.VirtualKeyCode.RSHIFT or
        WindowsInput.VirtualKeyCode.MENU or WindowsInput.VirtualKeyCode.LMENU or WindowsInput.VirtualKeyCode.RMENU or
        WindowsInput.VirtualKeyCode.LWIN or WindowsInput.VirtualKeyCode.RWIN;

    /// <summary>
    /// E-3: <c>"shift + Control"</c> → <c>[ctrl, shift]</c>. Win is refused on purpose: a Win key
    /// released on its own opens Start, so a click that fails half-way would leave a menu open.
    /// </summary>
    private static string[] ParseModifiers(string? modifiers)
    {
        if (string.IsNullOrWhiteSpace(modifiers)) return [];
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in modifiers.Split('+'))
        {
            var part = raw.Trim().ToLowerInvariant();
            if (part == "control") part = "ctrl";
            if (part.Length == 0)
                throw new ArgumentException($"modifiers '{modifiers}' has an empty part; expected ctrl|shift|alt joined with '+', e.g. ctrl+shift.", nameof(modifiers));
            if (Array.IndexOf(ClickModifiers, part) < 0)
                throw new ArgumentException($"Unknown modifier '{raw.Trim()}'; expected ctrl|shift|alt joined with '+', e.g. ctrl+shift (win is not supported).", nameof(modifiers));
            named.Add(part);
        }
        return ClickModifiers.Where(named.Contains).ToArray();
    }

    /// <summary>
    /// E-3: runs <paramref name="body"/> with <paramref name="keys"/> held, and never leaves one
    /// down. Every key that was sent a key-down — including one whose key-down threw, which may
    /// have half-landed — is sent a key-up in reverse order, on a token that cannot be cancelled;
    /// a release that fails is tried once more, and the others are attempted regardless. A failure
    /// of the body wins, unchanged. When the body succeeded and a key still could not be released,
    /// the caller is told the action happened (so it is not retried) and which keys may be down.
    /// </summary>
    /// <param name="done">What the body did, for that message: "The click was sent".</param>
    private async Task HoldingAsync(IReadOnlyList<string> keys, Func<Task> body, string done, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sent = new List<string>(keys.Count);
        Exception? failure = null;
        try
        {
            foreach (var key in keys)
            {
                sent.Add(key);
                await _input.KeyDownAsync(key, ct);
            }
            await body();
        }
        catch (Exception ex) { failure = ex; }

        var stuck = new List<string>();
        Exception? releaseFailure = null;
        for (int i = sent.Count - 1; i >= 0; i--)
        {
            try { await _input.KeyUpAsync(sent[i], CancellationToken.None); }
            catch (Exception first)
            {
                try { await _input.KeyUpAsync(sent[i], CancellationToken.None); }
                catch (Exception) { stuck.Add(sent[i]); releaseFailure ??= first; }
            }
        }

        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (releaseFailure is not null)
            throw new InvalidOperationException(
                $"{done}, but releasing {string.Join(", ", stuck)} failed twice, so {(stuck.Count == 1 ? "that key" : "those keys")} may still be down: " +
                $"{releaseFailure.Message} Do not repeat the action; press and release {string.Join(", ", stuck)} with key before sending more input.",
                releaseFailure);
    }

    [McpServerTool(Title = "Click", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Click at a point or on a snapshot element. Give x and y (physical virtual-desktop pixels: origin = top-left of the primary monitor, so monitors left of / above it have negative values; see multi_monitor) or element_id (an el_N id from snapshot/find_element; the click lands on the element's centre, and an off-screen element is refused with the reason). clicks: 1 single, 2 double, 3 triple, 0 = hover only (move the pointer there, press nothing). modifiers holds ctrl, shift and/or alt for the click (ctrl+shift; shift-click selects a range, ctrl-click adds to a selection): the pointer moves to the target before the keys go down, the click is sent, and the keys are released even if the click fails or the call is cancelled; a failed release is retried once, and if the release fails again the error names the key that may still be down and says the click was sent. Returns {action: click|hover, x, y, button, clicks, elementId?, name?, modifiers?}.")]
    public async Task<string> Click(
        [Description("X coordinate in physical pixels (virtual desktop); give with y, or use element_id")] int? x = null,
        [Description("Y coordinate in physical pixels (virtual desktop); give with x, or use element_id")] int? y = null,
        [Description("Element id (el_N) from snapshot or find_element; the click lands on its centre. Alternative to x/y")] string? element_id = null,
        [Description("Mouse button: left, right, or middle")] string button = "left",
        [Description("Number of clicks: 1 single, 2 double, 3 triple, 0 = hover only")] int clicks = 1,
        [Description("Keys to hold during the click: ctrl, shift, alt, joined with '+' (e.g. shift, ctrl+shift). They are released afterwards even if the click fails. Omit for a plain click")] string? modifiers = null,
        CancellationToken ct = default)
    {
        var parsedButton = ParseButton(button);
        if (clicks < 0)
            throw new ArgumentException($"clicks must be 0 (hover) or more, got {clicks}", nameof(clicks));
        // Before the target is resolved and before any input: a bad modifier is about the call.
        var held = ParseModifiers(modifiers);
        if (clicks == 0 && held.Length > 0)
            throw new ArgumentException("modifiers need a click: clicks 0 only moves the pointer, so there is nothing to hold them for.", nameof(modifiers));

        var target = await ResolveTargetAsync(x, y, element_id, allowCursor: false);
        if (clicks == 0)
        {
            await _input.HoverAsync(target.X, target.Y, 0);
            return JsonSerializer.Serialize(new { action = "hover", x = target.X, y = target.Y, button = parsedButton.ToString().ToLowerInvariant(), clicks, elementId = target.ElementId, name = target.Name });
        }
        if (held.Length == 0)
            await _input.ClickAsync(target.X, target.Y, parsedButton, clicks, ct);
        else
        {
            // The pointer goes to the target BEFORE a key goes down: a point the service refuses
            // (off every monitor) must not leave a bare Alt or Shift tap behind the refusal.
            ct.ThrowIfCancellationRequested();
            await _input.HoverAsync(target.X, target.Y, 0, ct);
            await HoldingAsync(held, () => _input.ClickAsync(target.X, target.Y, parsedButton, clicks, ct), "The click was sent", ct);
        }
        return JsonSerializer.Serialize(new { action = "click", x = target.X, y = target.Y, button = parsedButton.ToString().ToLowerInvariant(), clicks, elementId = target.ElementId, name = target.Name, modifiers = held.Length == 0 ? null : string.Join('+', held) });
    }

    [McpServerTool(Title = "Drag", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Drag with the button held: press at the origin, move through intermediate points (a first nudge past the system drag threshold, then 'steps' interpolated moves spread over duration_ms, so file managers, canvases and browser drag-and-drop recognise it), release on the destination. Destination: to_x/to_y or element_id (the element's centre). Origin: from_x/from_y, from_element_id, or nothing = the current cursor position. Coordinates are physical virtual-desktop pixels like click. duration_ms 0-10000 (default 300), steps 2-200 (default 20). Returns {fromX, fromY, toX, toY, button, durationMs, steps, fromTarget: point|element|cursor, elementId?, name?}.")]
    public async Task<string> Drag(
        [Description("Origin x; give with from_y, or use from_element_id, or omit both to start at the cursor")] int? from_x = null,
        [Description("Origin y; give with from_x")] int? from_y = null,
        [Description("Destination x; give with to_y, or use element_id")] int? to_x = null,
        [Description("Destination y; give with to_x")] int? to_y = null,
        [Description("Destination element id (el_N); the drop lands on its centre. Alternative to to_x/to_y")] string? element_id = null,
        [Description("Origin element id (el_N); the drag starts at its centre. Alternative to from_x/from_y")] string? from_element_id = null,
        [Description("Mouse button: left or right (middle is not supported)")] string button = "left",
        [Description("Total time of the motion in milliseconds, 0-10000")] int duration_ms = 300,
        [Description("Number of intermediate moves, 2-200")] int steps = 20)
    {
        var parsedButton = ParseButton(button);
        if (duration_ms is < 0 or > 10000)
            throw new ArgumentException($"duration_ms must be between 0 and 10000, got {duration_ms}", nameof(duration_ms));
        if (steps is < 2 or > 200)
            throw new ArgumentException($"steps must be between 2 and 200, got {steps}", nameof(steps));
        if (to_x is null && to_y is null && element_id is null)
            throw new ArgumentException("A drag needs a destination: to_x and to_y, or element_id.");

        var to = await ResolveTargetAsync(to_x, to_y, element_id, allowCursor: false, "to_x", "to_y", "element_id");
        var from = await ResolveTargetAsync(from_x, from_y, from_element_id, allowCursor: true, "from_x", "from_y", "from_element_id");

        await _input.DragAsync(from.X, from.Y, to.X, to.Y, parsedButton, duration_ms, steps);
        return JsonSerializer.Serialize(new
        {
            fromX = from.X, fromY = from.Y, toX = to.X, toY = to.Y,
            button = parsedButton.ToString().ToLowerInvariant(), durationMs = duration_ms, steps,
            fromTarget = from.Kind, elementId = to.ElementId, name = to.Name,
        });
    }

    [McpServerTool(Title = "Hover", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Move the cursor to coordinates and optionally hold it there (duration_ms). Same coordinate space as click: physical virtual-desktop pixels.")]
    public async Task<string> Hover(int x, int y, int duration_ms = 0)
    {
        await _input.HoverAsync(x, y, duration_ms);
        return $"hovered at ({x},{y}) for {duration_ms}ms";
    }

    [McpServerTool(Title = "Type text", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Type text. With no target it types at the current keyboard focus; with x/y or element_id (an el_N id from snapshot) it clicks that point or the element's centre first. clear:true selects all and deletes before typing (replace a field's content); caret: idle (default, type where the caret is) | start | end (move to the start/end of the text first); press_enter:true presses Enter last. Short text is typed key by key with newlines as Enter and tabs as Tab, paced by pace_ms; text of 200+ characters with no other control characters is pasted through the clipboard in one keystroke and the previous clipboard text is put back. Returns {typed, method: keys|paste, clipboardRestored?, x?, y?, elementId?, name?}.")]
    public async Task<string> Type(
        [Description("Text to type")] string text,
        [Description("X of a point to click first; give with y, or use element_id; omit both to type at the focus")] int? x = null,
        [Description("Y of a point to click first; give with x")] int? y = null,
        [Description("Element id (el_N) to click first; the click lands on its centre. Alternative to x/y")] string? element_id = null,
        [Description("Select all and delete before typing (replace the field's content)")] bool clear = false,
        [Description("idle (type at the caret) | start | end (move to the start/end of the text first)")] string caret = "idle",
        [Description("Press Enter after the text")] bool press_enter = false,
        [Description("Milliseconds between keystrokes when typing key by key (0 or more)")] int pace_ms = 5)
    {
        if (pace_ms < 0)
            throw new ArgumentException($"pace_ms must be 0 or more, got {pace_ms}", nameof(pace_ms));
        var caretPosition = caret.ToLowerInvariant() switch
        {
            "idle" => CaretPosition.Idle,
            "start" => CaretPosition.Start,
            "end" => CaretPosition.End,
            _ => throw new ArgumentException($"Unknown caret '{caret}'; expected idle|start|end", nameof(caret)),
        };

        Target? target = null;
        if (x is not null || y is not null || element_id is not null)
        {
            target = await ResolveTargetAsync(x, y, element_id, allowCursor: false);
            await _input.ClickAsync(target.X, target.Y, MouseButton.Left, 1);
        }

        var result = await _input.TypeAsync(text, new TypeOptions(clear, caretPosition, press_enter, pace_ms));
        return JsonSerializer.Serialize(new
        {
            typed = result.CharsTyped, method = result.Method, clipboardRestored = result.ClipboardRestored,
            x = target?.X, y = target?.Y, elementId = target?.ElementId, name = target?.Name,
        });
    }

    [McpServerTool(Title = "Press key", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Press one key: a character (a, 7, /), f1-f24, or a name (enter, tab, esc, backspace, delete, up/down/left/right, home, end, pageup, pagedown, insert, win, printscreen). repeat presses it 1-100 times in one call (ten Down arrows: key down, repeat 10); hold_ms keeps it down for up to 10000 ms and then releases it, held and not auto-repeated, for apps that read key state. Use one or the other, not both: repeat and hold_ms cannot be combined. Both take a non-modifier key (not ctrl, shift, alt or win: hold those with click's modifiers or shortcut), and a held key must need no modifier on the current layout. Letters are not case-sensitive: A is the a key (shortcut shift+a types a capital). A repeat that fails part-way reports how many presses landed. Returns 'pressed <key>', 'pressed <key> N times' or 'held <key> for M ms'. For chords use shortcut.")]
    public async Task<string> Key([Description("Key: a character, f1-f24, or a key name")] string key,
        [Description("Press the key this many times, 1-100 (default 1). Cannot be combined with hold_ms")] int repeat = 1,
        [Description("Hold the key down for this many milliseconds, 0-10000 (default 0 = a normal press). The key is held, it does not auto-repeat. Cannot be combined with repeat")] int hold_ms = 0,
        CancellationToken ct = default)
    {
        if (repeat is < 1 or > 100)
            throw new ArgumentException($"repeat must be between 1 and 100, got {repeat}", nameof(repeat));
        if (hold_ms is < 0 or > 10000)
            throw new ArgumentException($"hold_ms must be between 0 and 10000, got {hold_ms}", nameof(hold_ms));
        if (repeat > 1 && hold_ms > 0)
            throw new ArgumentException("repeat and hold_ms cannot be combined: press a key several times with repeat, or hold it once with hold_ms.", nameof(hold_ms));

        if (repeat == 1 && hold_ms == 0)
        {
            await _input.PressKeyAsync(key, ct);
            return $"pressed {key}";
        }

        // Before any press: a typo must not surface after the key went down or after N presses.
        ShortcutParser.Token token;
        try { token = ShortcutParser.ResolveKey(key); }
        catch (ArgumentException ex) { throw new ArgumentException($"Cannot press '{key}': {ex.Message}", nameof(key), ex); }

        if (hold_ms > 0)
        {
            // KeyDown/KeyUp send the bare virtual key, so a character that needs Shift on this
            // layout would hold a different key, and a Win key released alone opens Start.
            if (token.ImpliedModifiers.Length > 0)
                throw new ArgumentException($"Cannot hold '{key}': on this keyboard layout it needs a modifier, and a hold sends the bare key. Hold a key that needs none.", nameof(key));
            // A held modifier is the free-standing key-down this server deliberately does not
            // offer: if the process dies mid-hold, Ctrl stays down for the whole desktop.
            if (IsModifier(token.Key))
                throw new ArgumentException($"Cannot hold '{key}': a modifier is only held around an action. Use click with modifiers, or shortcut for a chord.", nameof(key));

            await HoldingAsync([key], () => Delay(TimeSpan.FromMilliseconds(hold_ms), ct), $"'{key}' was held for {hold_ms} ms", ct);
            return $"held {key} for {hold_ms} ms";
        }

        if (IsModifier(token.Key))
            throw new ArgumentException($"Cannot repeat '{key}': pressing a bare modifier again and again does nothing useful and can trigger Sticky Keys. Press it once, or use shortcut for a chord.", nameof(key));

        for (int i = 0; i < repeat; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { await _input.PressKeyAsync(key, ct); }
            // Not atomic: say how many landed, so a retry does not send them all again.
            catch (Exception ex) when (i > 0 && ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"'{key}' was pressed {i} of {repeat} times, then the next press failed: {ex.Message}", ex);
            }
            if (i < repeat - 1) await Delay(RepeatPace, ct);
        }
        return $"pressed {key} {repeat} times";
    }

    [McpServerTool(Title = "Press shortcut", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Press a chord: ctrl+c, ctrl+shift+s, win+r, alt+f4, ctrl+1. A single key such as win (opens Start) also works. Join parts with '+'; write plus for the + key.")]
    public async Task<string> Shortcut([Description("Chord, e.g. 'ctrl+c' or 'win+r'; a bare key like 'win' is allowed")] string shortcut)
    {
        await _input.PressShortcutAsync(shortcut);
        return $"pressed {shortcut}";
    }

    [McpServerTool(Title = "Scroll", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Scroll the mouse wheel. Coordinates are optional: give x and y (physical virtual-desktop pixels like click) to scroll at a point, element_id (an el_N id from snapshot; its centre) to scroll a specific region, or neither to scroll whatever is under the current cursor. amount is wheel notches. shift_wheel:true with left/right holds Shift and uses the vertical wheel, the horizontal scroll for apps that ignore the horizontal wheel. Returns {direction, amount, x, y, target: point|element|cursor, shiftWheel, elementId?, name?}.")]
    public async Task<string> Scroll(
        [Description("up|down|left|right")] string direction,
        [Description("Wheel notches (default 3)")] int amount = 3,
        [Description("X to scroll at; give with y, or use element_id, or omit both to scroll under the cursor")] int? x = null,
        [Description("Y to scroll at; give with x")] int? y = null,
        [Description("Element id (el_N) whose centre to scroll at. Alternative to x/y")] string? element_id = null,
        [Description("For left/right only: hold Shift and use the vertical wheel instead of the horizontal wheel")] bool shift_wheel = false)
    {
        var dir = direction.ToLowerInvariant();
        if (shift_wheel && dir is not ("left" or "right"))
            throw new ArgumentException("shift_wheel is the horizontal scroll: use it with left or right only.", nameof(shift_wheel));

        var target = await ResolveTargetAsync(x, y, element_id, allowCursor: true);
        await _input.ScrollAsync(target.X, target.Y, direction, amount, shift_wheel);
        return JsonSerializer.Serialize(new
        {
            direction, amount, x = target.X, y = target.Y, target = target.Kind,
            shiftWheel = shift_wheel, elementId = target.ElementId, name = target.Name,
        });
    }

    /// <summary>The point a verb aims at and how it was named (roadmap C1/C2).</summary>
    private sealed record Target(int X, int Y, string Kind, string? ElementId, string? Name);

    /// <summary>
    /// One rule for every verb: exactly one of (x and y) or element_id — an element resolves to
    /// its centre through <see cref="ElementTarget"/> and an unreachable one is refused with the
    /// reason before any input is sent; with <paramref name="allowCursor"/>, nothing at all means
    /// the live cursor.
    /// </summary>
    private async Task<Target> ResolveTargetAsync(int? x, int? y, string? elementId, bool allowCursor,
        string xName = "x", string yName = "y", string idName = "element_id")
    {
        bool hasPoint = x is not null || y is not null;
        if (hasPoint && elementId is not null)
            throw new ArgumentException($"Give either coordinates ({xName} and {yName}) or {idName}, not both.");
        if (hasPoint && (x is null || y is null))
            throw new ArgumentException($"{xName} and {yName} must be given together (coordinates in virtual-desktop pixels).");
        if (elementId is not null)
        {
            var info = await Uia.GetElementAsync(elementId);
            var (cx, cy) = ElementTarget.CentreOf(info);
            return new Target(cx, cy, "element", elementId, info.Name);   // the caller's id, not the lookup's re-minted one
        }
        if (hasPoint) return new Target(x!.Value, y!.Value, "point", null, null);
        if (!allowCursor)
            throw new ArgumentException($"Give a target: coordinates {xName} and {yName}, or {idName}.");
        var cursor = await _input.GetCursorPositionAsync();
        return new Target(cursor.X, cursor.Y, "cursor", null, null);
    }

    [McpServerTool(Title = "Wait", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Pause for a number of seconds (more than 0, at most 60) and return; use it between an action and the next snapshot instead of a PowerShell sleep. Returns {\"waited\": seconds}. For a longer pause, or to wait until something is on screen, use wait_for.")]
    public async Task<string> Wait(
        [Description("Seconds to pause: more than 0 and at most 60 (fractions allowed)")] double seconds,
        CancellationToken ct = default)
    {
        // NaN fails the first comparison, infinity the second: nothing outside (0, 60] gets a delay.
        if (!(seconds > 0 && seconds <= 60))
            throw new ArgumentException($"seconds must be more than 0 and at most 60, got {seconds}; for a longer or conditional wait use wait_for.", nameof(seconds));

        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        return JsonSerializer.Serialize(new { waited = seconds });
    }

    [McpServerTool(Title = "Multi-select", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Click several targets in one call, holding Ctrl for the whole batch (multi-select in lists, file managers, canvases). targets_json is a JSON array of {x,y} points (virtual-desktop pixels) or {element_id} objects (el_N ids from snapshot); a JSON string holding that array is accepted too. Every target is resolved before anything is clicked, so an off-screen element refuses the whole batch with nothing done. The clicks run in order and stop at the first failure: the result then carries failedIndex and error with the results so far, so the batch is not atomic. ctrl:false clicks without the modifier. Returns {count, ctrl, results:[{index, x, y, elementId?, name?, ok}], failedIndex?, error?}.")]
    public async Task<string> MultiSelect(
        [Description("JSON array of targets: {\"x\":..,\"y\":..} or {\"element_id\":\"el_N\"} objects, at least one")] string targets_json,
        [Description("Hold Ctrl from before the first click until after the last (default true)")] bool ctrl = true)
    {
        var targets = BatchTargets.ParseTargets(targets_json);
        var resolved = new List<Target>(targets.Count);
        foreach (var t in targets)
            resolved.Add(await ResolveTargetAsync(t.X, t.Y, t.ElementId, allowCursor: false));   // all refusals before any input

        var results = new List<object>();
        int? failedIndex = null; string? error = null;
        if (ctrl) await _input.KeyDownAsync("ctrl");
        try
        {
            for (int i = 0; i < resolved.Count; i++)
            {
                var r = resolved[i];
                try
                {
                    await _input.ClickAsync(r.X, r.Y, MouseButton.Left, 1);
                    results.Add(new { index = i, x = r.X, y = r.Y, elementId = r.ElementId, name = r.Name, ok = true });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failedIndex = i; error = ex.Message;
                    break;
                }
            }
        }
        finally
        {
            if (ctrl) await _input.KeyUpAsync("ctrl");   // never leave the modifier down
        }
        return JsonSerializer.Serialize(new { count = targets.Count, ctrl, results, failedIndex, error });
    }

    [McpServerTool(Title = "Multi-edit", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Fill several fields in one call: for each entry, click its target then type its text. entries_json is a JSON array of objects with a target ({x,y} in virtual-desktop pixels, or element_id = an el_N id from snapshot), text (required), and optionally clear (select all and delete first) and press_enter; a JSON string holding that array is accepted too. Every entry is resolved before anything is typed, so an off-screen element refuses the whole batch with nothing done. Entries run in order and stop at the first failure: the result then carries failedIndex and error with the results so far, so the batch is not atomic. Returns {count, results:[{index, x, y, elementId?, name?, typed, method, ok}], failedIndex?, error?}.")]
    public async Task<string> MultiEdit(
        [Description("JSON array of entries: {\"x\":..,\"y\":..} or {\"element_id\":\"el_N\"} plus \"text\" (required), \"clear\" and \"press_enter\" (optional)")] string entries_json)
    {
        var entries = BatchTargets.ParseEntries(entries_json);
        var resolved = new List<Target>(entries.Count);
        foreach (var e in entries)
            resolved.Add(await ResolveTargetAsync(e.X, e.Y, e.ElementId, allowCursor: false));   // all refusals before any input

        var results = new List<object>();
        int? failedIndex = null; string? error = null;
        for (int i = 0; i < resolved.Count; i++)
        {
            var r = resolved[i];
            var e = entries[i];
            try
            {
                await _input.ClickAsync(r.X, r.Y, MouseButton.Left, 1);
                var typed = await _input.TypeAsync(e.Text!, new TypeOptions(e.Clear, CaretPosition.Idle, e.PressEnter, 5));
                results.Add(new { index = i, x = r.X, y = r.Y, elementId = r.ElementId, name = r.Name, typed = typed.CharsTyped, method = typed.Method, ok = true });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedIndex = i; error = ex.Message;
                break;
            }
        }
        return JsonSerializer.Serialize(new { count = entries.Count, results, failedIndex, error });
    }

    [McpServerTool(Title = "Clipboard", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Clipboard get/set.")]
    public async Task<string> Clipboard(
        [Description("Action: get or set")] string action,
        [Description("Text to set; ignored for 'get'")] string? text = null)
    {
        switch (action.ToLowerInvariant())
        {
            case "get":
                return await _clipboard.GetTextAsync() ?? "";
            case "set":
                if (text == null) throw new ArgumentException("'set' requires text parameter");
                await _clipboard.SetTextAsync(text);
                return $"set ({text.Length} chars)";
            default:
                throw new ArgumentException($"Unknown clipboard action '{action}'; expected get|set");
        }
    }
}
