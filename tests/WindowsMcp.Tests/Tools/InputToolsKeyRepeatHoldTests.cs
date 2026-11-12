using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// E-3: <c>key(repeat: N)</c> is N presses paced 5 ms apart (one call instead of N), and
/// <c>key(hold_ms: M)</c> keeps one key down for M ms and always lets it go. Both are composed in
/// the tool layer from <see cref="IInputService"/>'s PressKey / KeyDown / KeyUp and the
/// <see cref="InputTools.Delay"/> seam, so one log shows presses, pauses and releases in the order
/// the desktop would see them, and no test waits in real time.
/// </summary>
[Trait("Category", "Unit")]
public class InputToolsKeyRepeatHoldTests
{
    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// One log for presses, downs, ups and delays, plus the token each received. Failures are
    /// thrown synchronously, as the real <c>InputService</c> does from its non-async methods.
    /// </summary>
    private sealed class Rig
    {
        public List<string> Log { get; } = [];
        public List<(string Call, CancellationToken Ct)> Tokens { get; } = [];
        public Mock<IInputService> Input { get; } = new();
        public int Presses { get; private set; }
        public Action<int>? OnPress { get; set; }
        public Dictionary<int, Exception> FailPress { get; } = [];
        public Exception? FailDown { get; set; }
        public Exception? FailUp { get; set; }

        /// <summary>With <see cref="FailUp"/>: only the first release throws; the retry succeeds.</summary>
        public bool FailUpOnlyOnce { get; set; }

        public int UpAttempts => Log.Count(e => e.StartsWith("up:", StringComparison.Ordinal));

        public Func<TimeSpan, CancellationToken, Task>? DelayImpl { get; set; }

