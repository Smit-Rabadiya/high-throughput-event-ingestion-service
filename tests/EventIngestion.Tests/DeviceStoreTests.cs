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

public class DeviceLimitTests
{
    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void New_devices_beyond_the_limit_are_refused_but_known_devices_keep_working()
    {
        var store = new DeviceStore(new WindowOptions { MaxDevices = 3 }, new Fixed(DateTimeOffset.UnixEpoch.AddDays(20000)));
        long now = store.NowUnixMs;
        for (int i = 0; i < 3; i++) Assert.Equal(AddResult.Accepted, store.Add("d" + i, now, 1, now));

        Assert.Equal(AddResult.DeviceLimit, store.Add("d3", now, 1, now));
        Assert.Equal(AddResult.Accepted, store.Add("d1", now, 2, now)); // existing device unaffected
        Assert.Equal(3, store.DeviceCount);
    }

    [Fact]
    public void Stale_or_future_readings_for_unknown_devices_do_not_consume_capacity()
    {
        var store = new DeviceStore(new WindowOptions { MaxDevices = 1 }, new Fixed(DateTimeOffset.UnixEpoch.AddDays(20000)));
        long now = store.NowUnixMs;
        Assert.Equal(AddResult.Stale, store.Add("old", now - 10 * 60_000, 1, now));
        Assert.Equal(AddResult.Future, store.Add("future", now + 10 * 60_000, 1, now));
        Assert.Equal(0, store.DeviceCount);
        Assert.Equal(AddResult.Accepted, store.Add("real", now, 1, now));
    }

    [Fact]
    public void Freed_capacity_is_reusable_after_sweep()
    {
        var store = new DeviceStore(new WindowOptions { MaxDevices = 1 }, new Fixed(DateTimeOffset.UnixEpoch.AddDays(20000)));
        long now = store.NowUnixMs;
        store.Add("a", now, 1, now);
        Assert.Equal(1, store.SweepIdle(now + 6 * 60_000));
        Assert.Equal(0, store.DeviceCount);
        Assert.Equal(AddResult.Accepted, store.Add("b", now, 1, now));
    }
}
