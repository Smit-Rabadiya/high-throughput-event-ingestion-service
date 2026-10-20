using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using EventIngestion.Api.Streaming;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Models;
using EventIngestion.Core.Options;

namespace EventIngestion.Benchmarks;

/// <summary>
/// Ingest of one maximum-size batch (50,000 readings over ~10,000 devices, slightly out of order), in the shapes
/// that matter: the streaming path against a naive "deserialize the whole list, then apply" baseline.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10, invocationCount: 16)]
public class IngestBenchmarks
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ReadingDto
    {
        public string? DeviceId { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public double Value { get; set; }
    }

    private const int BatchSize = 50_000;
    private const int Devices = 10_000;

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 5, 0, TimeSpan.Zero);
    private static readonly TimeProvider Clock = new FixedTime(Now);

    private byte[] _payload = [];
    private DeviceStore _warmStore = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            for (int i = 0; i < BatchSize; i++)
            {
                w.WriteStartObject();
                w.WriteString("deviceId", $"sensor-{rng.Next(Devices):0000}");
                w.WriteString("timestamp", Now.AddSeconds(-rng.Next(0, 240)));
                w.WriteNumber("value", Math.Round(rng.NextDouble() * 100, 3));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        _payload = ms.ToArray();

        _warmStore = NewStore();
        ParseStreaming(_warmStore); // all ~10,000 device windows exist before the query benchmark
    }

    [IterationSetup(Targets = [nameof(Streaming_WarmStore), nameof(Naive_DeserializeList_WarmStore)])]
    public void FreshWarmStore()
    {
        // Steady state: every device window already exists, and each iteration starts from the same one-batch state.
        _warmStore = NewStore();
        ParseStreaming(_warmStore);
    }

    private static DeviceStore NewStore() => new(new WindowOptions(), Clock);

    private IngestOutcome ParseStreaming(DeviceStore store) =>
        ReadingStreamParser.ParseAsync(PipeReader.Create(new ReadOnlySequence<byte>(_payload)), store).AsTask().GetAwaiter().GetResult();

    /// <summary>Production path, steady state. Device ids already known, so no per-reading string allocation.</summary>
    [Benchmark]
    public object Streaming_WarmStore() => ParseStreaming(_warmStore);

    /// <summary>Production path, first batch after start-up: includes creating ~10,000 device windows.</summary>
    [Benchmark]
    public object Streaming_ColdStore()
    {
        var store = NewStore();
        ParseStreaming(store);
        return store;
    }

    /// <summary>Baseline: deserialize the entire array into a List of objects, then apply it.</summary>
    [Benchmark(Baseline = true)]
    public object Naive_DeserializeList_ColdStore()
    {
        var store = NewStore();
        var list = JsonSerializer.Deserialize<List<ReadingDto>>(_payload, JsonSerializerOptions.Web)!;
        long now = store.NowUnixMs;
        foreach (var r in list)
            store.Add(r.DeviceId, r.Timestamp.ToUnixTimeMilliseconds(), r.Value, now);
        return store;
    }

    /// <summary>Same baseline but against a warm store, to isolate parse cost from device creation.</summary>
    [Benchmark]
    public object Naive_DeserializeList_WarmStore()
    {
        var list = JsonSerializer.Deserialize<List<ReadingDto>>(_payload, JsonSerializerOptions.Web)!;
        long now = _warmStore.NowUnixMs;
        foreach (var r in list)
            _warmStore.Add(r.DeviceId, r.Timestamp.ToUnixTimeMilliseconds(), r.Value, now);
        return _warmStore;
    }
}

/// <summary>Cost of an aggregate query against a populated store (should be O(1) per device).</summary>
[MemoryDiagnoser]
public class QueryBenchmarks
{
    private DeviceStore _store = null!;
    private long _now;

    [GlobalSetup]
    public void Setup()
    {
        var clock = new FixedClockProvider();
        _store = new DeviceStore(new WindowOptions(), clock);
        _now = _store.NowUnixMs;
        var rng = new Random(1);
        for (int i = 0; i < 500_000; i++)
            _store.Add("sensor-" + (i % 10_000), _now - rng.Next(0, 240_000), rng.NextDouble(), _now);
    }

    [Benchmark]
    public bool Query() => _store.Lookup("sensor-4242", _now, out _) == AggregateLookup.Found;

    private sealed class FixedClockProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 8, 9, 5, 0, TimeSpan.Zero);
    }
}



