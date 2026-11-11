using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-1 (R3): the EXECUTOR half of <c>type</c> — the part that turns a <c>TypePlan</c> into
/// keystrokes and a clipboard round-trip. Every test here drives a recording sink instead of
/// H.InputSimulator, so nothing is injected and the class stays Unit (roadmap C10); what it pins
/// is the ORDER of what would have been injected, which is the only thing a mocked
/// <c>IInputService</c> in a tool test cannot see. The B-2/B-3 guards that fire BEFORE any
/// injection (a bad drag budget, a bad scroll direction) live here too, for the same reason:
/// they are reachable without a desktop.
/// </summary>
[Trait("Category", "Unit")]
public class InputServiceTypeTests
{
    /// <summary>
    /// Keyboard and clipboard in one log, because the interesting facts are the interleavings:
    /// set-clipboard BEFORE ctrl+v BEFORE restore, and clear BEFORE the text.
    /// </summary>
    private sealed class Recorder : IKeyboardSink, IClipboardService
    {
        public List<string> Log { get; } = [];
        public string? Clipboard { get; set; }
        public bool FailSet { get; set; }

        /// <summary>Fail only the SECOND set - the restore - the way a clipboard grabbed mid-paste does.</summary>
        public bool FailRestore { get; set; }

        public int SetCalls { get; private set; }

        public void Shortcut(string chord) => Log.Add($"shortcut:{chord}");
        public void Key(string key) => Log.Add($"key:{key}");
        public void Text(string text) => Log.Add($"text:{text}");

        // B-7 added the held-modifier pair to the sink; typing never uses them, and a type plan
        // that started holding keys down would show up here.
        public void KeyDown(string key) => Log.Add($"down:{key}");
        public void KeyUp(string key) => Log.Add($"up:{key}");

        public Task<string?> GetTextAsync(CancellationToken ct = default)
        {
            Log.Add("clipboard.get");
            return Task.FromResult(Clipboard);
        }

        public Task SetTextAsync(string text, CancellationToken ct = default)
        {
            SetCalls++;
            Log.Add($"clipboard.set:{text}");
            if (FailSet || (FailRestore && SetCalls > 1))
                throw new InvalidOperationException("OpenClipboard failed: another app holds the clipboard");
            Clipboard = text;
            return Task.CompletedTask;
        }
    }

    private static readonly TypeOptions Fast = new(PaceMs: 0);

    // ---- keys mode ---------------------------------------------------------------------------

    [Fact]
    public async Task TypeAsync_executes_the_whole_plan_in_order_on_the_keyboard()
    {
        var recorder = new Recorder();
        var service = new InputService(recorder, recorder);

        var result = await service.TypeAsync("a\nb", new TypeOptions(Clear: true, PressEnter: true, PaceMs: 0));

        recorder.Log.Should().Equal(
            "shortcut:ctrl+a", "key:backspace",
            "text:a", "key:enter", "text:b",
            "key:enter");
        result.Method.Should().Be("keys");
        result.CharsTyped.Should().Be(3, "the count is the text the caller gave, not the steps taken");
        result.ClipboardRestored.Should().BeNull("no paste happened, so there was nothing to restore");
    }

    [Fact]
    public async Task TypeAsync_keys_mode_never_touches_the_clipboard()
    {
        var recorder = new Recorder();

        await new InputService(recorder, recorder).TypeAsync("short", Fast);

        recorder.Log.Should().NotContain(entry => entry.StartsWith("clipboard"),
            "borrowing the user's clipboard for five characters is a side effect nobody asked for");
    }

    [Fact]
    public async Task TypeAsync_moves_the_caret_with_a_chord_the_shortcut_parser_can_resolve()
    {
        // A "key" step goes through PressKeyAsync, which resolves ONE key; ctrl+end must therefore
        // arrive as a chord or the caret move throws at runtime on every call.
        var recorder = new Recorder();

        await new InputService(recorder, recorder).TypeAsync("hi", new TypeOptions(Caret: CaretPosition.End, PaceMs: 0));

        recorder.Log.Should().Equal("shortcut:ctrl+end", "text:hi");
    }

    // ---- paste mode --------------------------------------------------------------------------

    [Fact]
    public async Task TypeAsync_paste_borrows_the_clipboard_pastes_and_puts_the_old_text_back()
    {
        var recorder = new Recorder { Clipboard = "the user's own text" };
        var service = new InputService(recorder, recorder);
        var text = new string('a', 300);

        var result = await service.TypeAsync(text, Fast);

        recorder.Log.Should().Equal(
            "clipboard.get",
            $"clipboard.set:{text}",
            "shortcut:ctrl+v",
            "clipboard.set:the user's own text");
        result.Method.Should().Be("paste");
        result.CharsTyped.Should().Be(300);
        result.ClipboardRestored.Should().BeTrue();
        recorder.Clipboard.Should().Be("the user's own text",
            "the clipboard is the user's, not ours - what we found there is what they must find there");
    }

