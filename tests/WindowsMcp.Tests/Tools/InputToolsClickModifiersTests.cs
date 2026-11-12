using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// E-3: <c>click(modifiers: "ctrl+shift")</c> moves the pointer to the target, holds the named
/// keys around ONE click and releases them in a <c>finally</c>, as <c>multi_select</c> does for
/// Ctrl. Everything is in the tool layer over the existing <see cref="IInputService"/> primitives,
/// so a recording mock sees the exact interleaving: the pointer move, the order of key-downs, the
/// click, the order of key-ups, and the token each call was given. The rules that matter on a live
/// desktop are the ones pinned hardest: a key is never left down (release on failure, on
/// cancellation, after a failed press, a failed release is retried and then reported by name),
/// and a refusal - including a point the pointer cannot reach - happens before any key goes down.
/// </summary>
[Trait("Category", "Unit")]
public class InputToolsClickModifiersTests
{
    /// <summary>
    /// One log for pointer moves, key-downs, clicks and key-ups, plus the token each call
    /// received. Failures are thrown SYNCHRONOUSLY, the way the real <c>InputService</c> throws
    /// from <c>ct.ThrowIfCancellationRequested()</c> and <c>MoveCursor</c> in its non-async methods.
    /// </summary>
    private sealed class Rig
    {
        public List<string> Log { get; } = [];
        public List<(string Call, CancellationToken Ct)> Tokens { get; } = [];
        public Mock<IInputService> Input { get; } = new();
        public Dictionary<string, Exception> FailDown { get; } = new(StringComparer.Ordinal);

        /// <summary>Every release of this key throws (the retry too).</summary>
        public Dictionary<string, Exception> FailUp { get; } = new(StringComparer.Ordinal);

        /// <summary>Only the FIRST release attempt of this key throws; the retry succeeds.</summary>
        public Dictionary<string, Exception> FailUpOnce { get; } = new(StringComparer.Ordinal);

        /// <summary>Release attempts of this key throw these, one per attempt, in order.</summary>
        public Dictionary<string, Queue<Exception>> FailUpInTurn { get; } = new(StringComparer.Ordinal);

        public Exception? FailClick { get; set; }
        public Exception? FailHover { get; set; }
        public Action? DuringClick { get; set; }

