using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsInput;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

// Disambiguate: H.InputSimulator also exposes a WindowsInput.MouseButton enum.
using MouseButton = WindowsMcp.Abstractions.Models.MouseButton;

namespace WindowsMcp.Services;

public sealed class InputService : IInputService
{
    private readonly InputSimulator _sim = new();

    /// <summary>
    /// B-1: the clipboard the paste path borrows and puts back. DI supplies the real one; the
    /// parameterless construction (tests, D-2's fallback) has none, so a paste is impossible
    /// there and long text is typed instead.
    /// </summary>
    internal IClipboardService? Clipboard { get; }

    /// <summary>B-1: where typing plans are executed; null = the real simulator, a recorder in the unit tests.</summary>
    internal IKeyboardSink? Keyboard { get; }

    private SimulatorKeyboardSink? _simulatorSink;
    private IKeyboardSink Sink
    {
        get
        {
            if (Keyboard is not null) return Keyboard;
            return _simulatorSink ??= new SimulatorKeyboardSink(_sim);
        }
    }

    /// <summary>After Ctrl+V the target reads the clipboard on its own schedule; the previous text is put back after this.</summary>
    private static readonly TimeSpan PasteSettle = TimeSpan.FromMilliseconds(150);

    public InputService(IClipboardService? clipboard = null) => Clipboard = clipboard;

    /// <summary>Test seam (B-1): record the keystrokes a type plan produces without injecting any.</summary>
    internal InputService(IClipboardService? clipboard, IKeyboardSink keyboard) : this(clipboard)
        => Keyboard = keyboard;

    /// <summary>
    /// Places the cursor at (<paramref name="x"/>, <paramref name="y"/>): physical pixels on the
    /// virtual desktop, origin at the primary monitor's top-left, so monitors left of / above it
    /// have negative coordinates. The button and wheel events sent afterwards carry no position of
    /// their own (H.InputSimulator's LeftButtonClick etc.), so they act wherever this put the cursor.
    /// </summary>
    /// <remarks>
    /// SetCursorPos rather than an absolute SendInput move (D-3): the process is Per-Monitor-V2 DPI
    /// aware (Program.cs), so this, UIA BoundingRectangle and multi_monitor share one physical-pixel
    /// space, and there is no 0..65535 normalisation to get wrong — the previous code scaled by the
    /// primary monitor's size, so every secondary-monitor click landed somewhere else.
    /// SetCursorPos silently clamps a point outside the virtual screen to the nearest edge, which is
    /// that same failure in a different coat, so the position is read back and a mismatch throws.
    /// </remarks>
    private static void MoveCursor(int x, int y)
    {
        if (!PInvoke.SetCursorPos(x, y))
            throw new InvalidOperationException($"SetCursorPos({x},{y}) failed (Win32 error {Marshal.GetLastPInvokeError()}).");

        if (!PInvoke.GetCursorPos(out var actual))
            throw new InvalidOperationException($"GetCursorPos failed (Win32 error {Marshal.GetLastPInvokeError()}).");

        if (actual.X == x && actual.Y == y) return;

        int left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        int top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        int width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        int height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        throw new ArgumentOutOfRangeException(nameof(x),
            $"({x},{y}) is not on any monitor: the cursor landed at ({actual.X},{actual.Y}). The virtual screen spans " +
            $"x {left}..{left + width - 1}, y {top}..{top + height - 1} in physical pixels with the origin at the " +
            "primary monitor's top-left; see multi_monitor for each monitor's bounds.");
    }

    public Task<ClickResult> ClickAsync(int x, int y, MouseButton button = MouseButton.Left, int clicks = 1, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        MoveCursor(x, y);
        for (int i = 0; i < clicks; i++)
        {
            ct.ThrowIfCancellationRequested();
            switch (button)
            {
                case MouseButton.Left: _sim.Mouse.LeftButtonClick(); break;
                case MouseButton.Right: _sim.Mouse.RightButtonClick(); break;
                case MouseButton.Middle: _sim.Mouse.MiddleButtonClick(); break;
            }
        }
        return Task.FromResult(new ClickResult(x, y, button, clicks));
    }

    /// <summary>
    /// D-10: a bad argument value, so an <see cref="ArgumentException"/> the caller gets to read —
    /// the earlier <see cref="NotSupportedException"/> was masked by the call-tool filter.
    /// </summary>
    private static ArgumentException MiddleDragRefused(string paramName)
        => new("Middle-button drag is not supported (the input library has no middle-button press and release); use left or right.", paramName);

    public Task<DragResult> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // H.InputSimulator exposes only Left/Right ButtonDown/Up; no MiddleButtonDown/Up.
        // Reject middle-button drag rather than silently degrading to a left-button drag.
        if (button == MouseButton.Middle) throw MiddleDragRefused(nameof(button));

