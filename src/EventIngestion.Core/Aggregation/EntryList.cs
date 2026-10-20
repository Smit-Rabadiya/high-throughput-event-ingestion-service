namespace EventIngestion.Core.Aggregation;

/// <summary>
/// Array-backed list of (timestamp, value) entries kept sorted by timestamp, with a moving head so
/// front eviction is O(1). Inserts near the tail (slightly late readings) shift only a few entries.
/// A struct array avoids one heap node per reading (unlike LinkedList/Queue of objects).
/// </summary>
internal sealed class EntryList
{
    private const int MinCapacity = 16;

    private (long Ts, double Value)[] _items = [];
    private int _head;
    private int _count;

    public int Count => _count;

    public (long Ts, double Value) this[int index] => _items[_head + index];

    public (long Ts, double Value) First => _items[_head];

    /// <summary>Index of the first entry whose timestamp is greater than <paramref name="ts"/> (== Count if none).</summary>
    public int UpperBound(long ts)
    {
        int lo = 0, hi = _count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_items[_head + mid].Ts <= ts) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public void InsertAt(int index, long ts, double value)
    {
        EnsureRoomForOne();
        int abs = _head + index;
        if (index < _count)
            Array.Copy(_items, abs, _items, abs + 1, _count - index);
        _items[abs] = (ts, value);
        _count++;
    }

    public void RemoveRange(int index, int length)
    {
        if (length <= 0) return;
        int abs = _head + index;
        int tail = _count - index - length;
        if (tail > 0)
            Array.Copy(_items, abs + length, _items, abs, tail);
        _count -= length;
        if (_count == 0) _head = 0;
    }

    public void RemoveFront(int length)
    {
        _head += length;
        _count -= length;
        if (_count == 0)
        {
            _head = 0;
            // Idle window: release oversized storage so memory tracks live data.
            if (_items.Length > 4 * MinCapacity) _items = [];
        }
    }

    private void EnsureRoomForOne()
    {
        if (_head + _count < _items.Length) return;

        if (_head > 0 && _head >= _items.Length / 2)
        {
            Array.Copy(_items, _head, _items, 0, _count); // compact in place
            _head = 0;
            return;
        }

        var bigger = new (long, double)[Math.Max(MinCapacity, _items.Length * 2)];
        Array.Copy(_items, _head, bigger, 0, _count);
        _items = bigger;
        _head = 0;
    }
}
