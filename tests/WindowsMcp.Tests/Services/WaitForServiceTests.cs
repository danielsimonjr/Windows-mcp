using System.Diagnostics;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-6 (R29-R44): the SERVICE half of the conditional wait — the poll loop, what one poll is
/// allowed to touch, and the argument rules that fire before any of it. The loop is driven
/// through the <c>WaitLoopAsync</c> seam with a fake gatherer (the same trick D-5's
/// <c>PollAsync</c> tests use), and the one condition that needs no UIA at all —
/// <c>active_window</c> — is driven end to end through the real service with STRICT mocks, which
/// is what proves it never walks the tree.
/// </summary>
[Trait("Category", "Unit")]
public class WaitForServiceTests
{
    private static readonly ElementInfo Hit =
        new("el_1", "Save", "Button", true, false, new Bounds(0, 0, 10, 10), null, null, null);

    private static WaitRequest Request(
        WaitCondition condition = WaitCondition.ElementExists, string? text = "Save",
        int timeoutMs = 1000, int intervalMs = 10, FindScope scope = FindScope.Foreground,
        string? windowTitle = null, bool useDom = false)
        => new(condition, text, timeoutMs, intervalMs, FindKind.Any, scope, windowTitle, false, useDom);

    // ---- the loop -----------------------------------------------------------------------------

    [Fact]
    public async Task Wait_loop_stops_at_the_poll_that_satisfies_the_condition()
    {
        var polls = 0;

        var result = await UIAutomationService.WaitLoopAsync(Request(), _ =>
        {
            polls++;
            return Task.FromResult(new WaitEvidence(Matches: polls < 3 ? [] : [Hit]));
        }, CancellationToken.None);

        result.Satisfied.Should().BeTrue();
        result.Attempts.Should().Be(3, "attempts counts polls, and the third one is what found it");
        polls.Should().Be(3, "a satisfied wait stops polling immediately");
        result.Detail.Should().Be("found 'Save' (el_1)", "the detail is the evaluator's, unedited");
        result.Element.Should().BeSameAs(Hit);
        result.Condition.Should().Be("element_exists");
    }

