using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using EventIngestion.Api.Streaming;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Models;
using EventIngestion.Core.Options;

namespace EventIngestion.Tests;

public class ReadingStreamParserTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 5, 0, TimeSpan.Zero);

    private static DeviceStore NewStore() => new(new WindowOptions(), new FixedTime(Now));

    private static async Task<IngestOutcome> Parse(string json, DeviceStore store, int chunkSize = int.MaxValue)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var pipe = new Pipe();
        var writer = Task.Run(async () =>
        {
            for (int i = 0; i < bytes.Length; i += chunkSize)
            {
                await pipe.Writer.WriteAsync(bytes.AsMemory(i, Math.Min(chunkSize, bytes.Length - i)));
                await Task.Yield();
            }
            await pipe.Writer.CompleteAsync();
        });
        var outcome = await ReadingStreamParser.ParseAsync(pipe.Reader, store);
        await pipe.Reader.CompleteAsync();
        await writer;
        return outcome;
    }

    private static string Reading(string id, string ts, string value) =>
        $$"""{ "deviceId": "{{id}}", "timestamp": "{{ts}}", "value": {{value}} }""";

    private const string Ts1 = "2026-10-08T09:04:00Z";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(int.MaxValue)]
    public async Task Parses_identically_regardless_of_chunk_boundaries(int chunk)
    {
        var store = NewStore();
        var json = $"[ {Reading("sensor-0042", Ts1, "21.5")},\n {Reading("sensor-0042", "2026-10-08T09:04:30Z", "22.5")},{Reading("séñsor-é", Ts1, "-1e2")} ]";

        var outcome = await Parse(json, store, chunk);

        Assert.Equal(IngestError.None, outcome.Error);
        Assert.Equal(new IngestResult(3, 0, 0, 0), outcome.Result);
        Assert.True(store.TryGetSnapshot("sensor-0042", out var s));
        Assert.Equal(new AggregateSnapshot(2, 21.5, 22.5, 22.0), s);
        Assert.True(store.TryGetSnapshot("séñsor-é", out var u));
        Assert.Equal(-100, u.Min);
    }

    [Fact]
    public async Task Empty_array_is_accepted_with_zero()
    {
        var outcome = await Parse("[]", NewStore());
        Assert.Equal((IngestError.None, 0), (outcome.Error, outcome.Result.Total));
    }

    [Fact]
    public async Task Property_order_unknown_fields_and_nested_values_are_handled()
    {
        var store = NewStore();
        var json = """[ { "extra": {"a":[1,{"b":2}]}, "value": 3, "timestamp": "2026-10-08T09:04:00Z", "tags": [1,[2]], "deviceId": "d" } ]""";
        var outcome = await Parse(json, store, 5);
        Assert.Equal(new IngestResult(1, 0, 0, 0), outcome.Result);
        Assert.True(store.TryGetSnapshot("d", out var s));
        Assert.Equal(3, s.Max);
    }

    [Fact]
    public async Task Escaped_device_id_is_unescaped()
    {
        var store = NewStore();
        var outcome = await Parse("""[ { "deviceId": "a\"b\u00e9", "timestamp": "2026-10-08T09:04:00Z", "value": 1 } ]""", store, 3);
        Assert.Equal(1, outcome.Result.Accepted);
        Assert.True(store.TryGetSnapshot("a\"bé", out _));
    }

    [Fact]
    public async Task Timestamp_with_offset_is_normalised_to_utc()
    {
        var store = NewStore();
        // 11:04:00+02:00 == 09:04:00Z, inside the window
        var outcome = await Parse($"[{Reading("d", "2026-10-08T11:04:00+02:00", "1")}]", store);
        Assert.Equal(1, outcome.Result.Accepted);
    }

    [Fact]
    public async Task Stale_and_future_readings_are_counted_not_applied()
    {
        var store = NewStore();
        var json = $"[{Reading("d", "2026-10-08T08:00:00Z", "1")},{Reading("d", "2026-10-08T10:00:00Z", "1")},{Reading("d", Ts1, "1")}]";
        var outcome = await Parse(json, store);
        Assert.Equal(new IngestResult(1, 1, 1, 0), outcome.Result);
    }

    [Theory]
    [InlineData("""{ "deviceId": "d", "timestamp": "2026-10-08T09:04:00Z" }""")]                       // missing value
    [InlineData("""{ "timestamp": "2026-10-08T09:04:00Z", "value": 1 }""")]                            // missing id
    [InlineData("""{ "deviceId": "", "timestamp": "2026-10-08T09:04:00Z", "value": 1 }""")]            // empty id
    [InlineData("""{ "deviceId": null, "timestamp": "2026-10-08T09:04:00Z", "value": 1 }""")]          // null id
    [InlineData("""{ "deviceId": "d", "timestamp": "yesterday", "value": 1 }""")]                      // bad date
    [InlineData("""{ "deviceId": "d", "timestamp": 1760000000, "value": 1 }""")]                       // numeric date
    [InlineData("""{ "deviceId": "d", "timestamp": "2026-10-08T09:04:00Z", "value": "NaN" }""")]       // string value
    [InlineData("""{ "deviceId": "d", "timestamp": "2026-10-08T09:04:00Z", "value": 1e999 }""")]       // overflow
    [InlineData("""{ "deviceId": "d", "timestamp": "2026-10-08T09:04:00Z", "value": null }""")]
    [InlineData("""{ "deviceId": {"x":1}, "timestamp": "2026-10-08T09:04:00Z", "value": 1 }""")]       // wrong type
    [InlineData("42")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    public async Task Bad_readings_are_skipped_and_counted_invalid_without_blocking_good_ones(string bad)
    {
        var store = NewStore();
        var outcome = await Parse($"[{bad},{Reading("good", Ts1, "5")}]", store, 4);
        Assert.Equal(IngestError.None, outcome.Error);
        Assert.Equal(new IngestResult(1, 0, 0, 1), outcome.Result);
        Assert.True(store.TryGetSnapshot("good", out _));
    }

    [Fact]
    public async Task Device_id_longer_than_limit_is_invalid()
    {
        var outcome = await Parse($"[{Reading(new string('x', 257), Ts1, "1")},{Reading(new string('x', 256), Ts1, "1")}]", NewStore());
        Assert.Equal(new IngestResult(1, 0, 0, 1), outcome.Result);
    }

    [Fact]
    public async Task Huge_device_id_does_not_overflow_the_buffer()
    {
        var outcome = await Parse($"[{Reading(new string('é', 1_000_000), Ts1, "1")}]", NewStore());
        Assert.Equal(new IngestResult(0, 0, 0, 1), outcome.Result);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public async Task Non_array_root_is_rejected(string body)
    {
        var outcome = await Parse(body, NewStore());
        Assert.Equal(IngestError.NotAnArray, outcome.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[")]
    [InlineData("[{ \"deviceId\": \"d\", \"time")]
    [InlineData("[{ \"deviceId\": \"d\" ]")]
    [InlineData("[{\"a\":1},")]
    [InlineData("not json")]
    public async Task Malformed_or_truncated_json_is_reported(string body)
    {
        var outcome = await Parse(body, NewStore(), 3);
        Assert.Equal(IngestError.MalformedJson, outcome.Error);
    }

    [Fact]
    public async Task Readings_before_a_syntax_error_stay_applied_and_are_reported()
    {
        var store = NewStore();
        var outcome = await Parse($"[{Reading("d", Ts1, "1")},{Reading("d", Ts1, "2")}, {{ oops", store);
        Assert.Equal(IngestError.MalformedJson, outcome.Error);
        Assert.Equal(2, outcome.Result.Accepted);
        Assert.True(store.TryGetSnapshot("d", out var s));
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public async Task Exactly_50000_is_accepted_and_50001_stops_the_batch()
    {
        string Batch(int n)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(CultureInfo.InvariantCulture, $$"""{"deviceId":"d{{i % 100}}","timestamp":"2026-10-08T09:04:00Z","value":{{i}}}""");
            }
            return sb.Append(']').ToString();
        }

        var ok = await Parse(Batch(50_000), NewStore());
        Assert.Equal((IngestError.None, 50_000), (ok.Error, ok.Result.Accepted));

        var tooMany = await Parse(Batch(50_001), NewStore(), 4096);
        Assert.Equal(IngestError.TooManyReadings, tooMany.Error);
        Assert.Equal(50_000, tooMany.Result.Accepted);
    }

    [Fact]
    public async Task Duplicate_keys_last_one_wins()
    {
        var store = NewStore();
        await Parse("""[{"deviceId":"d","timestamp":"2026-10-08T09:04:00Z","value":1,"value":9}]""", store);
        Assert.True(store.TryGetSnapshot("d", out var s));
        Assert.Equal(9, s.Max);
    }
}