    [Fact]
    public async Task TypeAsync_paste_reports_that_nothing_was_restored_when_the_clipboard_held_no_text()
    {
        // An image or a file list on the clipboard cannot be read or put back through
        // IClipboardService; the honest answer is clipboardRestored:false, not a silent wipe.
        var recorder = new Recorder { Clipboard = null };
        var text = new string('b', 250);

        var result = await new InputService(recorder, recorder).TypeAsync(text, Fast);

        result.Method.Should().Be("paste");
        result.ClipboardRestored.Should().BeFalse();
        recorder.Log.Should().Equal("clipboard.get", $"clipboard.set:{text}", "shortcut:ctrl+v");
    }

    [Fact]
    public async Task TypeAsync_falls_back_to_keys_when_the_clipboard_cannot_be_set()
    {
        // ClipboardServiceTests is already flaky for exactly this reason (another app holding the
        // clipboard). A failed borrow must not fail the call - it must type instead and say so.
        var recorder = new Recorder { Clipboard = "before", FailSet = true };
        var text = new string('c', 300);

        var result = await new InputService(recorder, recorder).TypeAsync(text, Fast);

        result.Method.Should().Be("keys", "the response tells the truth about which path ran");
        result.CharsTyped.Should().Be(300);
        recorder.Log.Should().Contain($"text:{text}");
        recorder.Log.Should().NotContain("shortcut:ctrl+v", "nothing was ever put on the clipboard");
        recorder.Log.Count(entry => entry.StartsWith("clipboard.set")).Should().Be(1,
            "one failed attempt, and no restore of a clipboard we never changed");
    }

    [Fact]
    public async Task TypeAsync_without_a_clipboard_service_types_long_text_instead_of_pasting()
    {
        // `new InputService()` call sites (tests, D-2's fallback) have no clipboard; paste is
        // simply not available there and the text must still arrive.
        var recorder = new Recorder();
        var text = new string('d', 400);

        var result = await new InputService(null, recorder).TypeAsync(text, Fast);

        result.Method.Should().Be("keys");
        recorder.Log.Should().Equal($"text:{text}");
    }

    [Fact]
    public async Task TypeAsync_paste_still_honours_clear_and_press_enter_around_the_paste()
    {
        var recorder = new Recorder { Clipboard = "old" };
        var text = new string('e', 200);

        await new InputService(recorder, recorder)
            .TypeAsync(text, new TypeOptions(Clear: true, PressEnter: true, PaceMs: 0));

        recorder.Log.Should().Equal(
            "shortcut:ctrl+a", "key:backspace",
            "clipboard.get", $"clipboard.set:{text}", "shortcut:ctrl+v", "clipboard.set:old",
            "key:enter");
    }

    // ---- pace, cancellation, and the overload D-2 depends on ---------------------------------

    [Fact]
    public async Task TypeAsync_paces_the_keys_it_sends()
    {
        // The pace exists because TextEntry bursts drop keys in some apps. A pace of 25 ms over
        // five chunks cannot come back in under 100 ms unless it is being ignored.
        var recorder = new Recorder();
        var stopwatch = Stopwatch.StartNew();

        await new InputService(recorder, recorder).TypeAsync("a\nb\nc\nd\ne", new TypeOptions(PaceMs: 25));

        stopwatch.Stop();
        stopwatch.Elapsed.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(100);
        stopwatch.Elapsed.TotalMilliseconds.Should().BeLessThan(3000, "nine steps at 25 ms is not seconds of work");
    }