    [Fact]
    public async Task Wait_loop_polls_once_even_with_a_zero_timeout()
    {
        var polls = 0;

        var result = await UIAutomationService.WaitLoopAsync(Request(timeoutMs: 0), _ =>
        {
            polls++;
            return Task.FromResult(new WaitEvidence(Matches: [Hit]));
        }, CancellationToken.None);

        polls.Should().Be(1, "timeout_ms:0 means 'check now', not 'do nothing' (D-5's rule, kept)");
        result.Satisfied.Should().BeTrue();
        result.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Wait_loop_reports_a_timeout_as_a_result_carrying_the_last_detail()
    {
        // Roadmap C4, the one contract break of section B: a timeout is an outcome the agent acts
        // on, not an exception, and not the string "null".
        var result = await UIAutomationService.WaitLoopAsync(
            Request(timeoutMs: 200, intervalMs: 10),
            _ => Task.FromResult(new WaitEvidence(Matches: [])),
            CancellationToken.None);

        result.Satisfied.Should().BeFalse();
        result.Detail.Should().Be("no element matching 'Save'", "the last poll's verdict is the diagnosis");
        result.Element.Should().BeNull();
        result.Attempts.Should().BeGreaterThanOrEqualTo(2, "200 ms at 10 ms intervals polls many times");
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(150, "the wait actually waited");
    }

    [Fact]
    public async Task Wait_loop_keeps_polling_after_a_poll_throws()
    {
        // D-5's headline, inherited: absorbing a transient provider failure is what a wait is for.
        var polls = 0;

        var result = await UIAutomationService.WaitLoopAsync(Request(timeoutMs: 5000, intervalMs: 1), _ =>
        {
            polls++;
            if (polls < 3) throw new System.Runtime.InteropServices.COMException("stale", unchecked((int)0x80040201));
            return Task.FromResult(new WaitEvidence(Matches: [Hit]));
        }, CancellationToken.None);

        result.Satisfied.Should().BeTrue();
        result.Attempts.Should().Be(3, "a failed poll is still an attempt");
    }

    [Fact]
    public async Task Wait_loop_says_so_when_every_poll_failed_instead_of_reporting_not_found()
    {
        // D-5 answered this with a TimeoutException; C4 says a wait never throws, so the
        // distinction moves into the detail. Losing it entirely would be the D-5 defect again:
        // "we never managed to look" reported as "we looked and it was not there".
        var result = await UIAutomationService.WaitLoopAsync(
            Request(timeoutMs: 120, intervalMs: 1),
            _ => throw new InvalidOperationException("provider exploded"),
            CancellationToken.None);

        result.Satisfied.Should().BeFalse();
        result.Detail.Should().StartWith("every poll failed:").And.Contain("provider exploded");
        result.Attempts.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Wait_loop_prefers_a_clean_polls_verdict_over_an_earlier_failure()
    {
        // One transient failure followed by clean polls is NOT "every poll failed".
        var polls = 0;

        var result = await UIAutomationService.WaitLoopAsync(Request(timeoutMs: 150, intervalMs: 1), _ =>
        {
            polls++;
            if (polls == 1) throw new InvalidOperationException("provider exploded");
            return Task.FromResult(new WaitEvidence(Matches: []));
        }, CancellationToken.None);

        result.Satisfied.Should().BeFalse();
        result.Detail.Should().Be("no element matching 'Save'");
        result.Detail.Should().NotContain("every poll failed");
    }

    [Fact]
    public async Task Wait_loop_clamps_the_interval_to_the_remaining_budget()
    {
        // interval 5 s inside a 150 ms budget must not sleep past the deadline: a wait that
        // overshoots its timeout by a whole interval is a wait the agent cannot schedule around.
        var stopwatch = Stopwatch.StartNew();

        var result = await UIAutomationService.WaitLoopAsync(
            Request(timeoutMs: 150, intervalMs: 5000),
            _ => Task.FromResult(new WaitEvidence(Matches: [])),
            CancellationToken.None);

        stopwatch.Stop();
        result.Satisfied.Should().BeFalse();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        result.ElapsedMs.Should().BeLessThan(3000);
    }

    [Fact]
    public async Task Wait_loop_does_not_sleep_before_a_first_poll_that_hits()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await UIAutomationService.WaitLoopAsync(
            Request(timeoutMs: 10000, intervalMs: 5000),
            _ => Task.FromResult(new WaitEvidence(Matches: [Hit])),
            CancellationToken.None);

        stopwatch.Stop();
        result.Satisfied.Should().BeTrue();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "poll first, sleep afterwards");
    }

