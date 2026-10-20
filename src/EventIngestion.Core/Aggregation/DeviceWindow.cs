using EventIngestion.Core.Models;
using EventIngestion.Core.Options;

namespace EventIngestion.Core.Aggregation;

/// <summary>
/// Exact sliding-window state for one device, guarded by its own lock.
/// <list type="bullet">
/// <item>All live readings sorted by timestamp (exact eviction).</item>
/// <item>Running sum -> O(1) average.</item>
/// <item>Two monotonic queues (min, max) -> exact O(1) min/max that stay correct as the oldest reading expires.</item>
/// </list>
/// The window is (now - Window, now]: a reading stamped exactly now - Window is already expired.
/// </summary>
public sealed class DeviceWindow
{
    private readonly Lock _lock = new();
    private readonly long _windowMs;
    private readonly long _futureSkewMs;

    private readonly EntryList _all = new();
    private readonly EntryList _minQueue = new(); // values strictly increasing with timestamp
    private readonly EntryList _maxQueue = new(); // stores -value, so the same logic gives a max queue
    private double _sum;
    private bool _retired;

    public DeviceWindow(WindowOptions options)
    {
        _windowMs = options.WindowMs;
        _futureSkewMs = options.FutureSkewMs;
    }

    public AddResult Add(long timestampUnixMs, double value, long nowUnixMs)
    {
        long cutoff = nowUnixMs - _windowMs;
        if (timestampUnixMs <= cutoff) return AddResult.Stale;
        if (timestampUnixMs > nowUnixMs + _futureSkewMs) return AddResult.Future;

        lock (_lock)
        {
            if (_retired) return AddResult.Retired;

            Evict(cutoff);
            _all.InsertAt(_all.UpperBound(timestampUnixMs), timestampUnixMs, value);
            _sum += value;
            InsertMonotonic(_minQueue, timestampUnixMs, value);
            InsertMonotonic(_maxQueue, timestampUnixMs, -value);
            return AddResult.Accepted;
        }
    }

    /// <summary>Consistent snapshot of the live window; false when the device has no readings in the window.</summary>
    public bool TryGetSnapshot(long nowUnixMs, out AggregateSnapshot snapshot)
    {
        lock (_lock)
        {
            Evict(nowUnixMs - _windowMs);
            if (_all.Count == 0)
            {
                snapshot = default;
                return false;
            }

            snapshot = new AggregateSnapshot(_all.Count, _minQueue.First.Value, -_maxQueue.First.Value, _sum / _all.Count);
            return true;
        }
    }

    /// <summary>
    /// Sweeper hook: if the window is empty after eviction, marks it retired so the caller can drop it from
    /// the registry. A writer racing with removal sees <see cref="AddResult.Retired"/> and re-resolves.
    /// </summary>
    public bool TryRetire(long nowUnixMs)
    {
        lock (_lock)
        {
            if (_retired) return true;
            Evict(nowUnixMs - _windowMs);
            if (_all.Count != 0) return false;
            _retired = true;
            return true;
        }
    }

    private void Evict(long cutoff)
    {
        int expired = _all.UpperBound(cutoff);
        if (expired == 0) return;

        for (int i = 0; i < expired; i++) _sum -= _all[i].Value;
        _all.RemoveFront(expired);
        _minQueue.RemoveFront(_minQueue.UpperBound(cutoff));
        _maxQueue.RemoveFront(_maxQueue.UpperBound(cutoff));

        // Floating-point add/subtract drift: reset exactly when empty, recompute if it ever went non-finite.
        if (_all.Count == 0) _sum = 0;
        else if (!double.IsFinite(_sum)) RecomputeSum();
    }

    private void RecomputeSum()
    {
        double sum = 0;
        for (int i = 0; i < _all.Count; i++) sum += _all[i].Value;
        _sum = sum;
    }

    /// <summary>
    /// Keeps only entries that can still be the minimum at some future time: an entry is dominated if a
    /// later-or-equal-timestamp entry has a value less than or equal to it (that entry outlives it).
    /// Late readings insert at their timestamp position; cost is O(disorder), not O(window).
    /// </summary>
    private static void InsertMonotonic(EntryList queue, long ts, double value)
    {
        int pos = queue.UpperBound(ts);
        if (pos < queue.Count && queue[pos].Value <= value) return; // dominated by a later entry

        int start = pos;
        while (start > 0 && queue[start - 1].Value >= value) start--;
        queue.RemoveRange(start, pos - start);
        queue.InsertAt(start, ts, value);
    }
}
