using System.Collections.Concurrent;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;
using Xunit;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// D-11: the element-id cache on its own. Before D-11 every <c>find_element</c> hit, every
/// <c>get_state</c> node and every element-targeted click re-minted an id into a dictionary that
/// nothing but the next snapshot ever trimmed — on a long-running HTTP host, an unbounded set of
/// live COM references. The cache is generic so these tests hold plain objects: no UIA, no desktop.
/// </summary>
[Trait("Category", "Unit")]
public class ElementCacheTests
{
    /// <summary>A stand-in for an AutomationElement: identity is all the cache may rely on.</summary>
    private sealed class Probe(string label)
    {
        public string Label { get; } = label;
        public override string ToString() => Label;
    }

    private static Probe P(string label) => new(label);

    private static long IdNumber(string id)
    {
        id.Should().MatchRegex("^el_[0-9]+$", "ids keep the el_N shape every tool and the snapshot text use");
        return long.Parse(id["el_".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---- construction -------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Ctor_rejects_a_capacity_below_one(int capacity)
    {
        var act = () => new ElementCache<Probe>(capacity);

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*capacity*");
    }

    [Fact]
    public void A_new_cache_is_empty()
        => new ElementCache<Probe>(3).Count.Should().Be(0);

    // ---- R4: bounded LRU over non-snapshot ids ------------------------------------------------

    [Fact]
    public void Add_up_to_capacity_keeps_every_entry()
    {
        var cache = new ElementCache<Probe>(3);
        var a = P("a"); var b = P("b"); var c = P("c");

        var ids = new[] { cache.Add(a), cache.Add(b), cache.Add(c) };

        cache.Count.Should().Be(3, "filling the cache exactly is not overflowing it");
        cache.Get(ids[0]).Should().BeSameAs(a);
        cache.Get(ids[1]).Should().BeSameAs(b);
        cache.Get(ids[2]).Should().BeSameAs(c);
    }

    [Fact]
    public void Add_past_capacity_evicts_the_oldest_entry_first()
    {
        // The checklist's "N+1 lookups past the cap evict the oldest".
        var cache = new ElementCache<Probe>(3);
        var first = cache.Add(P("a"));
        var second = cache.Add(P("b"));
        var third = cache.Add(P("c"));

        var fourth = cache.Add(P("d"));

        cache.Count.Should().Be(3);
        cache.TryGet(first, out _).Should().BeFalse("the least recently used id goes first");
        cache.TryGet(second, out _).Should().BeTrue();
        cache.TryGet(third, out _).Should().BeTrue();
        cache.TryGet(fourth, out _).Should().BeTrue();
    }

    [Fact]
    public void A_capacity_of_one_holds_only_the_latest_id()
    {
        var cache = new ElementCache<Probe>(1);
        var a = cache.Add(P("a"));
        var b = cache.Add(P("b"));

        cache.Count.Should().Be(1);
        cache.TryGet(a, out _).Should().BeFalse();
        cache.TryGet(b, out _).Should().BeTrue();
    }

    [Fact]
    public void TryGet_refreshes_recency_so_a_used_id_outlives_an_unused_older_one()
    {
        var cache = new ElementCache<Probe>(2);
        var a = cache.Add(P("a"));
        var b = cache.Add(P("b"));

        cache.TryGet(a, out _).Should().BeTrue();   // a is now the most recently used
        var c = cache.Add(P("c"));

        cache.TryGet(b, out _).Should().BeFalse("b was the least recently USED, not a - LRU, not FIFO");
        cache.TryGet(a, out _).Should().BeTrue();
        cache.TryGet(c, out _).Should().BeTrue();
    }

    [Fact]
    public void Get_refreshes_recency_too()
    {
        // GetElementAsync and every input verb resolve through a lookup; an element the agent keeps
        // clicking must not be the next one evicted just because it was found first.
        var cache = new ElementCache<Probe>(2);
        var a = cache.Add(P("a"));
        var b = cache.Add(P("b"));

        cache.Get(a);
        cache.Add(P("c"));

        cache.TryGet(b, out _).Should().BeFalse();
        cache.TryGet(a, out _).Should().BeTrue();
    }

    [Fact]
    public void A_miss_does_not_disturb_recency()
    {
        var cache = new ElementCache<Probe>(2);
        var a = cache.Add(P("a"));
        var b = cache.Add(P("b"));

        cache.TryGet("el_999999", out _).Should().BeFalse();
        cache.Add(P("c"));

        cache.TryGet(a, out _).Should().BeFalse("a was still the oldest; a miss on another id changes nothing");
        cache.TryGet(b, out _).Should().BeTrue();
    }

    [Fact]
    public void Ten_thousand_adds_leave_the_cache_at_its_cap()
    {
        // The D-11 "Done when": a loop of 10 000 find_element calls leaves the cache at its cap.
        var cache = new ElementCache<Probe>(500);
        string last = "";
        for (int i = 0; i < 10_000; i++) last = cache.Add(P("x" + i));

        cache.Count.Should().Be(500);
        cache.TryGet(last, out _).Should().BeTrue();
    }

    [Fact]
    public void TryGet_on_a_miss_returns_false_and_no_element()
    {
        var cache = new ElementCache<Probe>(2);

        cache.TryGet("el_0", out var element).Should().BeFalse();

        element.Should().BeNull();
    }

    // ---- R5: the snapshot generation ----------------------------------------------------------

    [Fact]
    public void Snapshot_ids_are_not_counted_against_capacity()
    {
        var cache = new ElementCache<Probe>(2);
        var snap = Enumerable.Range(0, 5).Select(i => cache.AddSnapshot(P("s" + i))).ToArray();
        var a = cache.Add(P("a"));
        var b = cache.Add(P("b"));

        cache.Count.Should().Be(7, "five snapshot ids plus a full non-snapshot cache of two");
        cache.TryGet(a, out _).Should().BeTrue();
        cache.TryGet(b, out _).Should().BeTrue();
        foreach (var id in snap) cache.TryGet(id, out _).Should().BeTrue();
    }

    [Fact]
    public void Snapshot_ids_survive_any_amount_of_non_snapshot_churn()
    {
        // The checklist's "a snapshot id survives non-snapshot churn".
        var cache = new ElementCache<Probe>(3);
        var snapElement = P("snap");
        var snap = cache.AddSnapshot(snapElement);

        for (int i = 0; i < 1_000; i++) cache.Add(P("x" + i));

        cache.Get(snap).Should().BeSameAs(snapElement);
        cache.Count.Should().Be(4, "three non-snapshot entries at the cap plus the one snapshot id");
    }

    [Fact]
    public void Looking_up_a_snapshot_id_does_not_make_it_evictable_or_push_out_a_non_snapshot_id()
    {
        var cache = new ElementCache<Probe>(2);
        var snap = cache.AddSnapshot(P("snap"));
        var a = cache.Add(P("a"));
        var b = cache.Add(P("b"));

        cache.Get(snap);
        cache.TryGet(snap, out _);

        cache.Count.Should().Be(3);
        cache.TryGet(a, out _).Should().BeTrue("a snapshot lookup must not count against the non-snapshot cap");
        cache.TryGet(b, out _).Should().BeTrue();

        for (int i = 0; i < 10; i++) cache.Add(P("x" + i));
        cache.TryGet(snap, out _).Should().BeTrue("a looked-up snapshot id is still never LRU-evicted");
    }

    [Fact]
    public void EvictSnapshot_drops_every_snapshot_id_and_keeps_every_non_snapshot_id()
    {
        var cache = new ElementCache<Probe>(5);
        var a = cache.Add(P("a"));
        var snap = Enumerable.Range(0, 4).Select(i => cache.AddSnapshot(P("s" + i))).ToArray();
        var b = cache.Add(P("b"));

        cache.EvictSnapshot();

        cache.Count.Should().Be(2);
        foreach (var id in snap) cache.TryGet(id, out _).Should().BeFalse($"{id} belonged to the evicted snapshot");
        cache.TryGet(a, out _).Should().BeTrue("find_element / get_state ids are not snapshot ids");
        cache.TryGet(b, out _).Should().BeTrue();
    }

    [Fact]
    public void EvictSnapshot_drops_only_the_previous_generation()
    {
        var cache = new ElementCache<Probe>(5);
        var old = cache.AddSnapshot(P("old"));
        cache.EvictSnapshot();
        var fresh = cache.AddSnapshot(P("fresh"));

        cache.TryGet(old, out _).Should().BeFalse();
        cache.TryGet(fresh, out _).Should().BeTrue("ids added after the eviction are the new generation");
        cache.Count.Should().Be(1);
    }

    [Fact]
    public void EvictSnapshot_on_an_empty_cache_or_twice_is_a_no_op()
    {
        var cache = new ElementCache<Probe>(2);
        cache.EvictSnapshot();
        var a = cache.Add(P("a"));

        cache.EvictSnapshot();
        cache.EvictSnapshot();

        cache.Count.Should().Be(1);
        cache.TryGet(a, out _).Should().BeTrue();
    }

    // ---- R6: one counter, el_N, never reused --------------------------------------------------

    [Fact]
    public void Ids_are_el_N_unique_and_strictly_increasing_across_both_kinds()
    {
        var cache = new ElementCache<Probe>(2);
        var ids = new List<string>
        {
            cache.Add(P("a")),
            cache.AddSnapshot(P("s0")),
            cache.Add(P("b")),
            cache.AddSnapshot(P("s1")),
            cache.Add(P("c")),
        };

        var numbers = ids.Select(IdNumber).ToArray();
        numbers.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems("one counter serves both kinds");
    }

    [Fact]
    public void Ids_are_never_reused_after_eviction()
    {
        var cache = new ElementCache<Probe>(2);
        var seen = new HashSet<string>();

        for (int round = 0; round < 50; round++)
        {
            seen.Add(cache.Add(P("x" + round))).Should().BeTrue("an evicted id must never name a different element");
            seen.Add(cache.AddSnapshot(P("s" + round))).Should().BeTrue();
            if (round % 5 == 0) cache.EvictSnapshot();
        }

        seen.Should().HaveCount(100);
    }

    [Fact]
    public void Adding_the_same_element_twice_mints_two_distinct_ids()
    {
        // Add is "mint"; reusing an id for an element already present is a different feature
        // (E-11, stable ids). The two ids must at least never collide.
        var cache = new ElementCache<Probe>(5);
        var same = P("same");

        var first = cache.Add(same);
        var second = cache.Add(same);

        first.Should().NotBe(second);
        cache.Get(first).Should().BeSameAs(same);
        cache.Get(second).Should().BeSameAs(same);
    }

    // ---- R7: "expired" vs "unknown" -----------------------------------------------------------

    [Fact]
    public void Get_on_an_id_evicted_by_LRU_says_it_expired_and_names_the_remedy()
    {
        var cache = new ElementCache<Probe>(1);
        var gone = cache.Add(P("a"));
        cache.Add(P("b"));

        var act = () => cache.Get(gone);

        var message = act.Should().Throw<KeyNotFoundException>().Which.Message;
        message.Should().Contain(gone, "the caller is told which id");
        message.Should().ContainEquivalentOf("expired");
        message.Should().Contain("snapshot", "the remedy is a new snapshot");
        message.Should().NotContainEquivalentOf("not in cache", "the old message gave the caller nothing to do");
        message.Should().NotContainEquivalentOf("unknown", "this id WAS issued");
    }

    [Fact]
    public void Get_on_an_id_dropped_by_EvictSnapshot_says_it_expired_and_names_the_remedy()
    {
        var cache = new ElementCache<Probe>(4);
        var gone = cache.AddSnapshot(P("s"));
        cache.EvictSnapshot();

        var act = () => cache.Get(gone);

        var message = act.Should().Throw<KeyNotFoundException>().Which.Message;
        message.Should().Contain(gone);
        message.Should().ContainEquivalentOf("expired");
        message.Should().Contain("snapshot");
        message.Should().NotContainEquivalentOf("unknown");
    }

    [Fact]
    public void An_expired_id_stays_expired_however_much_churn_follows()
    {
        // Distinguishing expired from unknown must not depend on the id still being "recent".
        var cache = new ElementCache<Probe>(2);
        var gone = cache.Add(P("a"));
        for (int i = 0; i < 5_000; i++) cache.Add(P("x" + i));

        var act = () => cache.Get(gone);

        act.Should().Throw<KeyNotFoundException>().Which.Message.Should().ContainEquivalentOf("expired");
    }

    [Theory]
    [InlineData("el_999999")]     // the right shape, never issued
    [InlineData("foo")]
    [InlineData("")]
    [InlineData("el_")]
    [InlineData("el_-1")]
    [InlineData("EL_0")]          // ids are case-sensitive: EL_0 was never issued
    [InlineData(" el_0")]         // nor was " el_0"
    [InlineData("el_00")]         // nor el_00, though el_0 was - a numeric parse must not alias it
    [InlineData("el_99999999999999999999999")]   // overflows any integer parse
    public void Get_on_an_id_never_issued_says_it_is_unknown(string id)
    {
        var cache = new ElementCache<Probe>(2);
        for (int i = 0; i < 6; i++) cache.Add(P("x" + i));   // el_0.. issued, el_0..el_3 evicted

        var act = () => cache.Get(id);

        var message = act.Should().Throw<KeyNotFoundException>().Which.Message;
        message.Should().ContainEquivalentOf("unknown");
        message.Should().NotContainEquivalentOf("expired", $"'{id}' was never issued by this cache");
        message.Should().NotContainEquivalentOf("not in cache");
    }

    [Fact]
    public void Get_on_the_id_the_counter_would_mint_next_is_unknown_not_expired()
    {
        // The upper boundary of "issued": el_N with N strictly below the counter. The next id has
        // not been handed out yet, so calling it "expired" (an off-by-one <=) would tell the caller
        // to re-snapshot for an id the server never gave it.
        var cache = new ElementCache<Probe>(2);
        string last = "";
        for (int i = 0; i < 3; i++) last = cache.Add(P("x" + i));
        var next = "el_" + (IdNumber(last) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var act = () => cache.Get(next);

        var message = act.Should().Throw<KeyNotFoundException>().Which.Message;
        message.Should().ContainEquivalentOf("unknown");
        message.Should().NotContainEquivalentOf("expired", $"'{next}' has not been issued yet");
        cache.Add(P("y")).Should().Be(next, "precondition: that really was the next id the cache mints");
    }

    [Fact]
    public void A_null_id_is_a_miss_and_unknown_never_a_null_reference()
    {
        // The MCP layer can hand a JSON null through; Dictionary.TryGetValue(null) would throw
        // ArgumentNullException from inside the lock instead of the KeyNotFoundException the tools promise.
        var cache = new ElementCache<Probe>(2);
        cache.Add(P("a"));

        cache.TryGet(null!, out var element).Should().BeFalse();
        element.Should().BeNull();

        var act = () => cache.Get(null!);

        act.Should().Throw<KeyNotFoundException>().Which.Message.Should().ContainEquivalentOf("unknown");
    }

    [Fact]
    public void Get_on_an_unknown_id_names_the_id()
    {
        var cache = new ElementCache<Probe>(2);

        var act = () => cache.Get("el_424242");

        act.Should().Throw<KeyNotFoundException>().Which.Message.Should().Contain("el_424242");
    }

    [Fact]
    public void Get_on_a_live_id_returns_the_element_it_was_issued_for()
    {
        var cache = new ElementCache<Probe>(3);
        var a = P("a");
        var s = P("s");
        var aId = cache.Add(a);
        var sId = cache.AddSnapshot(s);

        cache.Get(aId).Should().BeSameAs(a);
        cache.Get(sId).Should().BeSameAs(s);
        cache.TryGet(aId, out var viaTry).Should().BeTrue();
        viaTry.Should().BeSameAs(a);
    }

    // ---- R8: thread safety --------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_Add_and_TryGet_never_throw_or_break_the_cap()
    {
        const int capacity = 64;
        const int threads = 8;
        const int perThread = 5_000;
        var cache = new ElementCache<Probe>(capacity);
        var minted = new ConcurrentBag<string>();
        using var start = new ManualResetEventSlim(false);

        var workers = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < perThread; i++)
            {
                var id = cache.Add(P($"{t}-{i}"));
                minted.Add(id);
                cache.TryGet(id, out _);
                cache.TryGet("el_" + (i * 7 % 1000), out _);
                if (i % 500 == 0) { cache.AddSnapshot(P($"s{t}-{i}")); }
                if (i % 1_000 == 0) cache.EvictSnapshot();
                cache.Count.Should().BeLessThanOrEqualTo(capacity + threads * (perThread / 500 + 1));
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(workers);

        minted.Should().HaveCount(threads * perThread);
        minted.Distinct().Should().HaveCount(threads * perThread, "no two threads may be handed the same id");
        cache.EvictSnapshot();
        cache.Count.Should().Be(capacity, "after the snapshot generation is gone exactly the cap remains");
    }

    // ---- R9: the capacity the service runs with -----------------------------------------------

    [Fact]
    public void UiTreeOptions_default_cache_cap_is_five_thousand()
    {
        UiTreeOptions.Default.MaxCachedElements.Should().Be(5000);
        new UiTreeOptions(500).MaxCachedElements.Should().Be(5000, "every existing construction keeps the default");
        new UiTreeOptions(42, Profile: true).MaxCachedElements.Should().Be(5000);
    }

    [Theory]
    [InlineData(500, 5000, 5000)]          // the defaults: the configured cap wins
    [InlineData(2500, 5000, 5000)]         // 2 x budget == cap
    [InlineData(3000, 5000, 6000)]         // a big --max-tree-elements raises the floor to 2 x budget
    [InlineData(1, 1, 2)]
    [InlineData(10, 100, 100)]
    public void CapacityFor_is_the_larger_of_the_cap_and_twice_the_element_budget(int maxElements, int maxCached, int expected)
        => ElementCache.CapacityFor(new UiTreeOptions(maxElements, MaxCachedElements: maxCached)).Should().Be(expected);

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1_500_000_000)]            // 2 x this overflows an int
    [InlineData(1_073_741_824)]            // exactly 2^30: 2 x this is int.MaxValue + 1
    public void CapacityFor_saturates_instead_of_overflowing_for_a_huge_element_budget(int maxElements)
    {
        // --max-tree-elements is parsed with int.TryParse and only checked >= 1, so int.MaxValue is a
        // legal flag. Math.Max(5000, 2 * int.MaxValue) is Math.Max(5000, -2): a cap of 5 000 under a
        // budget of two billion, and one get_state would evict its own ids.
        var capacity = ElementCache.CapacityFor(new UiTreeOptions(maxElements));

        capacity.Should().BeGreaterThanOrEqualTo(maxElements, "one call's own ids must never evict each other");
        capacity.Should().Be(int.MaxValue);
    }

    // ---- R17: the expired message names every hidden snapshot taker ---------------------------

    [Fact]
    public void Get_on_an_expired_id_names_every_call_that_takes_a_snapshot_behind_the_callers_back()
    {
        // scrape(source:"dom") walks the browser through SnapshotAsync and so expires the caller's
        // snapshot ids exactly as wait_for's text/focus conditions and an annotated screenshot do.
        // An agent that scraped and then lost its ids has to be able to read why.
        var cache = new ElementCache<Probe>(2);
        var gone = cache.AddSnapshot(P("s"));
        cache.EvictSnapshot();

        var act = () => cache.Get(gone);

        var message = act.Should().Throw<KeyNotFoundException>().Which.Message;
        message.Should().Contain("wait_for", "its text_exists / focused_element polls are snapshots");
        message.Should().Contain("screenshot", "annotate:true is a snapshot walk");
        message.Should().Contain("scrape", "source:dom is a snapshot walk too (R17)");
    }

    // ---- R18: Remove ---------------------------------------------------------------------------

    [Fact]
    public void Remove_drops_a_live_non_snapshot_id_and_says_it_was_there()
    {
        var cache = new ElementCache<Probe>(3);
        var a = cache.Add(P("a"));
        var b = cache.Add(P("b"));

        cache.Remove(a).Should().BeTrue();

        cache.Count.Should().Be(1);
        cache.TryGet(a, out _).Should().BeFalse();
        cache.TryGet(b, out _).Should().BeTrue("only the named id goes");
    }

    [Fact]
    public void Remove_drops_a_live_snapshot_id_and_the_next_EvictSnapshot_is_still_exact()
    {
        var cache = new ElementCache<Probe>(3);
        var s1 = cache.AddSnapshot(P("s1"));
        var s2 = cache.AddSnapshot(P("s2"));
        var a = cache.Add(P("a"));

        cache.Remove(s1).Should().BeTrue("Remove works for either kind");
        cache.Count.Should().Be(2);
        cache.TryGet(s2, out _).Should().BeTrue();

        cache.EvictSnapshot();

        cache.Count.Should().Be(1, "the rest of the generation goes; the removed id is not counted twice");
        cache.TryGet(a, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("el_999999")]
    [InlineData("foo")]
    [InlineData("")]
    public void Remove_of_an_id_that_is_not_held_is_false_and_changes_nothing(string id)
    {
        var cache = new ElementCache<Probe>(2);
        var a = cache.Add(P("a"));

        cache.Remove(id).Should().BeFalse();

        cache.Count.Should().Be(1);
        cache.TryGet(a, out _).Should().BeTrue();
    }

    [Fact]
    public void Remove_twice_is_true_then_false()
    {
        var cache = new ElementCache<Probe>(2);
        var a = cache.Add(P("a"));

        cache.Remove(a).Should().BeTrue();
        cache.Remove(a).Should().BeFalse("it is no longer there");
    }

    [Fact]
    public void Remove_of_a_null_id_is_false_never_a_null_reference()
    {
        var cache = new ElementCache<Probe>(2);
        cache.Add(P("a"));

        cache.Remove(null!).Should().BeFalse();
        cache.Count.Should().Be(1);
    }

    [Fact]
    public void A_removed_id_reads_as_expired_not_unknown()
    {
        // The server issued it; the remedy (a fresh id) is the same as for an LRU eviction.
        var cache = new ElementCache<Probe>(3);
        var a = cache.Add(P("a"));
        cache.Remove(a);

        var act = () => cache.Get(a);

        var message = act.Should().Throw<KeyNotFoundException>().Which.Message;
        message.Should().ContainEquivalentOf("expired");
        message.Should().NotContainEquivalentOf("unknown");
    }

    [Fact]
    public void Remove_frees_capacity_so_the_next_add_evicts_nobody()
    {
        // A Remove that dropped the entry but left its recency node behind would make the next
        // Add evict a LIVE id to make room for one already gone - the wait_for release would then
        // evict exactly the caller ids it exists to protect.
        var cache = new ElementCache<Probe>(2);
        var keep = cache.Add(P("keep"));
        var temp = cache.Add(P("temp"));

        cache.Remove(temp);
        var next = cache.Add(P("next"));

        cache.Count.Should().Be(2);
        cache.TryGet(keep, out _).Should().BeTrue("removing temp made room; keep must not be evicted");
        cache.TryGet(next, out _).Should().BeTrue();
    }

    [Fact]
    public void Remove_of_the_least_recent_id_leaves_LRU_order_intact_for_the_rest()
    {
        var cache = new ElementCache<Probe>(2);
        var a = cache.Add(P("a"));
        var b = cache.Add(P("b"));
        cache.Remove(a);
        var c = cache.Add(P("c"));

        var d = cache.Add(P("d"));

        cache.TryGet(b, out _).Should().BeFalse("b is now the least recently used and goes first");
        cache.TryGet(c, out _).Should().BeTrue();
        cache.TryGet(d, out _).Should().BeTrue();
    }

    [Fact]
    public void A_wait_shaped_churn_of_add_then_remove_never_evicts_a_callers_id()
    {
        // The R18 scenario on the cache alone: the caller holds ids, then a long wait mints and
        // releases 20 ids per poll, thousands of times, through a cap of 25.
        var cache = new ElementCache<Probe>(25);
        var held = Enumerable.Range(0, 5).Select(i => cache.Add(P("held" + i))).ToArray();

        for (int poll = 0; poll < 1_000; poll++)
        {
            var minted = Enumerable.Range(0, 20).Select(i => cache.Add(P($"p{poll}-{i}"))).ToArray();
            foreach (var id in minted) cache.Remove(id).Should().BeTrue();
        }

        cache.Count.Should().Be(5);
        foreach (var id in held) cache.TryGet(id, out _).Should().BeTrue($"{id} was the caller's");
    }
}
