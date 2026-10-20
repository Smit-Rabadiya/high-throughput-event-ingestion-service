using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Models;
using EventIngestion.Core.Options;

namespace EventIngestion.Tests;

public class DeviceWindowTests
{
    private const long Min5 = 5 * 60_000;
    private const long Now = 10_000_000;
    private static DeviceWindow New() => new(new WindowOptions());

    [Fact]
    public void Aggregates_basic_values()
    {
        var w = New();
        foreach (var (ts, v) in new[] { (Now - 3000, 10.0), (Now - 2000, 30.0), (Now - 1000, 20.0) })
            Assert.Equal(AddResult.Accepted, w.Add(ts, v, Now));

        Assert.True(w.TryGetSnapshot(Now, out var s));
        Assert.Equal(new AggregateSnapshot(3, 10, 30, 20), s);
    }

    [Fact]
    public void Reading_exactly_at_window_edge_is_stale_one_ms_inside_is_accepted()
    {
        var w = New();
        Assert.Equal(AddResult.Stale, w.Add(Now - Min5, 1, Now));
        Assert.Equal(AddResult.Accepted, w.Add(Now - Min5 + 1, 1, Now));
    }

    [Fact]
    public void Reading_ages_out_as_time_advances()
    {
        var w = New();
        w.Add(Now, 5, Now);
        Assert.True(w.TryGetSnapshot(Now + Min5 - 1, out _));
        Assert.False(w.TryGetSnapshot(Now + Min5, out _));
    }

    [Fact]
    public void Min_and_max_update_when_the_extreme_value_expires()
    {
        var w = New();
        w.Add(Now, 100, Now);          // max, oldest
        w.Add(Now + 1000, 50, Now + 1000);
        w.Add(Now + 2000, -5, Now + 2000); // min, newest

        Assert.True(w.TryGetSnapshot(Now + 2000, out var before));
        Assert.Equal((100.0, -5.0), (before.Max, before.Min));

        Assert.True(w.TryGetSnapshot(Now + Min5, out var after)); // the 100 has expired
        Assert.Equal((2L, 50.0, -5.0), (after.Count, after.Max, after.Min));
    }

    [Fact]
    public void Out_of_order_reading_inside_window_is_included_in_all_aggregates()
    {
        var w = New();
        w.Add(Now - 1000, 10, Now);
        w.Add(Now - 5000, 99, Now); // late
        w.Add(Now - 4000, -9, Now); // late

        Assert.True(w.TryGetSnapshot(Now, out var s));
        Assert.Equal(new AggregateSnapshot(3, -9, 99, 100.0 / 3), s);

        // the late 99 expires first, even though it arrived second
        Assert.True(w.TryGetSnapshot(Now - 5000 + Min5, out var later));
        Assert.Equal(2, later.Count);
        Assert.Equal(10, later.Max);
    }

    [Fact]
    public void Future_beyond_skew_is_rejected_within_skew_is_accepted()
    {
        var w = New();
        Assert.Equal(AddResult.Future, w.Add(Now + 60_001, 1, Now));
        Assert.Equal(AddResult.Accepted, w.Add(Now + 60_000, 1, Now));
    }

    [Fact]
    public void Equal_timestamps_and_duplicate_values_are_counted_separately()
    {
        var w = New();
        w.Add(Now, 7, Now);
        w.Add(Now, 7, Now);
        Assert.True(w.TryGetSnapshot(Now, out var s));
        Assert.Equal(new AggregateSnapshot(2, 7, 7, 7), s);
    }

    [Fact]
    public void Extreme_values_do_not_poison_the_average()
    {
        var w = New();
        w.Add(Now - 2000, double.MaxValue, Now);
        w.Add(Now - 1000, double.MaxValue, Now); // sum overflows to +Infinity
        w.Add(Now, 1, Now);
        Assert.True(w.TryGetSnapshot(Now - 1000 + Min5, out var s)); // both MaxValue readings expire, only the 1 is left
        Assert.Equal(new AggregateSnapshot(1, 1, 1, 1), s);
    }

    [Fact]
    public void Retired_window_rejects_writes_and_only_retires_when_empty()
    {
        var w = New();
        w.Add(Now, 1, Now);
        Assert.False(w.TryRetire(Now));
        Assert.True(w.TryRetire(Now + Min5));
        Assert.Equal(AddResult.Retired, w.Add(Now + Min5, 1, Now + Min5));
    }

    [Fact]
    public void Randomised_operations_match_a_brute_force_oracle()
    {
        var rng = new Random(12345);
        for (int round = 0; round < 200; round++)
        {
            var w = New();
            var oracle = new List<(long Ts, double V)>();
            long now = Now;

            for (int step = 0; step < 300; step++)
            {
                now += rng.Next(0, 30_000);
                if (rng.Next(4) != 0)
                {
                    long ts = now - rng.Next(0, 6 * 60_000); // sometimes stale
                    double v = rng.Next(-20, 20);            // small range -> many ties
                    var r = w.Add(ts, v, now);
                    Assert.Equal(ts <= now - Min5 ? AddResult.Stale : AddResult.Accepted, r);
                    if (r == AddResult.Accepted) oracle.Add((ts, v));
                }

                var live = oracle.Where(e => e.Ts > now - Min5).Select(e => e.V).ToList();
                bool has = w.TryGetSnapshot(now, out var s);
                Assert.Equal(live.Count > 0, has);
                if (has)
                {
                    Assert.Equal(live.Count, s.Count);
                    Assert.Equal(live.Min(), s.Min);
                    Assert.Equal(live.Max(), s.Max);
                    Assert.Equal(live.Average(), s.Average, 9);
                }
            }
        }
    }
}