        public Rig()
        {
            Input.Setup(s => s.PressKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string k, CancellationToken ct) =>
                {
                    Presses++;
                    Log.Add($"press:{k}"); Tokens.Add(("press", ct));
                    OnPress?.Invoke(Presses);
                    if (FailPress.TryGetValue(Presses, out var e)) throw e;
                    return Task.CompletedTask;
                });
            Input.Setup(s => s.KeyDownAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string k, CancellationToken ct) =>
                {
                    Log.Add($"down:{k}"); Tokens.Add(("down", ct));
                    if (FailDown is not null) throw FailDown;
                    return Task.CompletedTask;
                });
            Input.Setup(s => s.KeyUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string k, CancellationToken ct) =>
                {
                    Log.Add($"up:{k}"); Tokens.Add(("up", ct));
                    if (FailUp is not null)
                    {
                        var e = FailUp;
                        if (FailUpOnlyOnce) FailUp = null;
                        throw e;
                    }
                    return Task.CompletedTask;
                });
        }

        public InputTools Tools()
        {
            var tools = InputVerb.Tools(Input);
            tools.Delay = (span, ct) =>
            {
                Log.Add($"delay:{span.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)}");
                Tokens.Add(("delay", ct));
                return DelayImpl?.Invoke(span, ct) ?? Task.CompletedTask;
            };
            return tools;
        }

        public CancellationToken TokenOf(string call) => Tokens.Single(t => t.Call == call).Ct;
    }

    private static void NoInputAtAll(Rig rig) =>
        rig.Log.Should().BeEmpty("a refusal presses nothing and waits for nothing");

    // ---- K1: the defaults are today's key ----------------------------------------------------

    [Fact]
    public async Task Key_with_defaults_presses_once_with_the_request_token_and_says_so()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        var text = await rig.Tools().Key("down", ct: cts.Token);

        text.Should().Be("pressed down");
        rig.Log.Should().Equal(new[] { "press:down" });
        rig.Input.Verify(s => s.PressKeyAsync("down", cts.Token), Times.Once);
        rig.Input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Key_with_repeat_one_and_hold_zero_written_out_is_the_same_single_press()
    {
        var rig = new Rig();

        var text = await rig.Tools().Key("enter", repeat: 1, hold_ms: 0);

        text.Should().Be("pressed enter");
        rig.Log.Should().Equal(new[] { "press:enter" });
    }

    // ---- K2 / K3 / K4: bounds and the either-or rule, all before any input --------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task Key_refuses_a_repeat_outside_one_to_a_hundred_by_name(int repeat)
    {
        var rig = new Rig();

        var act = () => rig.Tools().Key("down", repeat: repeat);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("repeat");
        ex.Message.Should().Contain("1").And.Contain("100", "the refusal states the bounds");
        NoInputAtAll(rig);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task Key_refuses_a_hold_outside_zero_to_ten_seconds_by_name(int holdMs)
    {
        var rig = new Rig();

        var act = () => rig.Tools().Key("a", hold_ms: holdMs);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("hold_ms");
        ex.Message.Should().Contain("10000", "the refusal states the ceiling");
        NoInputAtAll(rig);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(100, 10000)]
    [InlineData(5, 250)]
    public async Task Key_refuses_repeat_and_hold_together_and_names_both(int repeat, int holdMs)
    {
        var rig = new Rig();

        var act = () => rig.Tools().Key("a", repeat: repeat, hold_ms: holdMs);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("repeat").And.Contain("hold_ms");
        NoInputAtAll(rig);
    }

    [Fact]
    public async Task Key_accepts_the_upper_bounds_of_repeat_and_hold()
    {
        var repeatRig = new Rig();
        (await repeatRig.Tools().Key("down", repeat: 100)).Should().Be("pressed down 100 times");
        repeatRig.Presses.Should().Be(100);

        var holdRig = new Rig();
        (await holdRig.Tools().Key("a", hold_ms: 10000)).Should().Be("held a for 10000 ms");
        holdRig.Log.Should().Equal(new[] { "down:a", "delay:10000", "up:a" });
    }

    // ---- K5: the key name is checked before the first press ----------------------------------

    [Theory]
    [InlineData("notakey", 3, 0)]
    [InlineData("ctrl+c", 3, 0)]
    [InlineData("", 3, 0)]
    [InlineData("notakey", 1, 250)]
    [InlineData("ctrl+c", 1, 250)]
    [InlineData("  ", 1, 250)]
    public async Task Key_refuses_an_unknown_key_before_any_press_when_repeating_or_holding(string key, int repeat, int holdMs)
    {
        var rig = new Rig();

        var act = () => rig.Tools().Key(key, repeat: repeat, hold_ms: holdMs);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        if (!string.IsNullOrWhiteSpace(key))
            ex.Message.Should().Contain(key, "the refusal names the key it could not resolve");
        NoInputAtAll(rig);
    }

    // ---- K6: repeat is N presses with N-1 pacing delays ---------------------------------------

    [Fact]
    public async Task Key_repeat_presses_n_times_with_a_five_ms_pause_between_and_none_after()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        var text = await rig.Tools().Key("down", repeat: 3, ct: cts.Token);

        text.Should().Be("pressed down 3 times");
        rig.Log.Should().Equal(new[] { "press:down", "delay:5", "press:down", "delay:5", "press:down" });
        rig.Tokens.Should().OnlyContain(t => t.Ct == cts.Token, "every press and every pause honours the request");
        rig.Input.Verify(s => s.KeyDownAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Input.Verify(s => s.KeyUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(100)]
    public async Task Key_repeat_sends_exactly_n_presses_and_n_minus_one_pauses(int repeat)
    {
        var rig = new Rig();
        var delays = new List<TimeSpan>();
        rig.DelayImpl = (span, _) => { delays.Add(span); return Task.CompletedTask; };

        await rig.Tools().Key("down", repeat: repeat);

        rig.Presses.Should().Be(repeat);
        delays.Should().HaveCount(repeat - 1).And.OnlyContain(d => d == Pace);
        rig.Log[^1].Should().Be("press:down", "no pause is paid after the last press");
    }

    [Fact]
    public async Task Key_repeat_forwards_the_key_exactly_as_written()
    {
        var rig = new Rig();

        await rig.Tools().Key("Down", repeat: 2);

        rig.Input.Verify(s => s.PressKeyAsync("Down", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Key_repeat_of_a_character_the_layout_shifts_is_allowed()
    {
        // Implied modifiers only matter to a HOLD (K10); PressKeyAsync already sends the implied
        // Shift around each tap, so repeating '+' is fine.
        var shifted = ShiftedCharactersOnThisLayout().First();
        var rig = new Rig();

        await rig.Tools().Key(shifted, repeat: 2);

        rig.Presses.Should().Be(2);
    }

    // ---- K7 / K8: cancellation and failure stop the loop --------------------------------------

    [Fact]
    public async Task Key_repeat_stops_at_cancellation_with_no_further_presses()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.OnPress = n => { if (n == 2) cts.Cancel(); };
        rig.DelayImpl = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };

        var act = () => rig.Tools().Key("down", repeat: 10, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Presses.Should().Be(2, "the request was cancelled after the second press");
    }

    [Fact]
    public async Task Key_repeat_cancelled_during_a_pause_presses_nothing_more()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.DelayImpl = (_, ct) =>
        {
            cts.Cancel();
            return Task.FromCanceled(ct);
        };

        var act = () => rig.Tools().Key("down", repeat: 10, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Presses.Should().Be(1);
    }

    [Fact]
    public async Task Key_repeat_on_an_already_cancelled_request_presses_nothing()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        rig.DelayImpl = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };

        var act = () => rig.Tools().Key("down", repeat: 5, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Presses.Should().Be(0, "GREEN decision 9: the loop checks the token before each press, the first one included");
    }

    [Fact]
    public async Task Key_repeat_checks_cancellation_before_each_press_even_when_the_pause_ignores_it()
    {
        // The loop's own check, not the delay's: a pause that does not observe the token (or a
        // zero pace) must still not let the remaining presses through.
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.OnPress = n => { if (n == 2) cts.Cancel(); };
        rig.DelayImpl = (_, _) => Task.CompletedTask;

        var act = () => rig.Tools().Key("down", repeat: 10, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Presses.Should().Be(2);
    }

    [Fact]
    public async Task Key_repeat_cancelled_after_the_last_press_still_reports_success()
    {
        // GREEN decision 9: all N presses landed; a late cancellation does not turn that into an error.
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.OnPress = n => { if (n == 3) cts.Cancel(); };
        rig.DelayImpl = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };

        var text = await rig.Tools().Key("down", repeat: 3, ct: cts.Token);

        text.Should().Be("pressed down 3 times");
        rig.Presses.Should().Be(3);
    }

    // ---- GREEN decisions 5-8: check order, the plain path, the bad-key error, Win ---------------

    [Theory]
    [InlineData(0, -1, "repeat")]        // both out of range: repeat is checked first
    [InlineData(101, 20000, "repeat")]
    [InlineData(2, -1, "hold_ms")]       // repeat fine, hold out of range
    [InlineData(2, 10001, "hold_ms")]
    [InlineData(2, 1, "hold_ms")]        // both in range, combined: the combination names hold_ms
    public async Task Key_checks_repeat_then_hold_ms_then_the_combination(int repeat, int holdMs, string paramName)
    {
        var rig = new Rig();

        var act = () => rig.Tools().Key("a", repeat: repeat, hold_ms: holdMs);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be(paramName);
        NoInputAtAll(rig);
    }

    [Fact]
    public async Task Key_with_defaults_leaves_an_unknown_key_to_the_service()
    {
        // GREEN decision 6: repeat 1 / hold 0 is today's path - no up-front parse, the service
        // resolves (and refuses) the key exactly as before E-3.
        var rig = new Rig();

        var text = await rig.Tools().Key("notakey");

        text.Should().Be("pressed notakey");
        rig.Input.Verify(s => s.PressKeyAsync("notakey", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("notakey", 3, 0)]
    [InlineData("ctrl+c", 3, 0)]
    [InlineData("notakey", 1, 250)]
    public async Task Key_bad_key_error_names_key_prefixes_the_call_and_wraps_the_parser_error(string key, int repeat, int holdMs)
    {
        // GREEN decision 7.
        var rig = new Rig();

        var act = () => rig.Tools().Key(key, repeat: repeat, hold_ms: holdMs);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("key");
        ex.Message.Should().StartWith($"Cannot press '{key}': ");
        ex.InnerException.Should().BeOfType<ArgumentException>();
        ex.Message.Should().Contain(ex.InnerException!.Message.Split(" (Parameter")[0],
            "the parser's own explanation is carried into the caller-facing message");
        NoInputAtAll(rig);
    }

    // ---- R2: hold and repeat take a non-modifier key (re-pins GREEN decision 8) ---------------

    /// <summary>Every name <see cref="ShortcutParser"/> resolves to a Ctrl, Shift, Alt or Win key.</summary>
    private static readonly string[] ModifierKeyNames =
    [
        "ctrl", "control", "lctrl", "rctrl",
        "shift", "lshift", "rshift",
        "alt", "option", "lalt", "ralt",
        "win", "windows", "cmd", "super", "meta", "lwin", "rwin",
    ];

    private static readonly WindowsInput.VirtualKeyCode[] ModifierVirtualKeys =
    [
        WindowsInput.VirtualKeyCode.CONTROL, WindowsInput.VirtualKeyCode.LCONTROL, WindowsInput.VirtualKeyCode.RCONTROL,
        WindowsInput.VirtualKeyCode.SHIFT, WindowsInput.VirtualKeyCode.LSHIFT, WindowsInput.VirtualKeyCode.RSHIFT,
        WindowsInput.VirtualKeyCode.MENU, WindowsInput.VirtualKeyCode.LMENU, WindowsInput.VirtualKeyCode.RMENU,
        WindowsInput.VirtualKeyCode.LWIN, WindowsInput.VirtualKeyCode.RWIN,
    ];

    public static TheoryData<string> ModifierKeys()
    {
        var data = new TheoryData<string>();
        foreach (var name in ModifierKeyNames) data.Add(name);
        data.Add("WIN");     // case does not get a modifier past the check
        data.Add("Shift");
        return data;
    }

    [Theory]
    [MemberData(nameof(ModifierKeys))]
    public async Task Key_hold_refuses_a_modifier_key_and_points_at_click_modifiers_or_shortcut(string key)
    {
        // A held modifier is a free-standing key-down: the #456 class of bug E-3 refuses to build.
        var rig = new Rig();

        var act = () => rig.Tools().Key(key, hold_ms: 100);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("key");
        ex.Message.Should().Contain("shortcut").And.Contain("click", "the refusal names the two ways to hold a modifier for a purpose");
        NoInputAtAll(rig);
    }

    [Theory]
    [MemberData(nameof(ModifierKeys))]
    public async Task Key_repeat_refuses_a_modifier_key_and_mentions_sticky_keys(string key)
    {
        // Repeating a bare modifier does nothing useful, and five Shift taps open Sticky Keys.
        var rig = new Rig();

        var act = () => rig.Tools().Key(key, repeat: 5);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("key");
        ex.Message.Should().ContainEquivalentOf("sticky keys");
        NoInputAtAll(rig);
    }

    [Theory]
    [InlineData("shift")]
    [InlineData("win")]
    [InlineData("alt")]
    public async Task Key_plain_press_of_a_modifier_is_unchanged(string key)
    {
        // R2 only covers hold and repeat: key("win") still opens Start, as it always has.
        var rig = new Rig();

        (await rig.Tools().Key(key)).Should().Be($"pressed {key}");
        rig.Input.Verify(s => s.PressKeyAsync(key, It.IsAny<CancellationToken>()), Times.Once);
        rig.Input.VerifyNoOtherCalls();
    }

    [Fact]
    public void Every_name_the_parser_resolves_to_a_modifier_is_in_the_refusal_theories()
    {
        // Keeps the theories above honest: if ShortcutParser learns a new alias for any modifier
        // virtual key, this fails until the alias is listed (and therefore proved refused).
        var named = typeof(ShortcutParser).GetField("Named", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null) as IReadOnlyDictionary<string, WindowsInput.VirtualKeyCode>;

        named.Should().NotBeNull("ShortcutParser keeps its key names in a static 'Named' dictionary");
        named!.Where(kv => ModifierVirtualKeys.Contains(kv.Value))
            .Select(kv => kv.Key.ToLowerInvariant())
            .Should().BeEquivalentTo(ModifierKeyNames);
    }

    [Fact]
    public async Task Key_hold_on_an_already_cancelled_request_sends_nothing()
    {
        // GREEN decision 2: no key-down, and so no key-up for a key that never went down.
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => rig.Tools().Key("a", hold_ms: 100, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        NoInputAtAll(rig);
    }

    // ---- R3: a repeat that fails part-way says how many presses landed (re-pins K8) ----------

    [Fact]
    public async Task Key_repeat_whose_first_press_fails_propagates_it_unchanged()
    {
        var rig = new Rig();
        var boom = new IOException("SendInput failed");
        rig.FailPress[1] = boom;

        var act = () => rig.Tools().Key("down", repeat: 5);

        (await act.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(boom, "nothing landed, so there is nothing to add");
        rig.Presses.Should().Be(1);
    }

    [Theory]
    [InlineData(2, 5)]
    [InlineData(5, 5)]
    [InlineData(10, 100)]
    public async Task Key_repeat_that_fails_part_way_reports_how_many_presses_landed(int failingPress, int repeat)
    {
        var rig = new Rig();
        var boom = new IOException("SendInput failed");
        rig.FailPress[failingPress] = boom;

        var act = () => rig.Tools().Key("down", repeat: repeat);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>(
            "a caller that retries the whole call would otherwise press the key too many times")).Which;
        ex.Message.Should().Contain($"{failingPress - 1} of {repeat}");
        ex.Message.Should().Contain(boom.Message);
        ex.InnerException.Should().BeSameAs(boom);
        rig.Presses.Should().Be(failingPress, "no press after the failing one");
    }

    [Fact]
    public async Task Key_repeat_that_fails_part_way_with_an_argument_exception_is_still_wrapped()
    {
        // The wrapper is about the partial effect, not the cause's type.
        var rig = new Rig();
        var boom = new ArgumentException("refused mid-way");
        rig.FailPress[3] = boom;

        var act = () => rig.Tools().Key("down", repeat: 4);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.InnerException.Should().BeSameAs(boom);
    }

    [Fact]
    public async Task Key_repeat_cancellation_part_way_is_never_wrapped()
    {
        var rig = new Rig();
        var cancelled = new OperationCanceledException("cancelled by the transport");
        rig.FailPress[3] = cancelled;

        var act = () => rig.Tools().Key("down", repeat: 5);

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(cancelled);
        rig.Presses.Should().Be(3);
    }

    // ---- K9: hold is down, wait, up - and the up always happens --------------------------------

    [Fact]
    public async Task Key_hold_presses_down_waits_and_releases_with_an_uncancellable_token()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        var text = await rig.Tools().Key("a", hold_ms: 250, ct: cts.Token);

        text.Should().Be("held a for 250 ms");
        rig.Log.Should().Equal(new[] { "down:a", "delay:250", "up:a" });
        rig.TokenOf("down").Should().Be(cts.Token);
        rig.TokenOf("delay").Should().Be(cts.Token);
        rig.TokenOf("up").CanBeCanceled.Should().BeFalse("the release must not depend on the request");
        rig.Input.Verify(s => s.PressKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "a hold is one long press, not a tap");
    }

    [Fact]
    public async Task Key_hold_with_repeat_one_written_out_is_a_hold()
    {
        var rig = new Rig();

        var text = await rig.Tools().Key("space", repeat: 1, hold_ms: 500);

        text.Should().Be("held space for 500 ms");
        rig.Log.Should().Equal(new[] { "down:space", "delay:500", "up:space" });
    }

    [Fact]
    public async Task Key_hold_cancelled_during_the_wait_still_releases_the_key()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.DelayImpl = (_, ct) =>
        {
            cts.Cancel();
            return Task.FromCanceled(ct);
        };

        var act = () => rig.Tools().Key("a", hold_ms: 5000, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Log.Should().Equal(new[] { "down:a", "delay:5000", "up:a" });
        // The real KeyUpAsync throws on a cancelled token: handed the request's, 'a' stays down.
        rig.TokenOf("up").CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task Key_hold_whose_wait_throws_releases_the_key_and_rethrows_the_same_exception()
    {
        var rig = new Rig();
        var boom = new InvalidOperationException("timer failed");
        rig.DelayImpl = (_, _) => Task.FromException(boom);

        var act = () => rig.Tools().Key("a", hold_ms: 100);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        rig.Log.Should().Equal(new[] { "down:a", "delay:100", "up:a" });
    }

    [Fact]
    public async Task Key_hold_whose_key_down_throws_still_attempts_the_release_and_rethrows()
    {
        // A key-down that half-landed must not stay down.
        var rig = new Rig();
        var boom = new InvalidOperationException("SendInput refused the key-down");
        rig.FailDown = boom;

        var act = () => rig.Tools().Key("a", hold_ms: 100);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        rig.Log.Should().Equal(new[] { "down:a", "up:a" });
        rig.TokenOf("up").CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task Key_hold_failure_outranks_a_release_failure()
    {
        // Extrapolated from click's C6 rule: the body's failure is the one the caller sees.
        var rig = new Rig();
        var boom = new InvalidOperationException("timer failed");
        rig.DelayImpl = (_, _) => Task.FromException(boom);
        rig.FailUp = new IOException("key would not come up");

        var act = () => rig.Tools().Key("a", hold_ms: 100);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        rig.Log.Should().Equal(new[] { "down:a", "delay:100", "up:a", "up:a" }, "R4: the failed release is retried once");
    }

    // ---- R4: a failed release is retried once, then reported -----------------------------------

    [Fact]
    public async Task Key_hold_retries_a_failed_release_once_and_a_successful_retry_is_no_error()
    {
        var rig = new Rig { FailUp = new IOException("bounced"), FailUpOnlyOnce = true };

        var text = await rig.Tools().Key("f5", hold_ms: 100);

        text.Should().Be("held f5 for 100 ms");
        rig.Log.Should().Equal(new[] { "down:f5", "delay:100", "up:f5", "up:f5" });
        rig.Tokens.Where(t => t.Call == "up").Should().OnlyContain(t => !t.Ct.CanBeCanceled);
    }

    [Fact]
    public async Task Key_hold_whose_release_fails_its_retry_reports_the_key_as_possibly_still_down()
    {
        var rig = new Rig();
        var stuck = new IOException("f5 would not come up");
        rig.FailUp = stuck;

        var act = () => rig.Tools().Key("f5", hold_ms: 100);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().ContainEquivalentOf("held", "the hold itself happened");
        ex.Message.Should().Contain("f5").And.ContainEquivalentOf("down");
        ex.Message.Should().Contain(stuck.Message);
        ex.InnerException.Should().BeSameAs(stuck);
        rig.UpAttempts.Should().Be(2);
        // GREEN 2: the done text, then exactly the stuck key.
        ex.Message.Should().StartWith("'f5' was held for 100 ms");
        ex.Message.Should().Contain("releasing f5 failed twice");
    }

    // ---- K10: a key that needs an implied modifier cannot be held ------------------------------

    /// <summary>
    /// Characters the ACTIVE layout produces only with a modifier (on US: + ! @ # ? : ...). Read
    /// through the same <see cref="ShortcutParser.ResolveKey"/> the tool uses, so the test holds
    /// on any layout instead of assuming US.
    /// </summary>
    private static string[] ShiftedCharactersOnThisLayout()
    {
        string[] candidates = ["+", "!", "@", "#", "?", ":", "\"", "_", "~", "(", ")", "*", "&", "%", "<", ">", "{", "}", "|"];
        var shifted = candidates.Where(c =>
        {
            try { return ShortcutParser.ResolveKey(c).ImpliedModifiers.Length > 0; }
            catch (ArgumentException) { return false; }
        }).ToArray();
        shifted.Should().NotBeEmpty("every keyboard layout needs a modifier for at least one of these");
        return shifted;
    }

    [Fact]
    public async Task Key_hold_refuses_every_character_that_needs_an_implied_modifier()
    {
        foreach (var key in ShiftedCharactersOnThisLayout())
        {
            var rig = new Rig();

            var act = () => rig.Tools().Key(key, hold_ms: 200);

            var ex = (await act.Should().ThrowAsync<ArgumentException>($"KeyDown cannot hold the Shift '{key}' needs")).Which;
            ex.Message.Should().ContainEquivalentOf("hold");
            ex.ParamName.Should().Be("key");
            NoInputAtAll(rig);
        }
    }

    [PlusNeedsShiftFact]
    public async Task Key_hold_refuses_a_named_character_that_needs_an_implied_modifier()
    {
        // R6/T2: "plus" resolves to '+' through the ACTIVE layout - the tool calls
        // ShortcutParser.ResolveKey without its vkKeyScan seam and exposes no seam of its own - so
        // on a layout where '+' needs no Shift (German) this is a visible skip, not a silent pass.
        var rig = new Rig();

        var act = () => rig.Tools().Key("plus", hold_ms: 200);

        await act.Should().ThrowAsync<ArgumentException>();
        NoInputAtAll(rig);
    }

    [Theory]
    [InlineData("A")]   // the parser maps 'A' to VK_A with NO implied Shift, so it is holdable
    [InlineData("a")]
    [InlineData("space")]   // "shift" was here until R2 refused holding a modifier
    [InlineData("f5")]
    public async Task Key_hold_accepts_a_key_with_no_implied_modifier(string key)
    {
        var rig = new Rig();

        await rig.Tools().Key(key, hold_ms: 50);

        rig.Log.Should().Equal(new[] { $"down:{key}", "delay:50", $"up:{key}" });
    }

    // ---- the seam's default -------------------------------------------------------------------

    [Fact]
    public async Task The_delay_seam_defaults_to_a_real_cancellable_delay()
    {
        var tools = InputVerb.Tools();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => tools.Delay(TimeSpan.FromSeconds(30), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("the default honours the request's token");
    }

    [Fact]
    public async Task The_delay_seam_default_actually_waits()
    {
        // R6/T1: the cancellation test above would also pass for an instant default that merely
        // checks the token; this one fails if the default does not wait out the span.
        var tools = InputVerb.Tools();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var pending = tools.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);

        pending.IsCompleted.Should().BeFalse("a 100 ms hold must not end the moment it starts");
        await pending;
        stopwatch.Stop();
        stopwatch.Elapsed.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(100 - 16,
            "100 ms was asked for (allowing one system timer tick of early return)");
    }

    // ---- the advertised surface ---------------------------------------------------------------

    private static MethodInfo KeyMethod => typeof(InputTools).GetMethod(nameof(InputTools.Key))!;

    [Fact]
    public void Key_adds_repeat_and_hold_ms_with_defaults_one_and_zero_and_a_trailing_token()
    {
        var parameters = KeyMethod.GetParameters();

        parameters.Select(p => p.Name).Take(3).Should().Equal(new[] { "key", "repeat", "hold_ms" });
        parameters[0].HasDefaultValue.Should().BeFalse("key stays required");
        parameters[1].ParameterType.Should().Be(typeof(int));
        parameters[1].DefaultValue.Should().Be(1);
        parameters[2].ParameterType.Should().Be(typeof(int));
        parameters[2].DefaultValue.Should().Be(0);
        parameters[^1].ParameterType.Should().Be(typeof(CancellationToken));
        parameters[^1].HasDefaultValue.Should().BeTrue();
    }

    [Fact]
    public void Key_describes_repeat_hold_their_bounds_their_exclusivity_and_that_a_hold_does_not_auto_repeat()
    {
        var method = KeyMethod.GetCustomAttribute<DescriptionAttribute>()!.Description;
        var repeat = KeyMethod.GetParameters().Single(p => p.Name == "repeat").GetCustomAttribute<DescriptionAttribute>();
        var hold = KeyMethod.GetParameters().Single(p => p.Name == "hold_ms").GetCustomAttribute<DescriptionAttribute>();

        repeat.Should().NotBeNull();
        hold.Should().NotBeNull();
        repeat!.Description.Should().Contain("1").And.Contain("100");
        hold!.Description.Should().Contain("10000");
        method.Should().Contain("repeat").And.Contain("hold_ms");

        var all = string.Join(" ", method, repeat.Description, hold.Description).ToLowerInvariant();
        all.Should().ContainAny(new[] { "not both", "one or the other", "exclusive", "either" },
            "the model is told it cannot combine them before it tries");
        all.Should().Contain("auto-repeat", "a held key is held, not auto-repeated - the model must not expect a stream of characters");
    }

    private static string AllKeyDescriptions() =>
        string.Join(" ", new[] { KeyMethod.GetCustomAttribute<DescriptionAttribute>()!.Description }
            .Concat(KeyMethod.GetParameters().Select(p => p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "")));

    [Fact]
    public void Key_describes_that_letters_are_not_case_sensitive()
    {
        // R5, pinned loosely: key "A" is the a key; a capital needs shortcut shift+a.
        AllKeyDescriptions().Should().MatchRegex(@"(?i)case[- ]?(in)?sensitive|\bcase\b",
            "the model must not expect key A to type a capital");
    }

    [Fact]
    public void Key_describes_that_hold_and_repeat_take_a_non_modifier_key_and_report_partial_repeats()
    {
        // R7, pinned loosely.
        var text = AllKeyDescriptions();

        text.Should().MatchRegex(@"(?i)non-modifier|not (a )?modifier|modifier keys?\b[^.]*\b(refused|cannot|can't|not allowed|rejected)",
            "the model is told hold_ms and repeat refuse ctrl/shift/alt/win before it tries");
        text.Should().MatchRegex(@"(?i)how many|landed",
            "the model is told a failed repeat reports how many presses landed");
    }

    [Fact]
    public void Key_schema_advertises_repeat_and_hold_ms_keeps_key_required_and_hides_the_token()
    {
        var schema = InputToolsClickModifiersTests.Schema(KeyMethod);
        var properties = schema.GetProperty("properties");

        properties.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo(new[] { "key", "repeat", "hold_ms" }, "the CancellationToken is bound by the SDK");
        properties.GetProperty("repeat").GetProperty("type").GetString().Should().Be("integer");
        properties.GetProperty("hold_ms").GetProperty("type").GetString().Should().Be("integer");
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal(new[] { "key" });
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that reports the test <b>Skipped</b>, with the reason, when the
/// active keyboard layout produces '+' with no modifier (German, for one), so there is no implied
/// Shift for a hold of <c>plus</c> to refuse. xunit 2.9 has no dynamic skip, so the probe runs in
/// the constructor at discovery time (the <see cref="Fixtures.JunctionFactAttribute"/> pattern).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class PlusNeedsShiftFactAttribute : FactAttribute
{
    public PlusNeedsShiftFactAttribute()
    {
        if (ShortcutParser.ResolveKey("plus").ImpliedModifiers.Length == 0)
            Skip = "On the active keyboard layout '+' needs no modifier, so a hold of 'plus' has no implied Shift to refuse.";
    }
}
