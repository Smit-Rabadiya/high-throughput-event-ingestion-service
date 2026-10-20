using System.Globalization;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Models;
using EventIngestion.Core.Options;

namespace EventIngestion.Tests;

/// <summary>
/// Thread-safety guarantees from the brief (4.2): no lost updates, no torn reads, queries never see corrupt state.
/// Clock is passed explicitly so concurrency is the only variable; integer-valued doubles keep sums exact.
/// </summary>
public class ConcurrencyTests
{
    private const long Now = 1_800_000_000_000;
    private static DeviceStore NewStore() => new(new WindowOptions(), TimeProvider.System);

    private static void RunConcurrently(int workers, Action<int> body)
    {
        using var barrier = new Barrier(workers);
        var tasks = Enumerable.Range(0, workers).Select(w => Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait(); // start together to maximise contention
            body(w);
        }, TaskCreationOptions.LongRunning)).ToArray();
        Task.WaitAll(tasks);
    }

    [Fact]
    public void Parallel_ingest_into_one_device_loses_no_updates()
    {
        const int workers = 16, perWorker = 5_000;
        var store = NewStore();

        RunConcurrently(workers, w =>
        {
            for (int i = 0; i < perWorker; i++)
                Assert.Equal(AddResult.Accepted, store.Add("hot", Now - (i % 1000), w * perWorker + i, Now));
        });

        Assert.Equal(AggregateLookup.Found, store.Lookup("hot", Now, out var s));
        long n = (long)workers * perWorker;
        Assert.Equal(n, s.Count);
        Assert.Equal(0, s.Min);
        Assert.Equal(n - 1, s.Max);
        Assert.Equal((n - 1) / 2.0, s.Average, 6); // sum of 0..n-1 is exact in double
    }

    [Fact]
    public void Parallel_ingest_across_overlapping_devices_matches_a_sequential_oracle()
    {
        const int workers = 8, perWorker = 20_000, devices = 50;
        var store = NewStore();
        var expected = new (long Count, double Sum, double Min, double Max)[devices];
        for (int d = 0; d < devices; d++) expected[d] = (0, 0, double.MaxValue, double.MinValue);

        for (int w = 0; w < workers; w++)
            for (int i = 0; i < perWorker; i++)
            {
                int d = (w * 7 + i) % devices;
                double v = (w * 31 + i * 17) % 1000 - 500;
                var e = expected[d];
                expected[d] = (e.Count + 1, e.Sum + v, Math.Min(e.Min, v), Math.Max(e.Max, v));
            }

        RunConcurrently(workers, w =>
        {
            for (int i = 0; i < perWorker; i++)
            {
                int d = (w * 7 + i) % devices;
                double v = (w * 31 + i * 17) % 1000 - 500;
                // out-of-order timestamps on purpose
                store.Add("dev-" + d.ToString(CultureInfo.InvariantCulture), Now - ((i * 37) % 250_000), v, Now);
            }
        });

        for (int d = 0; d < devices; d++)
        {
            Assert.Equal(AggregateLookup.Found, store.Lookup("dev-" + d.ToString(CultureInfo.InvariantCulture), Now, out var s));
            Assert.Equal(expected[d].Count, s.Count);
            Assert.Equal(expected[d].Min, s.Min);
            Assert.Equal(expected[d].Max, s.Max);
            Assert.Equal(expected[d].Sum / expected[d].Count, s.Average, 9);
        }
        Assert.Equal(devices, store.DeviceCount);
    }

    [Fact]
    public async Task Queries_during_ingest_never_see_torn_or_corrupt_state()
    {
        const int writers = 6, readers = 4, perWriter = 30_000;
        var store = NewStore();
        store.Add("d", Now, 15, Now);
        int writersDone = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        using var barrier = new Barrier(writers + readers);
        var tasks = new List<Task>();
        for (int w = 0; w < writers; w++)
        {
            int id = w;
            tasks.Add(Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < perWriter; i++) store.Add("d", Now - (i % 5000), 10 + ((id + i) % 11), Now); // values in [10,20]
                Interlocked.Increment(ref writersDone);
            }, TaskCreationOptions.LongRunning));
        }
        for (int r = 0; r < readers; r++)
        {
            tasks.Add(Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                long lastCount = 0;
                while (Volatile.Read(ref writersDone) < writers)
                {
                    if (store.Lookup("d", Now, out var s) != AggregateLookup.Found) { failures.Add("missing"); continue; }
                    if (s.Min < 10 || s.Max > 20) failures.Add($"range {s}");
                    if (!(s.Min <= s.Average + 1e-9 && s.Average <= s.Max + 1e-9)) failures.Add($"order {s}");
                    if (s.Count < lastCount) failures.Add($"count went backwards {lastCount}->{s.Count}");
                    lastCount = s.Count;
                }
            }, TaskCreationOptions.LongRunning));
        }
        await Task.WhenAll(tasks);

        Assert.Empty(failures);
        Assert.True(store.Lookup("d", Now, out var final) == AggregateLookup.Found && final.Count == 1 + writers * perWriter);
    }

    [Fact]
    public async Task Sweeping_at_the_same_instant_never_removes_live_data()
    {
        const int workers = 8, perWorker = 20_000, devices = 200;
        var store = NewStore();
        bool stop = false;
        var sweeper = Task.Run(() => { while (!Volatile.Read(ref stop)) store.SweepIdle(Now); });

        RunConcurrently(workers, w =>
        {
            for (int i = 0; i < perWorker; i++)
                store.Add("d" + ((w + i) % devices).ToString(CultureInfo.InvariantCulture), Now, 1, Now);
        });
        Volatile.Write(ref stop, true);
        await sweeper;

        long total = 0;
        for (int d = 0; d < devices; d++)
            if (store.Lookup("d" + d.ToString(CultureInfo.InvariantCulture), Now, out var s) == AggregateLookup.Found) total += s.Count;
        Assert.Equal(workers * perWorker, total);
    }

    [Fact]
    public async Task Writers_racing_a_sweeper_that_retires_everything_never_fail_or_leak_retired_results()
    {
        const int workers = 8, perWorker = 20_000, devices = 20;
        var store = NewStore();
        long later = Now + 6 * 60_000; // the sweeper's clock is ahead: every window looks empty and gets retired
        bool stop = false;
        var sweeper = Task.Run(() => { while (!Volatile.Read(ref stop)) store.SweepIdle(later); });

        RunConcurrently(workers, w =>
        {
            for (int i = 0; i < perWorker; i++)
                Assert.Equal(AddResult.Accepted, store.Add("d" + ((w + i) % devices).ToString(CultureInfo.InvariantCulture), Now, 1, Now));
        });
        Volatile.Write(ref stop, true);
        await sweeper;

        store.SweepIdle(later);
        Assert.Equal(0, store.DeviceCount);
    }
}