        MoveCursor(fromX, fromY);

        switch (button)
        {
            case MouseButton.Left: _sim.Mouse.LeftButtonDown(); break;
            case MouseButton.Right: _sim.Mouse.RightButtonDown(); break;
        }

        MoveCursor(toX, toY);

        switch (button)
        {
            case MouseButton.Left: _sim.Mouse.LeftButtonUp(); break;
            case MouseButton.Right: _sim.Mouse.RightButtonUp(); break;
        }

        return Task.FromResult(new DragResult(fromX, fromY, toX, toY, button));
    }

    /// <summary>
    /// B-2: press at the origin, then a short nudge past the system drag threshold and
    /// <paramref name="steps"/> interpolated moves spread over <paramref name="durationMs"/>, then
    /// release exactly on the destination. The button is released even when the drag is cancelled.
    /// </summary>
    public async Task<DragResult> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button, int durationMs, int steps, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (button == MouseButton.Middle) throw MiddleDragRefused(nameof(button));
        if (durationMs < 0) throw new ArgumentOutOfRangeException(nameof(durationMs), durationMs, "durationMs cannot be negative");
        if (steps < 1) throw new ArgumentOutOfRangeException(nameof(steps), steps, "steps must be at least 1");

        int nudge = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXDRAG) + 1;
        var points = DragPath.Points((fromX, fromY), (toX, toY), steps, nudge);
        var pause = TimeSpan.FromMilliseconds((double)durationMs / steps);

        MoveCursor(fromX, fromY);
        if (button == MouseButton.Left) _sim.Mouse.LeftButtonDown(); else _sim.Mouse.RightButtonDown();
        try
        {
            foreach (var (x, y) in points)
            {
                ct.ThrowIfCancellationRequested();
                MoveCursor(x, y);
                if (pause > TimeSpan.Zero) await Task.Delay(pause, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (button == MouseButton.Left) _sim.Mouse.LeftButtonUp(); else _sim.Mouse.RightButtonUp();
        }
        return new DragResult(fromX, fromY, toX, toY, button);
    }

    public Task HoverAsync(int x, int y, int durationMs = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        MoveCursor(x, y);
        if (durationMs > 0) return Task.Delay(durationMs, ct);
        return Task.CompletedTask;
    }

    public Task<TypeResult> TypeAsync(string text, CancellationToken ct = default)
        => TypeAsync(text, new TypeOptions(), ct);

    /// <summary>
    /// B-1 (roadmap C8): runs the <see cref="TypePlanner"/> plan — clear, caret, then the text by
    /// keys (newlines and tabs as Enter and Tab, <c>PaceMs</c> between steps) or by one clipboard
    /// paste that restores the previous clipboard text afterwards — and reports which path ran.
    /// A paste that cannot borrow the clipboard falls back to keys and says so.
    /// </summary>
    public async Task<TypeResult> TypeAsync(string text, TypeOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var plan = TypePlanner.Plan(text, options);   // a negative pace is refused before any keystroke
        var sink = Sink;
        if (sink is SimulatorKeyboardSink simulator) simulator.PaceMs = options.PaceMs;
        string method = plan.Method;
        bool? restored = null;

        for (int i = 0; i < plan.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = plan.Steps[i];
            switch (step.Kind)
            {
                case "shortcut": sink.Shortcut(step.Value); break;
                case "key": sink.Key(step.Value); break;
                case "text": sink.Text(step.Value); break;
                case "paste":
                    {
                        var outcome = await PasteAsync(step.Value, sink, ct).ConfigureAwait(false);
                        if (outcome is null) method = "keys";   // typed instead: the clipboard was not available
                        else restored = outcome;
                        break;
                    }
            }
            if (options.PaceMs > 0 && i < plan.Steps.Count - 1)
                await Task.Delay(options.PaceMs, ct).ConfigureAwait(false);
        }

        return new TypeResult(text.Length, method, restored);
    }

    /// <summary>
    /// Borrows the clipboard for one Ctrl+V. Null when there was no clipboard to borrow (the text
    /// was typed instead); otherwise whether the previous text was put back.
    /// </summary>
    private async Task<bool?> PasteAsync(string text, IKeyboardSink sink, CancellationToken ct)
    {
        if (Clipboard is null)
        {
            sink.Text(text);
            return null;
        }

        string? previous;
        try
        {
            previous = await Clipboard.GetTextAsync(ct).ConfigureAwait(false);
            await Clipboard.SetTextAsync(text, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sink.Text(text);   // another app holds the clipboard: type it, slowly but surely
            return null;
        }

        sink.Shortcut("ctrl+v");
        if (previous is null) return false;   // nothing to restore: the clipboard held no text

        try
        {
            if (Keyboard is null) await Task.Delay(PasteSettle, ct).ConfigureAwait(false);   // real desktop only
            await Clipboard.SetTextAsync(previous, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public Task PressKeyAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Sink.Key(key);
        return Task.CompletedTask;
    }

    public Task KeyDownAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Sink.KeyDown(key);
        return Task.CompletedTask;
    }

    public Task KeyUpAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Sink.KeyUp(key);
        return Task.CompletedTask;
    }

    public Task PressShortcutAsync(string shortcut, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Sink.Shortcut(shortcut);
        return Task.CompletedTask;
    }

    public Task ScrollAsync(int x, int y, string direction, int amount = 3, CancellationToken ct = default)
        => ScrollAsync(x, y, direction, amount, shiftWheel: false, ct);

    /// <summary>
    /// B-3: the wheel at a point. <paramref name="shiftWheel"/> sends Shift + the vertical wheel
    /// for <c>left</c>/<c>right</c> — the horizontal scroll for apps that ignore the horizontal
    /// wheel — and is refused for a vertical direction.
    /// </summary>
    public async Task ScrollAsync(int x, int y, string direction, int amount, bool shiftWheel, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dir = direction.ToLowerInvariant();
        if (dir is not ("up" or "down" or "left" or "right"))
            throw new ArgumentException($"Invalid direction: '{direction}'", nameof(direction));
        if (shiftWheel && dir is "up" or "down")
            throw new ArgumentException("shiftWheel is the horizontal scroll: use it with left or right only.", nameof(shiftWheel));

        await HoverAsync(x, y, 0, ct);
        if (shiftWheel)
        {
            // Shift + wheel up scrolls left, Shift + wheel down scrolls right, by convention.
            _sim.Keyboard.KeyDown(VirtualKeyCode.SHIFT);
            try { _sim.Mouse.VerticalScroll(dir == "left" ? amount : -amount); }
            finally { _sim.Keyboard.KeyUp(VirtualKeyCode.SHIFT); }
            return;
        }
        switch (dir)
        {
            case "up": _sim.Mouse.VerticalScroll(amount); break;
            case "down": _sim.Mouse.VerticalScroll(-amount); break;
            case "left": _sim.Mouse.HorizontalScroll(-amount); break;
            case "right": _sim.Mouse.HorizontalScroll(amount); break;
        }
    }

    /// <summary>Where the cursor is, in the same virtual-desktop pixels <see cref="MoveCursor"/> accepts (A-11).</summary>
    public Task<CursorPosition> GetCursorPositionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!PInvoke.GetCursorPos(out var p))
            throw new InvalidOperationException($"GetCursorPos failed (Win32 error {Marshal.GetLastPInvokeError()}).");
        return Task.FromResult(new CursorPosition(p.X, p.Y));
    }
}

