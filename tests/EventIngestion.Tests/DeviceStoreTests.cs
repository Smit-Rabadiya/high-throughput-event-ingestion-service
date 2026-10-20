using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Models;
using EventIngestion.Core.Options;

namespace EventIngestion.Tests;

public class DeviceStoreTests
{
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Unknown_device_has_no_snapshot()
    {
        var store = new DeviceStore(new WindowOptions(), new ManualTime(T0));
        Assert.False(store.TryGetSnapshot("nope", out _));
    }

    [Fact]
    public void Ids_are_case_sensitive_and_unicode_safe()
    {
        var time = new ManualTime(T0);
        var store = new DeviceStore(new WindowOptions(), time);
        long now = store.NowUnixMs;
        store.Add("Sensor-é", now, 1, now);
        store.Add("sensor-é", now, 2, now);
        Assert.True(store.TryGetSnapshot("Sensor-é", out var a));
        Assert.True(store.TryGetSnapshot("sensor-é", out var b));
        Assert.Equal((1.0, 2.0), (a.Min, b.Min));
    }

    [Fact]
    public void Sweeper_drops_idle_devices_and_keeps_active_ones()
    {
        var time = new ManualTime(T0);
        var store = new DeviceStore(new WindowOptions(), time);
        long now = store.NowUnixMs;
        store.Add("idle", now, 1, now);
        time.Advance(TimeSpan.FromMinutes(4));
        now = store.NowUnixMs;
        store.Add("active", now, 1, now);

        time.Advance(TimeSpan.FromMinutes(2)); // idle is 6 min old, active 2 min
        Assert.Equal(1, store.SweepIdle(store.NowUnixMs));
        Assert.Equal(1, store.DeviceCount);
        Assert.False(store.TryGetSnapshot("idle", out _));
        Assert.True(store.TryGetSnapshot("active", out _));
    }

    [Fact]
    public void Device_can_be_recreated_after_being_swept()
    {
        var time = new ManualTime(T0);
        var store = new DeviceStore(new WindowOptions(), time);
        store.Add("d", store.NowUnixMs, 1, store.NowUnixMs);
        time.Advance(TimeSpan.FromMinutes(6));
        store.SweepIdle(store.NowUnixMs);
        Assert.Equal(AddResult.Accepted, store.Add("d", store.NowUnixMs, 5, store.NowUnixMs));
        Assert.True(store.TryGetSnapshot("d", out var s));
        Assert.Equal(1, s.Count);
    }
}