        public Rig()
        {
            Input.Setup(s => s.KeyDownAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string k, CancellationToken ct) =>
                {
                    Log.Add($"down:{k}"); Tokens.Add(($"down:{k}", ct));
                    if (FailDown.TryGetValue(k, out var e)) throw e;
                    return Task.CompletedTask;
                });
            Input.Setup(s => s.KeyUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string k, CancellationToken ct) =>
                {
                    Log.Add($"up:{k}"); Tokens.Add(($"up:{k}", ct));
                    if (FailUp.TryGetValue(k, out var e)) throw e;
                    if (FailUpOnce.Remove(k, out var once)) throw once;
                    if (FailUpInTurn.TryGetValue(k, out var queue) && queue.Count > 0) throw queue.Dequeue();
                    return Task.CompletedTask;
                });
            Input.Setup(s => s.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int x, int y, MouseButton b, int c, CancellationToken ct) =>
                {
                    Log.Add($"click:{x},{y},{b},{c}"); Tokens.Add(("click", ct));
                    DuringClick?.Invoke();
                    if (FailClick is not null) throw FailClick;
                    return Task.FromResult(new ClickResult(x, y, b, c));
                });
            Input.Setup(s => s.HoverAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int x, int y, int d, CancellationToken ct) =>
                {
                    Log.Add($"hover:{x},{y},{d}"); Tokens.Add(("hover", ct));
                    if (FailHover is not null) throw FailHover;
                    return Task.CompletedTask;
                });
        }

        public InputTools Tools(Mock<IUIAutomationService>? uia = null) => InputVerb.Tools(Input, uia);

        public IEnumerable<CancellationToken> ReleaseTokens =>
            Tokens.Where(t => t.Call.StartsWith("up:", StringComparison.Ordinal)).Select(t => t.Ct);

        public int UpAttempts(string key) => Log.Count(e => e == $"up:{key}");

        public bool Clicked => Log.Any(e => e.StartsWith("click:", StringComparison.Ordinal));
    }

    private static void NoInputAtAll(Rig rig)
    {
        rig.Log.Should().BeEmpty("a refusal must leave the keyboard and the pointer untouched");
        rig.Input.Verify(s => s.KeyDownAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Input.Verify(s => s.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Input.Verify(s => s.HoverAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static string Hover(int x, int y) => $"hover:{x},{y},0";

    // ---- C1: the vocabulary, and "no modifiers" is today's click exactly ---------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Click_without_modifiers_is_one_click_and_touches_no_key(string? modifiers)
    {
        // R1: the pointer move is only for a click WITH modifiers; a plain click is unchanged.
        var rig = new Rig();

        await rig.Tools().Click(x: 10, y: 20, modifiers: modifiers);

        rig.Log.Should().Equal(new[] { "click:10,20,Left,1" });
        rig.Input.Verify(s => s.ClickAsync(10, 20, MouseButton.Left, 1, It.IsAny<CancellationToken>()), Times.Once);
        rig.Input.Verify(s => s.KeyDownAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Input.Verify(s => s.KeyUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("ctrl", new[] { "ctrl" })]
    [InlineData("control", new[] { "ctrl" })]
    [InlineData("shift", new[] { "shift" })]
    [InlineData("alt", new[] { "alt" })]
    [InlineData("CTRL", new[] { "ctrl" })]
    [InlineData("Control", new[] { "ctrl" })]
    [InlineData("ShIfT", new[] { "shift" })]
    [InlineData(" ctrl + shift ", new[] { "ctrl", "shift" })]
    [InlineData("ctrl+control", new[] { "ctrl" })]
    [InlineData("shift+ctrl+shift", new[] { "ctrl", "shift" })]
    [InlineData("alt+ALT+alt", new[] { "alt" })]
    public async Task Click_accepts_ctrl_shift_alt_in_any_case_and_spacing_and_collapses_duplicates(string modifiers, string[] held)
    {
        var rig = new Rig();

        await rig.Tools().Click(x: 1, y: 2, modifiers: modifiers);

        var expected = new[] { Hover(1, 2) }
            .Concat(held.Select(k => $"down:{k}"))
            .Append("click:1,2,Left,1")
            .Concat(Enumerable.Reverse(held).Select(k => $"up:{k}"));
        rig.Log.Should().Equal(expected);
    }

    // ---- C2: everything else is refused, before the target and before any input --------------

    [Theory]
    [InlineData("win", "win")]
    [InlineData("cmd", "cmd")]
    [InlineData("ctrl+win", "win")]
    [InlineData("lctrl", "lctrl")]
    [InlineData("a", "a")]
    [InlineData("ctrl+c", "c")]
    [InlineData("shift+meta", "meta")]
    [InlineData("ctrl+", null)]
    [InlineData("+ctrl", null)]
    [InlineData("ctrl++shift", null)]
    [InlineData("+", null)]
    public async Task Click_refuses_an_unknown_modifier_naming_it_and_the_three_allowed(string modifiers, string? badPart)
    {
        var rig = new Rig();

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: modifiers);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("modifiers");
        ex.Message.Should().Contain("ctrl").And.Contain("shift").And.Contain("alt");
        if (badPart is not null)
            ex.Message.Should().Contain($"'{badPart}'", "the refusal names the part it could not use");
        NoInputAtAll(rig);
    }

    [Theory]
    [InlineData("ctrl+ WIN ", "WIN")]
    [InlineData("Shift+Meta", "Meta")]
    [InlineData("  Cmd", "Cmd")]
    public async Task Click_quotes_the_bad_modifier_as_written_trimmed_and_in_its_own_case(string modifiers, string quoted)
    {
        // GREEN decision 10: the caller recognises what they wrote, not a normalised form of it.
        var act = () => new Rig().Tools().Click(x: 1, y: 2, modifiers: modifiers);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain($"'{quoted}'");
    }

    [Theory]
    [InlineData("ctrl+")]
    [InlineData("+shift")]
    [InlineData("ctrl++alt")]
    [InlineData("ctrl+ +alt")]
    public async Task Click_reports_an_empty_modifier_part_as_empty(string modifiers)
    {
        var act = () => new Rig().Tools().Click(x: 1, y: 2, modifiers: modifiers);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("modifiers");
        ex.Message.Should().Contain("empty", "a stray '+' is a different mistake from an unknown key name");
    }

    [Fact]
    public async Task Click_refuses_a_bad_modifier_before_it_resolves_the_element()
    {
        var rig = new Rig();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_12", It.IsAny<CancellationToken>())).ReturnsAsync(InputVerb.Element());

        var act = () => rig.Tools(uia).Click(element_id: "el_12", modifiers: "win");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("modifiers");
        uia.Verify(s => s.GetElementAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "an unusable request costs no UIA round-trip");
        NoInputAtAll(rig);
    }

    // ---- R1: the pointer goes first, so an unreachable point is refused with no key down -------

    [Fact]
    public async Task Click_with_modifiers_moves_the_pointer_to_the_target_before_any_key_goes_down()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        await rig.Tools().Click(x: 30, y: 40, modifiers: "ctrl+shift", ct: cts.Token);

        rig.Log.Should().Equal(new[] { Hover(30, 40), "down:ctrl", "down:shift", "click:30,40,Left,1", "up:shift", "up:ctrl" });
        rig.Input.Verify(s => s.HoverAsync(30, 40, 0, cts.Token), Times.Once);
    }

    public static TheoryData<Exception> PointRefusals() => new()
    {
        new ArgumentOutOfRangeException("x", "(-50000, 10) is not on any monitor"),
        new InvalidOperationException("SetCursorPos failed"),
    };

    [Theory]
    [MemberData(nameof(PointRefusals))]
    public async Task Click_with_modifiers_at_a_point_the_pointer_cannot_reach_presses_and_releases_nothing(Exception refusal)
    {
        // Without the move first, the keys would go down, the click would be refused, and the
        // releases would land as a bare Alt / Shift tap (Alt alone opens the menu bar).
        var rig = new Rig { FailHover = refusal };

        var act = () => rig.Tools().Click(x: -50000, y: 10, modifiers: "shift+alt");

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(refusal);
        rig.Log.Should().Equal(new[] { Hover(-50000, 10) });
        rig.Input.Verify(s => s.KeyDownAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Input.Verify(s => s.KeyUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Clicked.Should().BeFalse();
    }

    // ---- C3: canonical order down, one click, reverse order up --------------------------------

    [Theory]
    [InlineData("ctrl+shift+alt")]
    [InlineData("alt+shift+ctrl")]
    [InlineData("shift+alt+ctrl")]
    [InlineData("alt+ctrl+shift")]
    public async Task Click_presses_ctrl_shift_alt_in_that_order_and_releases_in_reverse(string modifiers)
    {
        var rig = new Rig();

        await rig.Tools().Click(x: 7, y: 8, modifiers: modifiers);

        rig.Log.Should().Equal(new[]
        {
            Hover(7, 8),
            "down:ctrl", "down:shift", "down:alt",
            "click:7,8,Left,1",
            "up:alt", "up:shift", "up:ctrl",
        });
    }

    [Fact]
    public async Task Click_with_modifiers_passes_the_request_token_to_the_click()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        await rig.Tools().Click(x: 1, y: 2, modifiers: "shift", ct: cts.Token);

        rig.Input.Verify(s => s.ClickAsync(1, 2, MouseButton.Left, 1, cts.Token), Times.Once);
        rig.Input.Verify(s => s.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Click_releases_with_a_token_that_cannot_be_cancelled_on_the_happy_path_too()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        await rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+alt", ct: cts.Token);

        rig.ReleaseTokens.Should().HaveCount(2)
            .And.OnlyContain(t => !t.CanBeCanceled, "a release is never conditional on the request");
    }

    // ---- C4: release in a finally - on failure and on cancellation ---------------------------

    [Fact]
    public async Task Click_releases_every_modifier_when_the_click_throws_and_rethrows_the_same_exception()
    {
        var rig = new Rig();
        var boom = new InvalidOperationException("click failed");
        rig.FailClick = boom;

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+shift+alt");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        rig.Log.Should().Equal(new[]
        {
            Hover(1, 2),
            "down:ctrl", "down:shift", "down:alt",
            "click:1,2,Left,1",
            "up:alt", "up:shift", "up:ctrl",
        });
        rig.ReleaseTokens.Should().OnlyContain(t => !t.CanBeCanceled);
    }

    [Fact]
    public async Task Click_cancelled_during_the_click_still_releases_with_an_uncancelled_token()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.DuringClick = cts.Cancel;
        rig.FailClick = new OperationCanceledException(cts.Token);

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+shift", ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Log.Should().Equal(new[] { Hover(1, 2), "down:ctrl", "down:shift", "click:1,2,Left,1", "up:shift", "up:ctrl" });
        // The real InputService.KeyUpAsync calls ct.ThrowIfCancellationRequested(): handed the
        // request's (now cancelled) token, the release would itself throw and leave Shift down.
        rig.ReleaseTokens.Should().HaveCount(2).And.OnlyContain(t => !t.CanBeCanceled && !t.IsCancellationRequested);
    }

    // ---- C5: a press that fails releases what is already down; the click is never sent -------

    [Fact]
    public async Task Click_when_the_second_key_down_throws_releases_the_first_and_never_clicks()
    {
        var rig = new Rig();
        var boom = new InvalidOperationException("SendInput refused shift");
        rig.FailDown["shift"] = boom;

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+shift+alt");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        // GREEN decision 1: the key whose key-down threw may have half-landed, so it is released
        // too - first, because release runs in reverse - and then ctrl. alt was never pressed and
        // the click is never sent with a partial chord.
        rig.Log.Should().Equal(new[] { Hover(1, 2), "down:ctrl", "down:shift", "up:shift", "up:ctrl" });
        rig.ReleaseTokens.Should().HaveCount(2).And.OnlyContain(t => !t.CanBeCanceled);
    }

    [Fact]
    public async Task Click_when_the_first_key_down_throws_sends_no_click()
    {
        var rig = new Rig();
        var boom = new InvalidOperationException("SendInput refused ctrl");
        rig.FailDown["ctrl"] = boom;

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+shift");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        rig.Log.Should().Equal(new[] { Hover(1, 2), "down:ctrl", "up:ctrl" });
    }

    [Fact]
    public async Task Click_key_down_failure_outranks_a_release_failure()
    {
        // The press failure is the body's failure; a release that then also fails (and fails its
        // retry) must not mask it.
        var rig = new Rig();
        var boom = new InvalidOperationException("SendInput refused shift");
        rig.FailDown["shift"] = boom;
        rig.FailUp["shift"] = new IOException("shift would not come up");

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+shift");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        rig.UpAttempts("shift").Should().Be(2, "R4: a failed release is retried once");
        rig.UpAttempts("ctrl").Should().Be(1);
        rig.Clicked.Should().BeFalse();
    }

    [Fact]
    public async Task Click_key_downs_receive_the_request_token()
    {
        // GREEN decision 2: a press honours the request; only releases are uncancellable.
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        await rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+alt", ct: cts.Token);

        rig.Tokens.Where(t => t.Call.StartsWith("down:", StringComparison.Ordinal)).Select(t => t.Ct)
            .Should().HaveCount(2).And.OnlyContain(t => t == cts.Token);
    }

    [Fact]
    public async Task Click_cancelled_between_two_key_downs_releases_both_and_never_clicks()
    {
        // The real KeyDownAsync throws OCE synchronously on a cancelled token: the key it was
        // asked to press is released anyway (decision 1), then the one already down.
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.FailDown["shift"] = new OperationCanceledException(cts.Token);
        rig.Input.Setup(s => s.KeyDownAsync("ctrl", It.IsAny<CancellationToken>()))
            .Returns((string k, CancellationToken ct) =>
            {
                rig.Log.Add($"down:{k}"); rig.Tokens.Add(($"down:{k}", ct));
                cts.Cancel();
                return Task.CompletedTask;
            });

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+shift", ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Log.Should().Equal(new[] { Hover(1, 2), "down:ctrl", "down:shift", "up:shift", "up:ctrl" });
        rig.ReleaseTokens.Should().OnlyContain(t => !t.CanBeCanceled);
    }

    // ---- C6 as re-pinned by R4: retry once, then report what may still be down ----------------

    [Fact]
    public async Task Click_retries_a_failed_release_once_and_a_successful_retry_is_no_error()
    {
        var rig = new Rig();
        rig.FailUpOnce["alt"] = new IOException("alt bounced");

        var json = InputVerb.Json(await rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+shift+alt"));

        InputVerb.Str(json, "action").Should().Be("click");
        rig.UpAttempts("alt").Should().Be(2);
        rig.UpAttempts("shift").Should().Be(1);
        rig.UpAttempts("ctrl").Should().Be(1);
        rig.Log.Where(e => e.StartsWith("up:", StringComparison.Ordinal)).First().Should().Be("up:alt", "release still runs in reverse");
        rig.Log[^1].Should().Be("up:ctrl");
        rig.ReleaseTokens.Should().OnlyContain(t => !t.CanBeCanceled, "the retry is uncancellable too");
    }

    [Fact]
    public async Task Click_release_that_fails_its_retry_after_a_sent_click_is_reported_naming_the_key()
    {
        var rig = new Rig();
        var stuck = new IOException("alt would not come up");
        rig.FailUp["alt"] = stuck;

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+shift+alt");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>(
            "the click landed; the caller must learn that, and that a key may still be down")).Which;
        ex.Message.Should().ContainEquivalentOf("click").And.ContainEquivalentOf("sent");
        ex.Message.Should().Contain("alt").And.ContainEquivalentOf("down");
        ex.Message.Should().Contain(stuck.Message);
        ex.InnerException.Should().BeSameAs(stuck);
        rig.UpAttempts("alt").Should().Be(2);
        rig.UpAttempts("shift").Should().Be(1, "every release is still attempted");
        rig.UpAttempts("ctrl").Should().Be(1);
        rig.Log.Count(e => e.StartsWith("click:", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Click_names_every_key_whose_release_failed_and_wraps_the_first_failure()
    {
        var rig = new Rig();
        var first = new IOException("alt stuck");
        rig.FailUp["alt"] = first;
        rig.FailUp["ctrl"] = new IOException("ctrl stuck");

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+alt");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("alt").And.Contain("ctrl");
        ex.Message.Should().Contain(first.Message, "the first failure (alt is released first) is the one quoted");
        ex.InnerException.Should().BeSameAs(first);
        rig.UpAttempts("alt").Should().Be(2);
        rig.UpAttempts("ctrl").Should().Be(2);
    }

    // ---- R4 settled in GREEN 2: immediate retry, first attempt wrapped, exact stuck list ----------

    [Fact]
    public async Task Click_retries_a_failed_release_immediately_before_moving_to_the_next_key()
    {
        var rig = new Rig();
        rig.FailUp["alt"] = new IOException("alt stuck");

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+alt");

        await act.Should().ThrowAsync<InvalidOperationException>();
        rig.Log.Should().Equal(new[] { Hover(1, 2), "down:ctrl", "down:alt", "click:1,2,Left,1", "up:alt", "up:alt", "up:ctrl" });
    }

    [Fact]
    public async Task Click_retry_that_succeeds_sits_right_after_the_failed_attempt()
    {
        var rig = new Rig();
        rig.FailUpOnce["shift"] = new IOException("shift bounced");

        await rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+shift+alt");

        rig.Log.Should().Equal(new[]
        {
            Hover(1, 2), "down:ctrl", "down:shift", "down:alt", "click:1,2,Left,1",
            "up:alt", "up:shift", "up:shift", "up:ctrl",
        });
    }

    [Fact]
    public async Task Click_wraps_the_first_release_attempts_exception_not_the_retrys()
    {
        var rig = new Rig();
        var firstAttempt = new IOException("first attempt: access denied");
        var retry = new IOException("retry: still denied");
        rig.FailUpInTurn["alt"] = new Queue<Exception>([firstAttempt, retry]);

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "alt");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.InnerException.Should().BeSameAs(firstAttempt);
        ex.Message.Should().Contain(firstAttempt.Message);
        rig.UpAttempts("alt").Should().Be(2);
    }

    [Fact]
    public async Task Click_release_failure_message_says_the_click_was_sent_and_lists_only_the_stuck_key()
    {
        var rig = new Rig();
        rig.FailUp["shift"] = new IOException("shift stuck");

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+shift+alt");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().StartWith("The click was sent", "the caller must not repeat a click that landed");
        ex.Message.Should().Contain("releasing shift failed twice",
            "only the key that stayed stuck is listed; alt and ctrl were released");
    }

    [Fact]
    public async Task Click_lists_every_stuck_key_in_release_order_and_leaves_out_a_key_its_retry_released()
    {
        var rig = new Rig();
        rig.FailUp["alt"] = new IOException("alt stuck");
        rig.FailUpOnce["shift"] = new IOException("shift bounced");     // released by its retry
        rig.FailUp["ctrl"] = new IOException("ctrl stuck");

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+shift+alt");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("releasing alt, ctrl failed twice");
        ex.Message.Should().NotContain("shift bounced", "a release that succeeded on retry is not a failure to report");
        ex.InnerException!.Message.Should().Be("alt stuck", "alt is released first, so it is the first stuck key");
    }

    [Fact]
    public async Task Click_failure_outranks_a_release_failure_and_every_release_is_still_attempted()
    {
        var rig = new Rig();
        var clickFailure = new InvalidOperationException("click failed");
        rig.FailClick = clickFailure;
        rig.FailUp["shift"] = new IOException("shift stuck");

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+shift+alt");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(clickFailure,
            "a release failure in the finally must not mask why the click failed");
        rig.UpAttempts("alt").Should().Be(1);
        rig.UpAttempts("shift").Should().Be(2, "the retry still happens when the body failed");
        rig.UpAttempts("ctrl").Should().Be(1);
    }

    // ---- C7: target refusals come before any key goes down ------------------------------------

    [Fact]
    public async Task Click_with_modifiers_on_an_offscreen_element_is_refused_with_no_key_down()
    {
        var rig = new Rig();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element(id: "el_5", offscreen: true));

        var act = () => rig.Tools(uia).Click(element_id: "el_5", modifiers: "shift");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("el_5");
        NoInputAtAll(rig);
        rig.Input.Verify(s => s.KeyUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Click_with_modifiers_on_an_unknown_element_is_refused_with_no_key_down()
    {
        var rig = new Rig();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_404", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Unknown element id 'el_404'"));

        var act = () => rig.Tools(uia).Click(element_id: "el_404", modifiers: "ctrl");

        await act.Should().ThrowAsync<KeyNotFoundException>();
        NoInputAtAll(rig);
    }

    [Fact]
    public async Task Click_with_modifiers_and_both_coordinates_and_an_element_id_is_refused_with_no_key_down()
    {
        var rig = new Rig();

        var act = () => rig.Tools().Click(x: 1, y: 2, element_id: "el_12", modifiers: "ctrl");

        await act.Should().ThrowAsync<ArgumentException>();
        NoInputAtAll(rig);
    }

    [Fact]
    public async Task Click_with_modifiers_by_element_id_holds_the_keys_around_a_click_on_its_centre()
    {
        var rig = new Rig();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_12", It.IsAny<CancellationToken>())).ReturnsAsync(InputVerb.Element());

        var json = InputVerb.Json(await rig.Tools(uia).Click(element_id: "el_12", modifiers: "shift"));

        rig.Log.Should().Equal(new[] { Hover(120, 210), "down:shift", "click:120,210,Left,1", "up:shift" });
        InputVerb.Str(json, "elementId").Should().Be("el_12");
        InputVerb.Str(json, "modifiers").Should().Be("shift");
    }

    // ---- C8: a hover cannot carry modifiers ---------------------------------------------------

    [Theory]
    [InlineData("ctrl")]
    [InlineData("shift+alt")]
    public async Task Click_with_zero_clicks_and_modifiers_is_refused_by_name(string modifiers)
    {
        var rig = new Rig();

        var act = () => rig.Tools().Click(x: 1, y: 2, clicks: 0, modifiers: modifiers);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("modifiers");
        NoInputAtAll(rig);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Click_with_zero_clicks_and_no_modifiers_is_still_a_hover(string? modifiers)
    {
        var rig = new Rig();

        var json = InputVerb.Json(await rig.Tools().Click(x: 3, y: 4, clicks: 0, modifiers: modifiers));

        rig.Log.Should().Equal(new[] { "hover:3,4,0" });
        InputVerb.Str(json, "action").Should().Be("hover");
        // GREEN decision 4: a hover holds nothing, so its result carries no modifiers field at all.
        json.TryGetProperty("modifiers", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Click_without_modifiers_forwards_the_request_token_to_the_click()
    {
        // GREEN decision 3.
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        await rig.Tools().Click(x: 1, y: 2, ct: cts.Token);

        rig.Input.Verify(s => s.ClickAsync(1, 2, MouseButton.Left, 1, cts.Token), Times.Once);
    }

    // ---- C9: every button and click count, still exactly one click ----------------------------

    [Theory]
    [InlineData("right", 1, MouseButton.Right)]
    [InlineData("middle", 1, MouseButton.Middle)]
    [InlineData("left", 2, MouseButton.Left)]
    [InlineData("left", 3, MouseButton.Left)]
    [InlineData("right", 2, MouseButton.Right)]
    public async Task Click_with_modifiers_forwards_button_and_count_in_a_single_click(string button, int clicks, MouseButton expected)
    {
        var rig = new Rig();

        var json = InputVerb.Json(await rig.Tools().Click(x: 5, y: 6, button: button, clicks: clicks, modifiers: "ctrl"));

        rig.Log.Should().Equal(new[] { Hover(5, 6), "down:ctrl", $"click:5,6,{expected},{clicks}", "up:ctrl" });
        rig.Input.Verify(s => s.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Str(json, "button").Should().Be(expected.ToString().ToLowerInvariant());
        InputVerb.Num(json, "clicks").Should().Be(clicks);
    }

    // ---- C10: the result reports the canonical modifiers ---------------------------------------

    [Theory]
    [InlineData("ctrl", "ctrl")]
    [InlineData("SHIFT + Control", "ctrl+shift")]
    [InlineData("alt+ctrl", "ctrl+alt")]
    [InlineData("alt+shift+control+alt", "ctrl+shift+alt")]
    public async Task Click_reports_the_canonical_modifier_string(string modifiers, string canonical)
    {
        var rig = new Rig();

        var json = InputVerb.Json(await rig.Tools().Click(x: 100, y: 200, button: "right", clicks: 2, modifiers: modifiers));

        InputVerb.Str(json, "modifiers").Should().Be(canonical);
        InputVerb.Str(json, "action").Should().Be("click");
        InputVerb.Num(json, "x").Should().Be(100);
        InputVerb.Num(json, "y").Should().Be(200);
        InputVerb.Str(json, "button").Should().Be("right");
        InputVerb.Num(json, "clicks").Should().Be(2);
        InputVerb.Absent(json, "elementId").Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Click_without_modifiers_reports_modifiers_as_null(string? modifiers)
    {
        var rig = new Rig();

        var json = InputVerb.Json(await rig.Tools().Click(x: 1, y: 2, modifiers: modifiers));

        json.TryGetProperty("modifiers", out var value).Should().BeTrue("the field is part of the click result now");
        value.ValueKind.Should().Be(JsonValueKind.Null);
        InputVerb.Str(json, "action").Should().Be("click");
    }

    // ---- C11: an already-cancelled request sends nothing --------------------------------------

    [Fact]
    public async Task Click_with_modifiers_on_an_already_cancelled_request_presses_nothing()
    {
        // With R1 the pointer move comes first, so the cancellation check must come before it:
        // "nothing is sent" includes the move.
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => rig.Tools().Click(x: 1, y: 2, modifiers: "ctrl+shift", ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        NoInputAtAll(rig);
        rig.Input.Verify(s => s.KeyUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- the advertised surface ---------------------------------------------------------------

    private static MethodInfo ClickMethod => typeof(InputTools).GetMethod(nameof(InputTools.Click))!;

    [Fact]
    public void Click_adds_modifiers_after_the_existing_parameters_and_a_trailing_token()
    {
        var parameters = ClickMethod.GetParameters();

        parameters.Select(p => p.Name).Take(6).Should().Equal(new[] { "x", "y", "element_id", "button", "clicks", "modifiers" },
            "positional callers of click(x, y, element_id, button, clicks) keep working");
        parameters[^1].ParameterType.Should().Be(typeof(CancellationToken));
        parameters[^1].HasDefaultValue.Should().BeTrue();

        var modifiers = parameters.Single(p => p.Name == "modifiers");
        modifiers.ParameterType.Should().Be(typeof(string));
        modifiers.HasDefaultValue.Should().BeTrue();
        modifiers.DefaultValue.Should().BeNull();
        new NullabilityInfoContext().Create(modifiers).WriteState.Should().Be(NullabilityState.Nullable);
    }

    [Fact]
    public void Click_describes_the_modifiers_it_takes_and_that_they_are_always_released()
    {
        var description = ClickMethod.GetCustomAttribute<DescriptionAttribute>()!.Description;
        var parameter = ClickMethod.GetParameters().Single(p => p.Name == "modifiers")
            .GetCustomAttribute<DescriptionAttribute>();

        parameter.Should().NotBeNull("the parameter's own description is what the schema shows beside it");
        parameter!.Description.Should().Contain("ctrl").And.Contain("shift").And.Contain("alt");
        description.Should().Contain("modifiers")
            .And.Contain("ctrl").And.Contain("shift").And.Contain("alt")
            .And.ContainEquivalentOf("release", "the model is told a failed click does not leave a key down");
    }

    [Fact]
    public void Click_describes_the_pointer_move_before_the_keys_and_the_reported_release_failure()
    {
        // R7, pinned loosely: one sentence ties the pointer move to "before" the keys, and the text
        // says a release that fails is reported (not merely that keys are released when the CLICK fails).
        var text = ClickMethod.GetCustomAttribute<DescriptionAttribute>()!.Description + " "
            + ClickMethod.GetParameters().Single(p => p.Name == "modifiers").GetCustomAttribute<DescriptionAttribute>()?.Description;

        text.Should().MatchRegex(@"(?i)\b(pointer|cursor|mouse)\b[^.]*\bbefore\b[^.]*\bkeys?\b",
            "the model should know the pointer moves to the target before any key goes down");
        text.Should().MatchRegex(@"(?i)\b(failed|failing)\s+release|\brelease\s+(fails|failed|failure)",
            "a release that fails is reported with the key named, and the model should expect that answer");
    }

    [Fact]
    public void Click_schema_advertises_modifiers_and_never_the_cancellation_token()
    {
        var properties = Schema(ClickMethod).GetProperty("properties");

        properties.TryGetProperty("modifiers", out _).Should().BeTrue();
        properties.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo(new[] { "x", "y", "element_id", "button", "clicks", "modifiers" },
                "the CancellationToken is bound by the SDK, not supplied by the client");
    }

    /// <summary>The tool's input schema as the SDK builds it (metadata only, never invoked).</summary>
    internal static JsonElement Schema(MethodInfo method) =>
        McpServerTool.Create(method, (RequestContext<CallToolRequestParams> _) =>
                throw new InvalidOperationException("metadata only - the tool is never invoked"))
            .ProtocolTool.InputSchema;
}
