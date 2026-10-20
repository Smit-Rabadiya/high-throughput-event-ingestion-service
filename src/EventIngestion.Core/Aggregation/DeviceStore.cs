using System.Collections.Concurrent;
using EventIngestion.Core.Models;
using EventIngestion.Core.Options;

namespace EventIngestion.Core.Aggregation;

/// <summary>
/// Registry of per-device windows. Lock-free reads via ConcurrentDictionary; all aggregate mutation is
/// guarded by the individual <see cref="DeviceWindow"/> lock, so requests only contend on the same device.
/// </summary>
public sealed class DeviceStore
{
    private readonly ConcurrentDictionary<string, DeviceWindow> _devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DeviceWindow>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly WindowOptions _options;
    private readonly TimeProvider _time;

    public DeviceStore(WindowOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
        _lookup = _devices.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public int DeviceCount => _devices.Count;

    public long NowUnixMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Apply one reading. The device id is a span so known devices cost no string allocation.</summary>
    public AddResult Add(ReadOnlySpan<char> deviceId, long timestampUnixMs, double value, long nowUnixMs)
    {
        while (true)
        {
            if (!_lookup.TryGetValue(deviceId, out var window))
            {
                // Only the first sighting of a device allocates its id string.
                window = _devices.GetOrAdd(deviceId.ToString(), static (_, o) => new DeviceWindow(o), _options);
            }

            var result = window.Add(timestampUnixMs, value, nowUnixMs);
            if (result != AddResult.Retired) return result;

            // Sweeper retired this window between lookup and lock: drop the stale entry and retry.
            _devices.TryRemove(new KeyValuePair<string, DeviceWindow>(deviceId.ToString(), window));
        }
    }

    public bool TryGetSnapshot(string deviceId, out AggregateSnapshot snapshot) =>
        TryGetSnapshot(deviceId, NowUnixMs, out snapshot);

    public bool TryGetSnapshot(string deviceId, long nowUnixMs, out AggregateSnapshot snapshot)
    {
        if (_devices.TryGetValue(deviceId, out var window) && window.TryGetSnapshot(nowUnixMs, out snapshot))
            return true;
        snapshot = default;
        return false;
    }

    /// <summary>Removes devices with no readings left in the window; returns how many were dropped.</summary>
    public int SweepIdle(long nowUnixMs)
    {
        int removed = 0;
        foreach (var pair in _devices)
        {
            if (pair.Value.TryRetire(nowUnixMs) && _devices.TryRemove(pair))
                removed++;
        }
        return removed;
    }
}
