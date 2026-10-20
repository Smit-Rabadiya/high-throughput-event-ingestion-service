using EventIngestion.Api.Hosting;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventIngestion.Tests;

public class EvictionSweeperTests
{
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task Background_service_eventually_drops_devices_that_went_silent()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
        var store = new DeviceStore(new WindowOptions(), time);
        for (int i = 0; i < 1000; i++) store.Add($"device-{i}", store.NowUnixMs, i, store.NowUnixMs);
        Assert.Equal(1000, store.DeviceCount);

        using var sweeper = new EvictionSweeper(store, NullLogger<EvictionSweeper>.Instance, TimeSpan.FromMilliseconds(20));
        await sweeper.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(6));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (store.DeviceCount > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await sweeper.StopAsync(CancellationToken.None);

        Assert.Equal(0, store.DeviceCount);
    }
}