    [Fact]
    public async Task Wait_loop_propagates_cancellation_rather_than_reporting_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => UIAutomationService.WaitLoopAsync(
            Request(timeoutMs: 5000, intervalMs: 1),
            _ => Task.FromResult(new WaitEvidence(Matches: [])),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Wait_loop_keeps_the_last_clean_verdict_when_the_FINAL_poll_throws()
    {
        // The mirror of the row above: clean polls first, a failure last. "every poll failed" is
        // reserved for "we never managed to look", so a late transient must not overwrite the
        // diagnosis the clean polls produced - and it still counts as an attempt.
        var polls = 0;

        var result = await UIAutomationService.WaitLoopAsync(Request(timeoutMs: 120, intervalMs: 10), _ =>
        {
            polls++;
            if (polls == 1) return Task.FromResult(new WaitEvidence(Matches: []));
            throw new InvalidOperationException("provider exploded");
        }, CancellationToken.None);

        result.Satisfied.Should().BeFalse();
        result.Detail.Should().Be("no element matching 'Save'");
        result.Detail.Should().NotContain("every poll failed");
        result.Attempts.Should().Be(polls, "a poll that threw is still an attempt");
        result.Attempts.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Wait_loop_lets_a_cancelled_poll_out_instead_of_retrying_it()
    {
        // The gatherer's own OperationCanceledException (a UIA call cancelled mid-flight) must
        // leave the loop. Swallowing it as "a poll that failed" would spin the whole timeout out
        // on a request the caller already abandoned.
        var polls = 0;

        Func<Task> act = () => UIAutomationService.WaitLoopAsync(Request(timeoutMs: 5000, intervalMs: 10), _ =>
        {
            polls++;
            throw new OperationCanceledException("the poll was cancelled");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        polls.Should().Be(1, "the loop stops at the cancellation, it does not poll again");
    }

    [Fact]
    public async Task Wait_loop_floors_a_zero_interval_at_ten_milliseconds()
    {
        // interval_ms:0 is legal (the range starts at 0) and means "as fast as sensible", not "spin
        // the CPU": the floor is what keeps a 2-minute wait from taking a core with it.
        var result = await UIAutomationService.WaitLoopAsync(
            Request(timeoutMs: 150, intervalMs: 0),
            _ => Task.FromResult(new WaitEvidence(Matches: [])),
            CancellationToken.None);

        result.Satisfied.Should().BeFalse();
        result.Attempts.Should().BeGreaterThanOrEqualTo(2, "150 ms at a 10 ms floor is many polls");
        result.Attempts.Should().BeLessThan(75,
            "a 150 ms budget cannot hold more than ~15 polls at the floor; hundreds would mean no floor at all");
    }

    [Fact]
    public async Task Wait_loop_reports_an_elapsed_time_it_actually_measured()
    {
        var result = await UIAutomationService.WaitLoopAsync(
            Request(timeoutMs: 10000, intervalMs: 500),
            _ => Task.FromResult(new WaitEvidence(Matches: [Hit])),
            CancellationToken.None);

        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(0, "a negative or unset clock reads as a bug to the model");
        result.ElapsedMs.Should().BeLessThan(2000, "an immediate hit did not wait an interval");
        result.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Wait_loop_treats_a_missing_text_as_the_empty_string_rather_than_throwing()
    {
        // WaitForAsync refuses a blank text before it gets here, so this is the loop's own
        // defence: the seam is internal and D-2/D-5 call into it, and an NRE inside the loop would
        // surface as "every poll failed" on a request that never even asked for anything.
        var result = await UIAutomationService.WaitLoopAsync(
            Request(text: null, timeoutMs: 0),
            _ => Task.FromResult(new WaitEvidence(Matches: [])),
            CancellationToken.None);

        result.Satisfied.Should().BeFalse();
        result.Detail.Should().Be("no element matching ''");
    }

    [Theory]
    [InlineData(WaitCondition.ElementExists, "element_exists")]
    [InlineData(WaitCondition.ElementEnabled, "element_enabled")]
    [InlineData(WaitCondition.FocusedElement, "focused_element")]
    [InlineData(WaitCondition.TextExists, "text_exists")]
    [InlineData(WaitCondition.ActiveWindow, "active_window")]
    public async Task Wait_loop_reports_the_canonical_condition_name(WaitCondition condition, string expected)
    {
        // The result is what the model reads back; the alias it happened to pass in must not be
        // what it sees, or "window" and "active_window" become two conditions in its head.
        var result = await UIAutomationService.WaitLoopAsync(
            Request(condition, "Save", timeoutMs: 0),
            _ => Task.FromResult(new WaitEvidence()),
            CancellationToken.None);

        result.Condition.Should().Be(expected);
    }

    // ---- what one poll gathers ----------------------------------------------------------------

    [Theory]
    [InlineData(FindScope.Foreground, SnapshotScope.Foreground)]
    [InlineData(FindScope.Desktop, SnapshotScope.Desktop)]
    public void The_snapshot_a_poll_takes_maps_the_find_scope_onto_the_snapshot_scope(
        FindScope find, SnapshotScope snapshot)
    {
        var request = UIAutomationService.SnapshotRequestFor(Request(WaitCondition.TextExists, "hello", scope: find));

        request.Scope.Should().Be(snapshot);
        request.WindowTitle.Should().BeNull();
        request.IncludeTree.Should().BeFalse("a wait never needs the tree; it is the expensive half");
        request.MaxElements.Should().Be(0, "0 = the server's --max-tree-elements budget");
        request.UseDom.Should().BeFalse();
    }

    [Fact]
    public void The_snapshot_a_poll_takes_carries_the_window_title_and_the_dom_flag()
    {
        var request = UIAutomationService.SnapshotRequestFor(
            Request(WaitCondition.TextExists, "hello", scope: FindScope.Window, windowTitle: "Notepad", useDom: true));

        request.Scope.Should().Be(SnapshotScope.Window);
        request.WindowTitle.Should().Be("Notepad", "scope=window is re-resolved every poll (D-5)");
        request.UseDom.Should().BeTrue("use_dom is what makes text_exists see the page's words (A-5)");
    }

    [Fact]
    public void The_snapshot_a_poll_takes_keeps_a_null_window_title_for_the_window_scope()
    {
        // scope=window with no title is the tool's business to refuse (ParseTarget); the mapper
        // must carry the null through rather than inventing "" - a snapshot of the window titled
        // "" is a different (and always empty) request.
        var request = UIAutomationService.SnapshotRequestFor(
            Request(WaitCondition.TextExists, "hello", scope: FindScope.Window, windowTitle: null));

        request.Scope.Should().Be(SnapshotScope.Window);
        request.WindowTitle.Should().BeNull();
        request.IncludeTree.Should().BeFalse();
    }

    // ---- active_window end to end: no walk, no snapshot, no input -----------------------------

    private static Mock<IWindowService> StrictInventory(params WindowInfo[] windows)
    {
        var mock = new Mock<IWindowService>(MockBehavior.Strict);
        mock.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(windows);
        return mock;
    }

    private static WindowInfo Window(string title, bool active) =>
        new(title, 1, 42, "notepad", WindowState.Normal, new Bounds(0, 0, 800, 600), 0, active, false, 0);

    [Fact]
    public async Task Active_window_reads_the_inventory_and_nothing_else()
    {
        // The point of the condition (roadmap: "needs no walk"): a UI tree walk costs hundreds of
        // milliseconds and can fail; asking "what is in front" costs one EnumWindows. Both mocks
        // are STRICT, so a snapshot (cursor, monitors, active window) or any input call fails here.
        var windows = StrictInventory(Window("Untitled - Notepad", active: true));
        var input = new Mock<IInputService>(MockBehavior.Strict);
        using var svc = new UIAutomationService(input.Object, windows.Object);

        var result = await svc.WaitForAsync(new WaitRequest(WaitCondition.ActiveWindow, "notepad", TimeoutMs: 0));

        result.Satisfied.Should().BeTrue();
        result.Condition.Should().Be("active_window");
        result.Attempts.Should().Be(1);
        result.Detail.Should().Be("active window is 'Untitled - Notepad' (substring)");
        result.Element.Should().BeNull();
        windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Active_window_that_never_comes_forward_times_out_into_a_result()
    {
        var windows = StrictInventory(Window("Calculator", active: true));
        using var svc = new UIAutomationService(new Mock<IInputService>(MockBehavior.Strict).Object, windows.Object);

        var result = await svc.WaitForAsync(
            new WaitRequest(WaitCondition.ActiveWindow, "Notepad", TimeoutMs: 250, IntervalMs: 20));

        result.Satisfied.Should().BeFalse();
        result.Detail.Should().Be("active window is 'Calculator', wanted 'Notepad'");
        result.Attempts.Should().BeGreaterThanOrEqualTo(2, "the inventory is re-read every interval");
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(200);
        windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2), "a wait that reads the inventory once is not a wait");
    }

    // ---- E-1 (review R-F4): text and focus waits take a snapshot, which replaces the ids --------

    /// <summary>
    /// The server instructions say snapshot ids die at "`wait_for` on text or focus". Each such
    /// poll goes through the service's own <c>SnapshotAsync</c>, which evicts the previous
    /// snapshot's ids; its header reads the cursor and the monitor inventory exactly once, and no
    /// other wait path reads either, so those two reads are the observable trace of the snapshot.
    /// Desktop scope over an empty inventory walks no window, so no live desktop is needed.
    /// (A direct proof - an id from an earlier snapshot no longer resolving - needs a real walked
    /// window; see the design request in the E-1 report.)
    /// </summary>
    [Theory]
    [InlineData(WaitCondition.TextExists)]
    [InlineData(WaitCondition.FocusedElement)]
    public async Task Text_and_focus_waits_take_a_snapshot_on_every_poll(WaitCondition condition)
    {
        var input = new Mock<IInputService>();
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(10, 10));
        var windows = new Mock<IWindowService>();
        windows.Setup(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WindowInfo>());
        windows.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        using var svc = new UIAutomationService(input.Object, windows.Object);

        var result = await svc.WaitForAsync(new WaitRequest(
            condition, "wmcp-e1-" + Guid.NewGuid().ToString("N"), TimeoutMs: 0, Scope: FindScope.Desktop));

        result.Satisfied.Should().BeFalse();
        result.Attempts.Should().Be(1, "timeout 0 is one poll");
        result.Detail.Should().NotStartWith("every poll failed", "the snapshot was taken and judged");
        input.Verify(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()), Times.Once,
            "one poll is one snapshot, and a snapshot's header reads the cursor");
        windows.Verify(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()), Times.Once,
            "and the monitor inventory");
    }

    // ---- argument rules, decided before any poll ----------------------------------------------

    private static UIAutomationService NewService() =>
        new(new Mock<IInputService>(MockBehavior.Strict).Object, new Mock<IWindowService>(MockBehavior.Strict).Object);

    [Theory]
    [InlineData(-1)]
    [InlineData(120001)]
    public async Task WaitForAsync_refuses_a_timeout_outside_the_upstream_range(int timeoutMs)
    {
        using var svc = NewService();

        Func<Task> act = () => svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, "Save", TimeoutMs: timeoutMs));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("120000");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    public async Task WaitForAsync_refuses_an_interval_outside_the_upstream_range(int intervalMs)
    {
        using var svc = NewService();

        Func<Task> act = () => svc.WaitForAsync(
            new WaitRequest(WaitCondition.ElementExists, "Save", TimeoutMs: 1000, IntervalMs: intervalMs));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("5000");
    }

