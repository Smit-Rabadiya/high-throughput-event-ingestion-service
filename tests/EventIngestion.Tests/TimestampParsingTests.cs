using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using EventIngestion.Api.Streaming;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Options;

namespace EventIngestion.Tests;

/// <summary>Timestamps are UTC per the brief; results must not depend on the server's time zone.</summary>
public class TimestampParsingTests
{
    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<(int Accepted, int Stale, int Future, int Invalid)> Parse(string ts)
    {
        var store = new DeviceStore(new WindowOptions(), new Fixed(new DateTimeOffset(2026, 10, 8, 9, 5, 0, TimeSpan.Zero)));
        var body = Encoding.UTF8.GetBytes($$"""[{"deviceId":"d","timestamp":"{{ts}}","value":1}]""");
        var o = await ReadingStreamParser.ParseAsync(PipeReader.Create(new ReadOnlySequence<byte>(body)), store);
        return (o.Result.Accepted, o.Result.Stale, o.Result.Future, o.Result.Invalid);
    }

    [Theory]
    [InlineData("2026-10-08T09:04:00")]              // no designator => UTC (was read as server-local time)
    [InlineData("2026-10-08T09:04:00Z")]
    [InlineData("2026-10-08T09:04:00.123456Z")]
    [InlineData("2026-10-08T09:04:00+00:00")]
    [InlineData("2026-10-08T11:04:00+02:00")]        // same instant, with an offset
    [InlineData("2026-10-08T03:34:00-05:30")]
    public async Task Timestamp_forms_denoting_the_same_utc_instant_are_all_accepted(string ts) =>
        Assert.Equal((1, 0, 0, 0), await Parse(ts));

    [Fact]
    public async Task No_designator_is_utc_not_local_so_a_stale_value_stays_stale() =>
        Assert.Equal((0, 1, 0, 0), await Parse("2026-10-08T03:34:00")); // 09:04 in IST, but UTC 03:34 is 90 minutes old

    [Theory]
    [InlineData("2026-10-08 09:04:00Z")]
    [InlineData("08/10/2026 09:04")]
    [InlineData("not a date")]
    [InlineData("2026-13-45T25:61:00Z")]
    public async Task Non_iso_timestamps_are_invalid(string ts) =>
        Assert.Equal((0, 0, 0, 1), (await Parse(ts)) is (var a, var s, var f, var i) ? (a, s, f, i) : default);
}
