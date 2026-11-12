using System.Text.RegularExpressions;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// D-11 R18 / R21: <c>WaitLoopAsync</c>'s <c>release</c> callback. An element wait runs a find every
/// poll and every find mints up to 20 non-snapshot ids; the loop hands back the ids the result
/// will NOT carry so the service can drop them before they push the caller's own ids out of the
/// LRU. Driven through the same seam as <c>WaitForServiceTests</c>: a fake gatherer, a recorder.
/// <c>UIAutomationServiceWaitReleaseIntegrationTests</c> is the live sibling.
/// <para>
/// Round 3 (R21) REPLACES the round-2 contract. Round 2 released every id of the final
/// unsatisfied poll, but a timed-out <c>element_enabled</c> wait names one of them in its Detail
/// ("found 'Save' (el_57) but it is disabled") — so the id in the reply was dead on arrival. Now:
/// an unsatisfied poll's ids are released only when another poll is about to be gathered, or when
/// the wait is cancelled; the FINAL unsatisfied poll of a timed-out wait is kept (at most 20 ids,
/// bounded by the LRU); a satisfied poll still releases every match but the returned one at once;
/// a throwing poll releases nothing new; snapshot and window evidence is never released.
/// </para>
/// <para>
/// Out of scope, by decision: ids minted by a poll still running on the STA thread when the wait
/// is cancelled — that poll's result never returns to the loop, so there is nothing to name; the
/// LRU bounds them.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class WaitLoopReleaseTests
{
    private static ElementInfo El(string id, bool enabled = true) =>
        new(id, "Save " + id, "Button", enabled, false, new Bounds(0, 0, 10, 10), null, null, null);

    private static WaitRequest Request(WaitCondition condition = WaitCondition.ElementExists, int timeoutMs = 1000, int intervalMs = 10)
        => new(condition, "Save", timeoutMs, intervalMs);

    /// <summary>
    /// Every release call, in order, the flat set of ids released, and an event log that interleaves
    /// releases with gathers so "released before the next gather" is observable.
    /// </summary>
    private sealed class Recorder
    {
        public List<string[]> Calls { get; } = [];
        public List<string> Events { get; } = [];
        public IEnumerable<string> All => Calls.SelectMany(c => c);
        public void Release(IReadOnlyList<string> ids)
        {
            Calls.Add(ids.ToArray());
            Events.Add("release " + string.Join(",", ids.OrderBy(i => i, StringComparer.Ordinal)));
        }
    }

    /// <summary>A gatherer that hands out the given polls in order, then repeats the last one.</summary>
    private static Func<CancellationToken, Task<WaitEvidence>> Polls(params ElementInfo[][] polls)
    {
        int i = 0;
        return _ => Task.FromResult(new WaitEvidence(Matches: polls[Math.Min(i++, polls.Length - 1)]));
    }

    /// <summary>The same, logging "gather N" into the recorder on entry.</summary>
    private static Func<CancellationToken, Task<WaitEvidence>> LoggedPolls(Recorder recorder, params Func<WaitEvidence>[] polls)
    {
        int i = 0;
        return _ =>
        {
            var n = i++;
            recorder.Events.Add($"gather {n + 1}");
            return Task.FromResult(polls[Math.Min(n, polls.Length - 1)]());
        };
    }

    private static readonly Regex IdInDetail = new(@"\bel_\d+\b", RegexOptions.CultureInvariant);

    // ---- satisfied polls: everything but the answer, immediately ------------------------------

    [Fact]
    public async Task A_satisfied_element_exists_poll_releases_every_other_match_and_never_the_returned_one()
    {
        var recorder = new Recorder();

        var result = await UIAutomationService.WaitLoopAsync(
            Request(), Polls([El("el_1"), El("el_2"), El("el_3")]), CancellationToken.None, recorder.Release);

        result.Satisfied.Should().BeTrue();
        result.Element!.ElementId.Should().Be("el_1");
        recorder.All.Should().BeEquivalentTo(["el_2", "el_3"], "the result carries el_1 and nothing else");
        recorder.All.Should().NotContain("el_1", "releasing the id the caller is handed would expire it on arrival");
    }

    [Fact]
    public async Task A_satisfied_element_enabled_poll_releases_the_disabled_matches_it_skipped_and_the_rest()
    {
        var recorder = new Recorder();

        var result = await UIAutomationService.WaitLoopAsync(
            Request(WaitCondition.ElementEnabled),
            Polls([El("el_1", enabled: false), El("el_2"), El("el_3")]), CancellationToken.None, recorder.Release);

        result.Satisfied.Should().BeTrue();
        result.Element!.ElementId.Should().Be("el_2", "the first ENABLED match is the answer");
        recorder.All.Should().BeEquivalentTo(["el_1", "el_3"]);
    }

    [Fact]
    public async Task A_single_match_that_satisfies_releases_nothing()
    {
        var recorder = new Recorder();

        await UIAutomationService.WaitLoopAsync(Request(), Polls([El("el_1")]), CancellationToken.None, recorder.Release);

        recorder.All.Should().BeEmpty();
    }

    [Fact]
    public async Task A_satisfied_poll_with_exactly_one_match_after_an_unsatisfied_poll_releases_the_earlier_ids_and_keeps_the_answer()
    {
        // GREEN round 3 check: the earlier poll's ids are pending when the satisfying poll is
        // gathered; they go back BEFORE that gather, and the satisfying poll's lone match is the
        // answer, so the satisfied-poll flush has nothing of its own to release.
        var recorder = new Recorder();

        var result = await UIAutomationService.WaitLoopAsync(
            Request(WaitCondition.ElementEnabled),
            LoggedPolls(recorder,
                () => new WaitEvidence(Matches: [El("el_1", enabled: false), El("el_2", enabled: false)]),
                () => new WaitEvidence(Matches: [El("el_3")])),
            CancellationToken.None, recorder.Release);

        result.Satisfied.Should().BeTrue();
        result.Attempts.Should().Be(2);
        result.Element!.ElementId.Should().Be("el_3");
        recorder.Events.Should().Equal(["gather 1", "release el_1,el_2", "gather 2"],
            "poll 1's ids are released once, before poll 2; the answer is never released and no empty release is made");
        recorder.All.Should().NotContain("el_3");
    }

    // ---- unsatisfied polls: released when the NEXT poll is about to be gathered --------------

    [Fact]
    public async Task Every_unsatisfied_poll_that_is_followed_by_another_poll_releases_all_of_its_ids()
    {
        var recorder = new Recorder();

        var result = await UIAutomationService.WaitLoopAsync(
            Request(WaitCondition.ElementEnabled),
            Polls(
                [El("el_1", enabled: false), El("el_2", enabled: false)],
                [],
                [El("el_3", enabled: false)],
                [El("el_4")]),
            CancellationToken.None, recorder.Release);

        result.Satisfied.Should().BeTrue();
        result.Element!.ElementId.Should().Be("el_4");
        recorder.All.Should().BeEquivalentTo(["el_1", "el_2", "el_3"],
            "found-but-disabled matches are minted ids the result never carries, and each poll had a successor");
        recorder.All.Should().OnlyHaveUniqueItems("an id is released once");
    }

    [Fact]
    public async Task An_unsatisfied_polls_ids_are_released_before_the_next_gather_and_after_their_own()
    {
        var recorder = new Recorder();

        var result = await UIAutomationService.WaitLoopAsync(
            Request(WaitCondition.ElementEnabled),
            LoggedPolls(recorder,
                () => new WaitEvidence(Matches: [El("el_1", enabled: false), El("el_2", enabled: false)]),
                () => new WaitEvidence(Matches: [El("el_3", enabled: false), El("el_4"), El("el_5")])),
            CancellationToken.None, recorder.Release);

        result.Satisfied.Should().BeTrue();
        result.Element!.ElementId.Should().Be("el_4");
        recorder.Events.Should().Equal(
            ["gather 1", "release el_1,el_2", "gather 2", "release el_3,el_5"],
            "poll 1's ids go back before poll 2 is gathered (never later, so they cannot pile up), and the " +
            "satisfied poll releases everything but its answer at once");
    }

    [Fact]
    public async Task A_timed_out_wait_keeps_the_final_polls_ids_and_releases_every_earlier_one()
    {
        var recorder = new Recorder();
        var perPoll = new List<string[]>();
        int n = 0;

        var result = await UIAutomationService.WaitLoopAsync(
            Request(WaitCondition.ElementEnabled, timeoutMs: 150, intervalMs: 10),
            _ =>
            {
                var a = El($"el_{n++}", enabled: false);
                var b = El($"el_{n++}", enabled: false);
                perPoll.Add([a.ElementId, b.ElementId]);
                return Task.FromResult(new WaitEvidence(Matches: [a, b]));
            },
            CancellationToken.None, recorder.Release);

        result.Satisfied.Should().BeFalse();
        result.Attempts.Should().BeGreaterThan(1, "precondition: several polls ran");
        perPoll.Should().HaveCount(result.Attempts);

        var final = perPoll[^1];
        var earlier = perPoll.Take(perPoll.Count - 1).SelectMany(p => p).ToArray();
        recorder.All.Should().BeEquivalentTo(earlier,
            "every poll that had a successor is released, so a long wait still cannot flood the LRU");
        recorder.All.Should().NotContain(final,
            "the final poll's ids are what the timed-out reply describes; releasing them makes the reply a lie");
    }

    [Fact]
    public async Task A_timed_out_element_enabled_wait_never_releases_an_id_its_detail_names()
    {
        // THE R21 headline: "found 'Save el_7' (el_7) but it is disabled" has to name a live id.
        var recorder = new Recorder();
        int n = 0;

        var result = await UIAutomationService.WaitLoopAsync(
            Request(WaitCondition.ElementEnabled, timeoutMs: 120, intervalMs: 10),
            _ => Task.FromResult(new WaitEvidence(Matches: [El($"el_{n++}", enabled: false), El($"el_{n++}", enabled: false)])),
            CancellationToken.None, recorder.Release);

        result.Satisfied.Should().BeFalse();
        var named = IdInDetail.Matches(result.Detail).Select(m => m.Value).ToArray();
        named.Should().NotBeEmpty($"precondition: the element_enabled detail names the disabled match: {result.Detail}");
        recorder.All.Should().NotContain(named, "every id named in Detail must still resolve");
    }

    [Fact]
    public async Task A_single_poll_timeout_releases_nothing()
    {
        // timeout_ms:0 is "check now": one poll, no successor, so nothing is owed a release and the
        // id its detail names stays live.
        var recorder = new Recorder();

        var result = await UIAutomationService.WaitLoopAsync(
            Request(WaitCondition.ElementEnabled, timeoutMs: 0),
            Polls([El("el_1", enabled: false), El("el_2", enabled: false)]), CancellationToken.None, recorder.Release);

        result.Satisfied.Should().BeFalse();
        result.Attempts.Should().Be(1);
        result.Detail.Should().Contain("el_1");
        recorder.Calls.Should().BeEmpty();
    }

    // ---- throwing polls -----------------------------------------------------------------------

    [Fact]
    public async Task A_poll_that_throws_releases_nothing_and_the_loop_goes_on()
    {
        var recorder = new Recorder();
        int polls = 0;

        var result = await UIAutomationService.WaitLoopAsync(
            Request(),
            _ => ++polls == 1
                ? throw FlaUiShaped.From(FlaUiShaped.RPC_S_SERVER_UNAVAILABLE)
                : Task.FromResult(new WaitEvidence(Matches: [El("el_9")])),
            CancellationToken.None, recorder.Release);

        result.Satisfied.Should().BeTrue();
        recorder.All.Should().BeEmpty("a throwing poll names no ids, and the satisfying one carries its only match");
    }

    [Fact]
    public async Task Ids_pending_from_the_poll_before_a_throwing_poll_are_released_before_the_throwing_gather()
    {
        var recorder = new Recorder();

        var result = await UIAutomationService.WaitLoopAsync(
            Request(WaitCondition.ElementEnabled),
            LoggedPolls(recorder,
                () => new WaitEvidence(Matches: [El("el_1", enabled: false), El("el_2", enabled: false)]),
                () => throw new InvalidOperationException("provider exploded"),
                () => new WaitEvidence(Matches: [El("el_3")])),
            CancellationToken.None, recorder.Release);

        result.Satisfied.Should().BeTrue();
        result.Element!.ElementId.Should().Be("el_3");
        recorder.Events.Should().Equal(
            ["gather 1", "release el_1,el_2", "gather 2", "gather 3"],
            "pending ids go back before the next gather as usual; the throwing poll owes nothing new");
    }

    // ---- cancellation -------------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_between_polls_does_not_skip_a_release_already_owed()
    {
        // The poll returns, THEN the token fires (during the inter-poll delay in a real wait). The
        // ids that poll minted are pending; a cancelled wait owes every pending id its release.
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource();

        Func<Task> act = () => UIAutomationService.WaitLoopAsync(
            Request(WaitCondition.ElementEnabled, timeoutMs: 5000),
            _ =>
            {
                cts.Cancel();
                return Task.FromResult(new WaitEvidence(Matches: [El("el_1", enabled: false), El("el_2", enabled: false)]));
            },
            cts.Token, recorder.Release);

        await act.Should().ThrowAsync<OperationCanceledException>();
        recorder.All.Should().BeEquivalentTo(["el_1", "el_2"]);
    }

    [Fact]
    public async Task Cancellation_during_the_inter_poll_delay_releases_the_pending_poll()
    {
        // The token fires on a timer while the loop sleeps between polls (intervalMs long enough that
        // it is certainly asleep): the last unsatisfied poll's ids are still pending, and a cancelled
        // wait returns nothing that could name them.
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource();
        int n = 0;
        var minted = new List<string>();

        Func<Task> act = () => UIAutomationService.WaitLoopAsync(
            Request(WaitCondition.ElementEnabled, timeoutMs: 60_000, intervalMs: 2_000),
            _ =>
            {
                var a = El($"el_{n++}", enabled: false);
                minted.Add(a.ElementId);
                if (minted.Count == 1) cts.CancelAfter(100);
                return Task.FromResult(new WaitEvidence(Matches: [a]));
            },
            cts.Token, recorder.Release);

        await act.Should().ThrowAsync<OperationCanceledException>();
        recorder.All.Should().BeEquivalentTo(minted, "nothing a cancelled wait minted is left behind");
    }

    [Fact]
    public async Task A_gather_that_is_cancelled_after_an_unsatisfied_poll_leaves_nothing_pending()
    {
        // Poll 1 is unsatisfied; poll 2's gather observes the token and throws. Poll 1's ids were
        // released at the top of iteration 2 (before that gather), so nothing is owed twice.
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource();
        int polls = 0;

        Func<Task> act = () => UIAutomationService.WaitLoopAsync(
            Request(WaitCondition.ElementEnabled, timeoutMs: 5000),
            token =>
            {
                if (++polls == 2) { cts.Cancel(); token.ThrowIfCancellationRequested(); }
                return Task.FromResult(new WaitEvidence(Matches: [El("el_1", enabled: false), El("el_2", enabled: false)]));
            },
            cts.Token, recorder.Release);

        await act.Should().ThrowAsync<OperationCanceledException>();
        recorder.All.Should().BeEquivalentTo(["el_1", "el_2"]);
        recorder.All.Should().OnlyHaveUniqueItems("an id is released once");
    }

    // ---- never released -----------------------------------------------------------------------

    [Theory]
    [InlineData(WaitCondition.TextExists)]
    [InlineData(WaitCondition.FocusedElement)]
    [InlineData(WaitCondition.ActiveWindow)]
    public async Task Snapshot_and_window_evidence_is_never_released(WaitCondition condition)
    {
        // A snapshot's ids are its own generation, dropped by the next snapshot; a window list has
        // no element ids. Releasing either would expire ids the caller may have read off a result.
        var recorder = new Recorder();
        var focused = new SnapshotElement("el_7", "Notepad", "Edit", "Save", 5, 5, new Bounds(0, 0, 10, 10), "type",
            Focused: true, IsPassword: false, Value: null, Toggle: null, Expand: null, Shortcut: null,
            RangeValue: null, RangeMin: null, RangeMax: null);
        var snapshot = new SnapshotResult([], null, new CursorPosition(0, 0), 0, [focused], [], null, false, 500, 1, 0);
        var windows = new[]
        {
            new WindowInfo("Save", 1, 1, "notepad", WindowState.Normal, new Bounds(0, 0, 10, 10), 0,
                IsActive: true, IsBrowser: false, MonitorIndex: 0),
        };

        await UIAutomationService.WaitLoopAsync(
            Request(condition, timeoutMs: 50),
            _ => Task.FromResult(new WaitEvidence(Snapshot: snapshot, Windows: windows)),
            CancellationToken.None, recorder.Release);

        recorder.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_release_callback_the_loop_behaves_exactly_as_before()
    {
        // Every existing caller (and WaitForServiceTests) passes no callback.
        var result = await UIAutomationService.WaitLoopAsync(
            Request(), Polls([], [El("el_1"), El("el_2")]), CancellationToken.None);

        result.Satisfied.Should().BeTrue();
        result.Attempts.Should().Be(2);
        result.Element!.ElementId.Should().Be("el_1");
    }
}
