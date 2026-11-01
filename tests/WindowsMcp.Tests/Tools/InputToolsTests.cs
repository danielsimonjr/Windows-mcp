using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Server;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class InputToolsTests
{
    // The click tests that lived here moved to InputToolsClickTests when B-4 gave `click` an
    // element_id, optional coordinates and the clicks:0 hover. Everything below is B-5's `wait`.

    // ---- B-5: wait -------------------------------------------------------------------------
    // The tool exists so an agent stops paying a PowerShell cold start (seconds to tens of
    // seconds under Defender, CLAUDE.md) for `Start-Sleep 2`. Everything below is about it being
    // a plain Task.Delay with a bounded, honestly-reported argument.

    private static InputTools NewTools(Mock<IInputService>? input = null, Mock<IClipboardService>? clipboard = null)
        => new((input ?? new Mock<IInputService>()).Object,
               (clipboard ?? new Mock<IClipboardService>()).Object,
               new Mock<IUIAutomationService>().Object);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(0.05)]
    [InlineData(1)]
    [InlineData(1.5)]
    // 60 is the ceiling and is accepted, but waiting it out costs the headless suite a whole
    // minute: Wait_honours_a_cancelled_token asks for 60 and cancels it, which is the same
    // proof that the boundary passes validation - an out-of-range value throws before the delay.
    public async Task Wait_accepts_the_open_interval_up_to_sixty_seconds_and_echoes_what_it_waited(double seconds)
    {
        var json = await NewTools().Wait(seconds);

        Parse(json).GetProperty("waited").GetDouble().Should().Be(seconds,
            "the response echoes the seconds as given, so the agent can log what it actually paid");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.001)]
    [InlineData(60.001)]
    [InlineData(3600)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task Wait_rejects_anything_outside_the_range_and_points_at_wait_for(double seconds)
    {
        var act = () => NewTools().Wait(seconds);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("seconds", "the offending parameter is named")
            .And.Contain("60", "the model is told where the ceiling is")
            .And.Contain("wait_for", "a longer wait is a condition, not a sleep - say so instead of just refusing");
    }

    [Theory]
    [InlineData(0.5, "{\"waited\":0.5}")]
    [InlineData(1, "{\"waited\":1}")]
    public async Task Wait_returns_exactly_the_documented_json(double seconds, string expected)
    {
        // The description promises {"waited": seconds}: one key, that name, the number as given.
        // A fractional wait must not be rounded to a whole one, and a whole one must not gain a
        // ".0" or become a quoted string. (The 60 s ceiling is accepted, not waited out, by
        // Wait_honours_a_cancelled_token below - an out-of-range 60 would throw before the delay.)
        var json = await NewTools().Wait(seconds);

        json.Should().Be(expected);
    }

    [Fact]
    public async Task Wait_actually_waits_and_does_not_overshoot()
    {
        // A real 50 ms delay: the one test that fails if Wait returns immediately (or sleeps a
        // whole second because the seconds were read as milliseconds, or vice versa).
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await NewTools().Wait(0.05);

        stopwatch.Stop();
        stopwatch.Elapsed.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(50 - 16,
            "50 ms was asked for (allowing one system timer tick of early return)");
        stopwatch.Elapsed.TotalMilliseconds.Should().BeLessThan(50 + 250,
            "a delay, not a poll loop and not a PowerShell cold start");
    }

    [Fact]
    public async Task Wait_honours_a_cancelled_token()
    {
        using var cts = new CancellationTokenSource();
        var waiting = NewTools().Wait(60, cts.Token);

        await cts.CancelAsync();

        var act = async () => await waiting;
        await act.Should().ThrowAsync<OperationCanceledException>(
            "a cancelled request must not hold the server for the rest of a minute");
    }

    [Fact]
    public async Task Wait_is_already_cancelled_before_it_starts()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => NewTools().Wait(60, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Wait_touches_no_collaborator_at_all()
    {
        // The parity point: waiting is a delay, not a call into anything. If this ever starts
        // going through a service it will be paying for a process again.
        var input = new Mock<IInputService>(MockBehavior.Strict);
        var clipboard = new Mock<IClipboardService>(MockBehavior.Strict);

        await NewTools(input, clipboard).Wait(0.001);

        input.VerifyNoOtherCalls();
        clipboard.VerifyNoOtherCalls();
    }

    [Fact]
    public void Wait_is_annotated_read_only_and_idempotent()
    {
        // C-7 will annotate the other 65; wait is the first, because a plain sleep is the clearest
        // possible read-only idempotent tool and the SDK carries the hints to the client.
        var attribute = typeof(InputTools).GetMethod(nameof(InputTools.Wait))!
            .GetCustomAttribute<McpServerToolAttribute>();

        attribute.Should().NotBeNull("wait is an MCP tool");
        attribute!.ReadOnly.Should().BeTrue("waiting changes nothing on the machine");
        attribute.Idempotent.Should().BeTrue("waiting twice is waiting");
    }

    [Fact]
    public void Wait_describes_its_range_and_what_to_use_instead_for_longer_waits()
    {
        var description = typeof(InputTools).GetMethod(nameof(InputTools.Wait))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("60", "the ceiling belongs in the description, not only in the refusal")
            .And.Contain("wait_for", "the model needs to know what to reach for beyond the ceiling")
            .And.NotContain("not implemented");
    }

    [Fact]
    public void Wait_takes_a_cancellation_token_so_the_transport_can_cut_it_short()
    {
        var parameters = typeof(InputTools).GetMethod(nameof(InputTools.Wait))!.GetParameters();

        parameters[0].Name.Should().Be("seconds");
        parameters[0].ParameterType.Should().Be(typeof(double));
        parameters.Should().Contain(p => p.ParameterType == typeof(CancellationToken),
            "the SDK passes the request's token, and a 60 s sleep must be interruptible");
    }

    // ---- the thin verbs: hover, key, shortcut, clipboard -------------------------------------
    // No element_id and no options, so they have no file of their own - but they are the other
    // half of the same tool class, and each one is a forwarding contract that can silently rot
    // (a lower-cased key, a swapped x/y, a "set" that returns before it sets).

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    public async Task Hover_forwards_the_point_and_the_duration_it_was_given(int durationMs)
    {
        var input = new Mock<IInputService>();

        var text = await NewTools(input).Hover(x: 12, y: -34, duration_ms: durationMs);

        input.Verify(s => s.HoverAsync(12, -34, durationMs, It.IsAny<CancellationToken>()), Times.Once);
        input.VerifyNoOtherCalls();
        text.Should().Contain("12").And.Contain("-34").And.Contain(durationMs.ToString());
    }

    [Theory]
    [InlineData("enter")]
    [InlineData("F5")]
    [InlineData("/")]
    public async Task Key_forwards_the_key_string_exactly_as_written(string key)
    {
        // Case and punctuation belong to ShortcutParser, not to the tool: lower-casing here would
        // silently change which key a caller asked for on a layout where case matters.
        var input = new Mock<IInputService>();

        var text = await NewTools(input).Key(key);

        input.Verify(s => s.PressKeyAsync(key, It.IsAny<CancellationToken>()), Times.Once);
        input.VerifyNoOtherCalls();
        text.Should().Contain(key);
    }

    [Theory]
    [InlineData("ctrl+c")]
    [InlineData("CTRL+SHIFT+S")]
    [InlineData("win")]
    public async Task Shortcut_forwards_the_chord_exactly_as_written(string chord)
    {
        var input = new Mock<IInputService>();

        var text = await NewTools(input).Shortcut(chord);

        input.Verify(s => s.PressShortcutAsync(chord, It.IsAny<CancellationToken>()), Times.Once);
        input.VerifyNoOtherCalls();
        text.Should().Contain(chord);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("GET")]
    public async Task Clipboard_get_returns_what_the_clipboard_holds(string action)
    {
        var clipboard = new Mock<IClipboardService>();
        clipboard.Setup(c => c.GetTextAsync(It.IsAny<CancellationToken>())).ReturnsAsync("hello");

        var text = await NewTools(clipboard: clipboard).Clipboard(action);

        text.Should().Be("hello");
        clipboard.Verify(c => c.GetTextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Clipboard_get_of_a_clipboard_holding_no_text_is_an_empty_string()
    {
        // Null would serialise as "null" over MCP and read as the literal word to a model.
        var clipboard = new Mock<IClipboardService>();
        clipboard.Setup(c => c.GetTextAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        (await NewTools(clipboard: clipboard).Clipboard("get")).Should().BeEmpty();
    }

    [Fact]
    public async Task Clipboard_set_stores_the_text_and_reports_its_length()
    {
        var clipboard = new Mock<IClipboardService>();

        var text = await NewTools(clipboard: clipboard).Clipboard("set", "abcd");

        clipboard.Verify(c => c.SetTextAsync("abcd", It.IsAny<CancellationToken>()), Times.Once);
        text.Should().Contain("4");
    }

    [Fact]
    public async Task Clipboard_set_without_text_is_refused_and_sets_nothing()
    {
        var clipboard = new Mock<IClipboardService>();

        var act = () => NewTools(clipboard: clipboard).Clipboard("set");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("text");
        clipboard.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("paste")]
    [InlineData("")]
    public async Task Clipboard_rejects_an_unknown_action_and_lists_the_two_it_takes(string action)
    {
        var clipboard = new Mock<IClipboardService>();

        var act = () => NewTools(clipboard: clipboard).Clipboard(action);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("get").And.Contain("set");
        clipboard.VerifyNoOtherCalls();
    }
}