    [Fact]
    public async Task WaitForAsync_range_refusals_name_the_budget_the_range_and_the_value_seen()
    {
        // The caller has to be able to fix the call from the message alone: which budget, what it
        // may be, and what it actually sent.
        using var svc = NewService();

        Func<Task> timeout = () => svc.WaitForAsync(new WaitRequest(WaitCondition.ElementExists, "Save", TimeoutMs: 120001));
        Func<Task> interval = () => svc.WaitForAsync(
            new WaitRequest(WaitCondition.ElementExists, "Save", TimeoutMs: 1000, IntervalMs: 5001));

        (await timeout.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("timeoutMs").And.Contain("0 and 120000").And.Contain("120001");
        (await interval.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("intervalMs").And.Contain("0 and 5000").And.Contain("5001");
    }

    [Theory]
    [InlineData(WaitCondition.ElementExists, "element_exists")]
    [InlineData(WaitCondition.ElementEnabled, "element_enabled")]
    [InlineData(WaitCondition.FocusedElement, "focused_element")]
    [InlineData(WaitCondition.TextExists, "text_exists")]
    [InlineData(WaitCondition.ActiveWindow, "active_window")]
    public async Task WaitForAsync_refuses_a_blank_text_naming_the_condition_that_needed_it(
        WaitCondition condition, string name)
    {
        // All five conditions need the text: it is the element name, the on-screen string or the
        // window title. Waiting on "" would match everything on poll one and report success.
        using var svc = NewService();

        foreach (var blank in new string?[] { null, "", "   " })
        {
            Func<Task> act = () => svc.WaitForAsync(new WaitRequest(condition, blank, TimeoutMs: 0));
            (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain(name);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(120000, 5000)]
    public async Task WaitForAsync_accepts_both_ends_of_both_ranges(int timeoutMs, int intervalMs)
    {
        // The boundaries are inclusive; an off-by-one here silently costs the caller the longest
        // wait upstream allows.
        var windows = StrictInventory(Window("Untitled - Notepad", active: true));
        using var svc = new UIAutomationService(new Mock<IInputService>(MockBehavior.Strict).Object, windows.Object);

        var result = await svc.WaitForAsync(
            new WaitRequest(WaitCondition.ActiveWindow, "Notepad", timeoutMs, intervalMs));

        result.Satisfied.Should().BeTrue("the first poll matches, so neither boundary is waited out");
    }

    [Fact]
    public async Task WaitForAsync_propagates_a_cancelled_token()
    {
        var windows = StrictInventory(Window("Calculator", active: true));
        using var svc = new UIAutomationService(new Mock<IInputService>(MockBehavior.Strict).Object, windows.Object);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => svc.WaitForAsync(
            new WaitRequest(WaitCondition.ActiveWindow, "Notepad", TimeoutMs: 5000), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

/// <summary>
/// B-6 through the REAL find path (CLAUDE.md's "a mocked collaborator is not evidence"): the two
/// waits below run against live UIA, no mock in the way. Read-only — they wait for a window title
/// that cannot exist, so nothing on this desktop is touched and no input is injected.
/// </summary>
[Trait("Category", "Integration")]
public class WaitForFindPathIntegrationTests
{
    private static UIAutomationService NewService() => new(new InputService(), new WindowService());

    private static string MissingWindowTitle() => "wmcp-b6-no-such-window-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Element_exists_scoped_to_a_window_that_is_not_open_reports_that_every_poll_failed()
    {
        // The real find path throws KeyNotFoundException for an unknown window on every poll, so
        // this is the "never managed to look" case end to end - through UIA, not through a fake.
        using var svc = NewService();

        var result = await svc.WaitForAsync(new WaitRequest(
            WaitCondition.ElementExists, "Save", TimeoutMs: 400, IntervalMs: 50,
            Scope: FindScope.Window, WindowTitle: MissingWindowTitle()));

        result.Satisfied.Should().BeFalse();
        result.Condition.Should().Be("element_exists");
        result.Detail.Should().StartWith("every poll failed:",
            "a wait that never managed to look must not answer 'not found' (D-5)");
        result.Attempts.Should().BeGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// E-1 (review R-F4): the converse of WaitForServiceTests.Text_and_focus_waits_take_a_snapshot_on_every_poll.
    /// The instructions do not list element_exists or element_enabled as evicting snapshot ids, so
    /// their polls must run the find path and never <c>SnapshotAsync</c> - whose header would read
    /// the cursor and the monitors. Integration only because the find path reads the live
    /// top-level window list (read-only; the window title cannot exist, so nothing is walked).
    /// </summary>
    [Theory]
    [InlineData(WaitCondition.ElementExists)]
    [InlineData(WaitCondition.ElementEnabled)]
    public async Task Element_waits_never_take_a_snapshot(WaitCondition condition)
    {
        var input = new Mock<IInputService>();
        var windows = new Mock<IWindowService>();
        using var svc = new UIAutomationService(input.Object, windows.Object);

        var result = await svc.WaitForAsync(new WaitRequest(
            condition, "Save", TimeoutMs: 0, Scope: FindScope.Window, WindowTitle: MissingWindowTitle()));

        // Both paths can say "No top-level window matching": the find path enumerates the
        // desktop's children through UIA itself, the snapshot path asks IWindowService.ListAsync
        // for the inventory. So the message shows a poll ran, and the Times.Never on ListAsync
        // below is what shows it was the find path - the two assertions are not in conflict.
        result.Detail.Should().Contain("No top-level window matching", "a poll ran and looked for the window");
        input.Verify(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()), Times.Never,
            "no snapshot header was read, so no snapshot was taken and the caller's ids survive");
        windows.Verify(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()), Times.Never);
        windows.Verify(w => w.ListAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Text_exists_really_takes_a_snapshot_and_answers_about_it()
    {
        // CLAUDE.md's rule: the mocked tests above prove the loop, not that the snapshot gatherer
        // is wired up. A string that cannot be on any screen must come back as the evaluator's
        // "not found anywhere on screen" - NOT as "every poll failed", which is what a snapshot
        // call that throws (or was never made) would produce.
        using var svc = NewService();
        var absent = "wmcp-b6-not-on-screen-" + Guid.NewGuid().ToString("N");

        var result = await svc.WaitForAsync(new WaitRequest(
            WaitCondition.TextExists, absent, TimeoutMs: 0, Scope: FindScope.Desktop));

        result.Satisfied.Should().BeFalse();
        result.Condition.Should().Be("text_exists");
        result.Attempts.Should().Be(1, "timeout 0 is one poll");
        result.Detail.Should().Be($"'{absent}' not found anywhere on screen");
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Text_exists_finds_text_that_is_really_on_this_desktop()
    {
        // Non-vacuity for the row above: take a real snapshot, pick a name out of it, and wait for
        // it. A gatherer that returned an empty snapshot would satisfy the "absent" test and fail
        // this one.
        using var svc = NewService();
        var snapshot = await svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Desktop));
        var name = snapshot.Interactive
            .Select(e => e.Name)
            .FirstOrDefault(n => n.Length >= 4 && !n.Any(char.IsDigit));
        if (name is null) return;   // a session with no stable named control: nothing to assert on

        var result = await svc.WaitForAsync(new WaitRequest(
            WaitCondition.TextExists, name, TimeoutMs: 3000, IntervalMs: 250, Scope: FindScope.Desktop));

        result.Satisfied.Should().BeTrue($"'{name}' was on this desktop one snapshot ago");
        result.Detail.Should().Contain("found in");
    }

    [Fact]
    public async Task Focused_element_reads_the_focus_out_of_a_real_snapshot()
    {
        // The other snapshot-backed condition, same wiring. Whatever holds the focus on this
        // desktop, it is not a fresh GUID, so the answer is one of the two "no" diagnoses - and
        // never a poll failure.
        using var svc = NewService();
        var absent = "wmcp-b6-not-focused-" + Guid.NewGuid().ToString("N");

        var result = await svc.WaitForAsync(new WaitRequest(
            WaitCondition.FocusedElement, absent, TimeoutMs: 0, Scope: FindScope.Desktop));

        result.Satisfied.Should().BeFalse();
        result.Condition.Should().Be("focused_element");
        result.Detail.Should().Match(d => d == "nothing has keyboard focus" || d.Contains("has focus, wanted"),
            "a snapshot was taken and judged; 'every poll failed' would mean it was not");
    }

    [Fact]
    public async Task The_pre_B6_overload_still_throws_a_timeout_when_every_poll_failed()
    {
        // D-2 and D-5 still call WaitForAsync(text, ...) and its contract does NOT change with
        // B-6: null when clean polls found nothing, TimeoutException when every poll failed.
        using var svc = NewService();

        Func<Task> act = () => svc.WaitForAsync("Save", timeoutMs: 300, intervalMs: 50,
            FindKind.Any, FindScope.Window, MissingWindowTitle());

        await act.Should().ThrowAsync<TimeoutException>();
    }
}