/// <summary>
/// B-1: the production <see cref="IKeyboardSink"/> — H.InputSimulator's keyboard. Text goes in
/// one character per SendInput call with <see cref="PaceMs"/> between them: a whole chunk in one
/// call is exactly what a target that falls behind its input queue garbles (it reads the last
/// injected character for every queued key, so "abc" arrives as "c"), which is why upstream
/// paces per key too.
/// </summary>
internal sealed class SimulatorKeyboardSink(InputSimulator sim) : IKeyboardSink
{
    /// <summary>Milliseconds between characters and after every step; set by the executor from <c>TypeOptions.PaceMs</c>.</summary>
    public int PaceMs { get; set; } = 5;

    public void Shortcut(string chord)
    {
        var parsed = ShortcutParser.Parse(chord);
        if (parsed.Modifiers.Length == 0)
            sim.Keyboard.KeyPress(parsed.Key);                                    // bare key: "win" opens Start, "esc" dismisses
        else
            sim.Keyboard.ModifiedKeyStroke(parsed.Modifiers, parsed.Key);
    }

    public void Key(string key)
    {
        var token = ShortcutParser.ResolveKey(key);
        if (token.ImpliedModifiers.Length == 0)
            sim.Keyboard.KeyPress(token.Key);
        else
            sim.Keyboard.ModifiedKeyStroke(token.ImpliedModifiers, token.Key);    // e.g. key("+") on a US layout = Shift + OEM_PLUS
    }

    public void KeyDown(string key) => sim.Keyboard.KeyDown(ShortcutParser.ResolveKey(key).Key);

    public void KeyUp(string key) => sim.Keyboard.KeyUp(ShortcutParser.ResolveKey(key).Key);

    public void Text(string text)
    {
        // One character per call, so a surrogate pair stays together and nothing is batched.
        for (int i = 0; i < text.Length; i++)
        {
            bool pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            sim.Keyboard.TextEntry(pair ? text.Substring(i, 2) : text[i].ToString());
            if (pair) i++;
            if (PaceMs > 0 && i < text.Length - 1) Thread.Sleep(PaceMs);
        }
    }
}
