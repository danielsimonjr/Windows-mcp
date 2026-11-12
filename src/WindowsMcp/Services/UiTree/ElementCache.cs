using System.Globalization;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services.UiTree;

/// <summary>
/// D-11: the element-id cache, extracted from <c>UIAutomationService</c> so its bounds can be
/// tested without a desktop.
/// </summary>
internal static class ElementCache
{
    /// <summary>
    /// D-11: the cache capacity the service runs with — the larger of
    /// <see cref="UiTreeOptions.MaxCachedElements"/> and twice the element budget, so the ids one
    /// call issues (a <c>get_state</c> tree is bounded by the budget) can never evict each other.
    /// Saturates at <see cref="int.MaxValue"/>: <c>--max-tree-elements</c> is only checked for
    /// being positive, and <c>2 * int.MaxValue</c> in 32 bits is negative.
    /// </summary>
    public static int CapacityFor(UiTreeOptions options)
    {
        long wanted = Math.Max(options.MaxCachedElements, 2L * options.MaxElements);
        return (int)Math.Clamp(wanted, 1, int.MaxValue);
    }
}

/// <summary>
/// D-11: element ids and what they name. Two kinds share one counter: ids from <see cref="Add"/>
/// (<c>find_element</c>, <c>get_state</c>) live in a bounded least-recently-used set, and ids from
/// <see cref="AddSnapshot"/> live until the next <see cref="EvictSnapshot"/> and are never
/// LRU-evicted — a snapshot's size is already bounded by the element budget. Before D-11 the first
/// kind was never removed at all, so a long-running host held a COM reference for every element it
/// had ever returned.
/// </summary>
internal sealed class ElementCache<T> where T : class
{
    private const string Prefix = "el_";

    private sealed record Entry(T Element, LinkedListNode<string>? Recency);

    private readonly Lock _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    /// <summary>Non-snapshot ids, least recently used first.</summary>
    private readonly LinkedList<string> _recency = new();
    private readonly List<string> _snapshot = new();
    private long _nextId;

    /// <param name="capacity">Max non-snapshot entries; below 1 is an <see cref="ArgumentOutOfRangeException"/>.</param>
    public ElementCache(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be at least 1");
        _capacity = capacity;
    }

    /// <summary>All live entries, snapshot and non-snapshot.</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Mint <c>el_N</c>, LRU-tracked; evicts the least-recently-used non-snapshot entry past capacity.</summary>
    public string Add(T element)
    {
        ArgumentNullException.ThrowIfNull(element);
        lock (_gate)
        {
            var id = Mint();
            _entries[id] = new Entry(element, _recency.AddLast(id));
            while (_recency.Count > _capacity)
            {
                _entries.Remove(_recency.First!.Value);
                _recency.RemoveFirst();
            }
            return id;
        }
    }

    /// <summary>Drop every id issued by <see cref="AddSnapshot"/> since the last call.</summary>
    public void EvictSnapshot()
    {
        lock (_gate)
        {
            foreach (var id in _snapshot) _entries.Remove(id);
            _snapshot.Clear();
        }
    }

    /// <summary>Mint <c>el_N</c>, not counted against capacity, never LRU-evicted.</summary>
    public string AddSnapshot(T element)
    {
        ArgumentNullException.ThrowIfNull(element);
        lock (_gate)
        {
            var id = Mint();
            _entries[id] = new Entry(element, null);
            _snapshot.Add(id);
            return id;
        }
    }

    /// <summary>A hit on a non-snapshot id refreshes its recency.</summary>
    public bool TryGet(string id, out T element)
    {
        lock (_gate)
        {
            if (id is not null && _entries.TryGetValue(id, out var entry))
            {
                if (entry.Recency is { } node)
                {
                    _recency.Remove(node);
                    _recency.AddLast(node);
                }
                element = entry.Element;
                return true;
            }
        }
        element = null!;
        return false;
    }

    /// <summary>
    /// The element, or a <see cref="KeyNotFoundException"/> that tells the two misses apart: an id
    /// this cache issued and no longer holds has <i>expired</i> (and the remedy is a fresh one); an
    /// id it never issued is <i>unknown</i>.
    /// </summary>
    public T Get(string id)
    {
        if (TryGet(id, out var element)) return element;

        if (WasIssued(id))
            throw new KeyNotFoundException(
                $"Element '{id}' has expired: a newer snapshot replaced it, or it aged out of the element cache. " +
                "These take a snapshot too: wait_for with a text or focus condition, screenshot with annotate, " +
                "and scrape with source 'dom'. Take a new snapshot (or run find_element again) and use the id it returns.");
        throw new KeyNotFoundException(
            $"Unknown element id '{id}': no such id was issued. Ids come from snapshot, find_element or get_state.");
    }

    /// <summary>
    /// D-11 R18: drop one id of either kind; true when it was present. A removed id was issued, so
    /// it afterwards reads as expired, not unknown.
    /// </summary>
    public bool Remove(string id)
    {
        if (id is null) return false;
        lock (_gate)
        {
            if (!_entries.Remove(id, out var entry)) return false;
            if (entry.Recency is { } node) _recency.Remove(node);
            else _snapshot.Remove(id);
            return true;
        }
    }

    private string Mint() => Prefix + (_nextId++).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// True when <paramref name="id"/> is exactly a string this cache has minted. The counter never
    /// goes back, so "issued" is "canonical <c>el_N</c> with N below the counter" — no history to
    /// keep, and it stays true however long ago the id was dropped. The round-trip comparison is
    /// what keeps <c>el_00</c>, <c>EL_0</c> and <c> el_0</c> from aliasing <c>el_0</c>.
    /// </summary>
    public bool WasIssued(string? id)
    {
        if (id is null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        if (!long.TryParse(id.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return false;
        if (Prefix + n.ToString(CultureInfo.InvariantCulture) != id) return false;
        lock (_gate) return n < _nextId;
    }
}
