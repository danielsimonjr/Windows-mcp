using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// Thread-safe bounded FIFO buffer of watch events. When full, the oldest event is dropped to make
/// room (and <see cref="Dropped"/> is incremented) so a burst of changes can never grow unbounded
/// between polls. Pure and deterministic — the unit-testable core of the watch service.
/// </summary>
public sealed class EventRingBuffer
{
    private readonly int _capacity;
    private readonly Queue<WatchEvent> _queue = new();
    private readonly object _lock = new();

    public EventRingBuffer(int capacity = 2000) => _capacity = Math.Max(1, capacity);

    public int Dropped { get; private set; }

    public int Count
    {
        get { lock (_lock) { return _queue.Count; } }
    }

    public void Add(WatchEvent e)
    {
        lock (_lock)
        {
            if (_queue.Count >= _capacity)
            {
                _queue.Dequeue();
                Dropped++;
            }
            _queue.Enqueue(e);
        }
    }

    /// <summary>Remove and return up to <paramref name="max"/> oldest events (all of them if max &lt;= 0).</summary>
    public WatchEvent[] Drain(int max)
    {
        lock (_lock)
        {
            int n = max <= 0 ? _queue.Count : Math.Min(max, _queue.Count);
            var result = new WatchEvent[n];
            for (int i = 0; i < n; i++) result[i] = _queue.Dequeue();
            return result;
        }
    }
}