    [Fact]
    public async Task TypeAsync_with_options_throws_when_cancellation_is_already_requested()
    {
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => new InputService(recorder, recorder).TypeAsync("hi", Fast, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        recorder.Log.Should().BeEmpty("the token is checked before the first keystroke, as everywhere else");
    }

    [Fact]
    public async Task TypeAsync_with_a_negative_pace_is_refused_before_anything_is_typed()
    {
        var recorder = new Recorder();

        var act = () => new InputService(recorder, recorder).TypeAsync("hi", new TypeOptions(PaceMs: -1));

        await act.Should().ThrowAsync<ArgumentException>();
        recorder.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task The_single_argument_overload_means_the_same_as_default_options()
    {
        // D-2's interact_element(type) keyboard fallback calls TypeAsync(text, ct). B-1 ADDS an
        // overload; it does not replace one, and the old call must keep behaving.
        var recorder = new Recorder();

        var result = await new InputService(null, recorder).TypeAsync("hi");

        result.Method.Should().Be("keys");
        result.CharsTyped.Should().Be(2);
        result.ClipboardRestored.Should().BeNull();
        recorder.Log.Should().Equal("text:hi");
    }

    [Fact]
    public void Both_TypeAsync_overloads_are_on_the_interface()
    {
        // A signature test, because the D-2 call site (UIAutomationService's PendingText hand-off
        // off the STA thread) is only covered by UIAutomation-category tests: turning the old
        // overload into an optional parameter would compile there and break every other caller.
        var type = typeof(IInputService);

        type.GetMethod(nameof(IInputService.TypeAsync), [typeof(string), typeof(CancellationToken)])
            .Should().NotBeNull("D-2's keyboard fallback calls TypeAsync(text, ct)");
        type.GetMethod(nameof(IInputService.TypeAsync), [typeof(string), typeof(TypeOptions), typeof(CancellationToken)])
            .Should().NotBeNull("B-1 adds the options overload beside it");
    }

    [Fact]
    public void The_old_scroll_and_drag_overloads_survive_beside_the_new_ones()
    {
        // Same rule for B-2 and B-3: the pre-B overloads stay, byte-compatible, so nothing that
        // already calls them changes behaviour.
        var type = typeof(IInputService);

        type.GetMethod(nameof(IInputService.ScrollAsync),
                [typeof(int), typeof(int), typeof(string), typeof(int), typeof(CancellationToken)])
            .Should().NotBeNull();
        type.GetMethod(nameof(IInputService.ScrollAsync),
                [typeof(int), typeof(int), typeof(string), typeof(int), typeof(bool), typeof(CancellationToken)])
            .Should().NotBeNull("B-3 adds the shift-wheel overload");

        type.GetMethod(nameof(IInputService.DragAsync),
                [typeof(int), typeof(int), typeof(int), typeof(int), typeof(MouseButton), typeof(CancellationToken)])
            .Should().NotBeNull();
        type.GetMethod(nameof(IInputService.DragAsync),
                [typeof(int), typeof(int), typeof(int), typeof(int), typeof(MouseButton), typeof(int), typeof(int), typeof(CancellationToken)])
            .Should().NotBeNull("B-2 adds the duration/steps overload");
    }

    [Fact]
    public void InputService_takes_the_clipboard_from_DI_and_still_has_a_parameterless_construction()
    {
        // The registration is AddSingleton<IInputService, InputService>(): DI has to be able to
        // pick a public constructor, and the paste path needs the clipboard through it.
        var ctors = typeof(InputService).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        ctors.Should().ContainSingle("one public constructor keeps DI's choice unambiguous");
        var parameters = ctors[0].GetParameters();
        parameters.Should().HaveCount(1);
        parameters[0].ParameterType.Should().Be(typeof(IClipboardService));
        parameters[0].HasDefaultValue.Should().BeTrue("every existing `new InputService()` must keep compiling");
    }

    // ---- B-2: the rejection that survives the new overload ------------------------------------

    [Fact]
    public async Task DragAsync_with_duration_and_steps_still_refuses_the_middle_button()
    {
        // Checked before the cursor moves, so this injects nothing and stays Unit.
        // H.InputSimulator has no MiddleButtonDown/Up; degrading to a left drag would silently do
        // the wrong thing to the desktop.
        // D-10 (R2): the refusal used to be a NotSupportedException, which the call-tool filter
        // masks; a bad argument value is an ArgumentException naming the button.
        var act = () => new InputService().DragAsync(0, 0, 10, 10, MouseButton.Middle, 300, 20);

        var ex = (await act.Should().ThrowAsync<ArgumentException>().WithMessage("*iddle*")).Which;
        ex.ParamName.Should().Be("button");
        ToolErrors.IsCallerFacing(ex).Should().BeTrue("a refusal the caller cannot read is indistinguishable from a crash");
    }

    [Fact]
    public async Task DragAsync_without_duration_refuses_the_middle_button_as_a_caller_facing_argument_error()
    {
        // D-10 (R2), the other overload: same check, same type. Checked before the cursor moves,
        // so this injects nothing and stays Unit.
        var act = () => new InputService().DragAsync(0, 0, 10, 10, MouseButton.Middle);

        var ex = (await act.Should().ThrowAsync<ArgumentException>().WithMessage("*iddle*")).Which;
        ex.ParamName.Should().Be("button");
        ToolErrors.IsCallerFacing(ex).Should().BeTrue();
    }

    [Fact]
    public async Task TypeAsync_paste_reports_a_clipboard_it_could_not_put_back()
    {
        // The borrow succeeded, Ctrl+V went out, and only the restore failed (another app grabbed
        // the clipboard in between). The text IS typed, so the call succeeds - but the user's
        // clipboard now holds ours, and clipboardRestored:false is the only honest way to say so.
        var recorder = new Recorder { Clipboard = "the user's own text", FailRestore = true };
        var text = new string('f', 300);

        var result = await new InputService(recorder, recorder).TypeAsync(text, Fast);

        result.Method.Should().Be("paste", "the paste itself happened; only the tidy-up failed");
        result.CharsTyped.Should().Be(300);
        result.ClipboardRestored.Should().BeFalse();
        recorder.Log.Should().Equal(
            "clipboard.get",
            $"clipboard.set:{text}",
            "shortcut:ctrl+v",
            "clipboard.set:the user's own text");
    }

    [Fact]
    public async Task TypeAsync_does_not_pace_after_the_last_step()
    {
        // A one-step plan with a 1.5 s pace must return at once: pacing BETWEEN keys is the point,
        // and a trailing sleep would add pace_ms to every single call for nothing.
        var recorder = new Recorder();
        var stopwatch = Stopwatch.StartNew();

        await new InputService(recorder, recorder).TypeAsync("hi", new TypeOptions(PaceMs: 1500));

        stopwatch.Stop();
        recorder.Log.Should().Equal(["text:hi"], "one step, so there is no gap to pace");
        stopwatch.Elapsed.TotalMilliseconds.Should().BeLessThan(750,
            "the pace is applied between steps only, so a single-step plan waits for nothing");
    }

    // ---- B-1: the key and chord verbs now go through the same sink ----------------------------

    [Fact]
    public async Task PressKeyAsync_goes_through_the_keyboard_sink()
    {
        // Not cosmetic: a "key" step of a type plan and the `key` tool must reach the keyboard the
        // same way, or the plan's Enter and the tool's Enter can diverge.
        var recorder = new Recorder();

        await new InputService(null, recorder).PressKeyAsync("enter");

        recorder.Log.Should().Equal("key:enter");
    }

    [Fact]
    public async Task PressShortcutAsync_goes_through_the_keyboard_sink()
    {
        var recorder = new Recorder();

        await new InputService(null, recorder).PressShortcutAsync("ctrl+shift+s");

        recorder.Log.Should().Equal("shortcut:ctrl+shift+s");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_key_verbs_check_cancellation_before_they_touch_the_keyboard(bool shortcut)
    {
        var recorder = new Recorder();
        var service = new InputService(null, recorder);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => shortcut ? service.PressShortcutAsync("ctrl+c", cts.Token) : service.PressKeyAsync("a", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        recorder.Log.Should().BeEmpty();
    }

    // ---- B-2: the budget guards, all checked before the pointer moves -------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task DragAsync_with_duration_and_steps_refuses_a_negative_duration(int durationMs)
    {
        // Checked before SetCursorPos and before the button goes down, which is what makes this
        // Unit: a guard that fired after the press would leave a button held down on the desktop.
        var act = () => new InputService().DragAsync(0, 0, 10, 10, MouseButton.Left, durationMs, 20);

        (await act.Should().ThrowAsync<ArgumentOutOfRangeException>()).WithMessage("*durationMs*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task DragAsync_with_duration_and_steps_refuses_fewer_than_one_step(int steps)
    {
        var act = () => new InputService().DragAsync(0, 0, 10, 10, MouseButton.Left, 300, steps);

        (await act.Should().ThrowAsync<ArgumentOutOfRangeException>()).WithMessage("*steps*");
    }

    [Fact]
    public async Task DragAsync_with_duration_and_steps_checks_cancellation_before_the_button_goes_down()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => new InputService().DragAsync(0, 0, 10, 10, MouseButton.Left, 300, 20, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- B-3: the scroll guards, likewise before the pointer moves ----------------------------

    [Theory]
    [InlineData("sideways")]
    [InlineData("")]
    [InlineData("upp")]
    public async Task ScrollAsync_refuses_an_unknown_direction_before_it_moves_the_pointer(string direction)
    {
        // Through the FOUR-argument overload, so the pre-B-3 signature is proven to still validate
        // (it now delegates to the shift-wheel one, and a delegation that skipped the guard would
        // send a wheel event in no direction at all).
        var act = () => new InputService().ScrollAsync(0, 0, direction, 3);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("direction").And.Contain($"'{direction}'", "the refusal quotes back what it was given");
    }

    [Theory]
    [InlineData("up")]
    [InlineData("DOWN")]
    public async Task ScrollAsync_refuses_the_shift_wheel_for_a_vertical_direction(string direction)
    {
        // Shift+wheel IS the vertical wheel with Shift held: honouring it for up/down would scroll
        // sideways. The service refuses it itself, not only the tool, because D-2 and any future
        // caller reach the service directly.
        var act = () => new InputService().ScrollAsync(0, 0, direction, 3, shiftWheel: true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().ContainEquivalentOf("shiftwheel").And.ContainEquivalentOf("left");
    }

    [Fact]
    public async Task ScrollAsync_checks_cancellation_before_anything_else()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => new InputService().ScrollAsync(0, 0, "down", 3, shiftWheel: false, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
