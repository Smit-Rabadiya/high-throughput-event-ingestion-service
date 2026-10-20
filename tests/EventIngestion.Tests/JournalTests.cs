using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EventIngestion.Api.Durability;
using EventIngestion.Api.Options;
using EventIngestion.Api.Streaming;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventIngestion.Tests;

public sealed class JournalTests : IDisposable
{
    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);
        public void Dispose() { }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ei-journal-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 8, 9, 5, 0, TimeSpan.Zero));
    private readonly WindowOptions _window = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp cleanup is best effort */ }
    }

    private (FileReadingJournal Journal, DeviceStore Store) NewJournal(JournalDurability mode = JournalDurability.Fsync, TimeSpan? segment = null)
    {
        var store = new DeviceStore(_window, _time);
        var options = new JournalOptions { Enabled = true, Directory = _dir, Durability = mode, SegmentDuration = segment ?? TimeSpan.FromMinutes(1) };
        return (new FileReadingJournal(options, _window, store, _time, new TestMeterFactory(), NullLogger<FileReadingJournal>.Instance), store);
    }

    private static JournalBatchBuffer Batch(long now, string device, params double[] values)
    {
        var b = new JournalBatchBuffer();
        for (int i = 0; i < values.Length; i++) b.Record(device, now - 1000 * (values.Length - i), values[i]);
        return b;
    }

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private string[] Segments() => Directory.GetFiles(_dir, "journal-*.log").OrderBy(f => f, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Restart_restores_the_window_from_the_journal()
    {
        var (j1, _) = NewJournal();
        await j1.StartAsync(default);
        await j1.AppendAsync(Batch(Now, "sensor-é", 10, 20, 30), default);
        await j1.AppendAsync(Batch(Now, "other", 5), default);
        await j1.StopAsync(default);

        var (j2, store2) = NewJournal(); // "restarted" process: empty memory
        await j2.StartAsync(default);

        Assert.True(store2.TryGetSnapshot("sensor-é", out var s));
        Assert.Equal((3L, 10.0, 30.0, 20.0), (s.Count, s.Min, s.Max, s.Average));
        Assert.True(store2.TryGetSnapshot("other", out var o));
        Assert.Equal(1, o.Count);
        await j2.StopAsync(default);
    }

    [Fact]
    public async Task Readings_that_aged_out_during_the_outage_are_not_restored()
    {
        var (j1, _) = NewJournal();
        await j1.StartAsync(default);
        await j1.AppendAsync(Batch(Now, "old", 1, 2), default);
        _time.Advance(TimeSpan.FromMinutes(3));
        await j1.AppendAsync(Batch(Now, "fresh", 9), default);
        await j1.StopAsync(default);

        _time.Advance(TimeSpan.FromMinutes(3)); // "old" is now 6 min old, "fresh" 3 min
        var (j2, store2) = NewJournal();
        await j2.StartAsync(default);

        Assert.False(store2.TryGetSnapshot("old", out _));
        Assert.True(store2.TryGetSnapshot("fresh", out _));
        await j2.StopAsync(default);
    }

    [Fact]
    public async Task Torn_final_frame_is_ignored_and_earlier_frames_survive()
    {
        var (j1, _) = NewJournal();
        await j1.StartAsync(default);
        await j1.AppendAsync(Batch(Now, "a", 1, 2), default);
        await j1.AppendAsync(Batch(Now, "b", 7, 8, 9), default);
        await j1.StopAsync(default);

        var file = Segments().Single();
        await using (var fs = new FileStream(file, FileMode.Open, FileAccess.Write)) fs.SetLength(fs.Length - 5); // simulate a crash mid-write

        var (j2, store2) = NewJournal();
        await j2.StartAsync(default);
        Assert.True(store2.TryGetSnapshot("a", out var a));
        Assert.Equal(2, a.Count);
        Assert.False(store2.TryGetSnapshot("b", out _));
        await j2.StopAsync(default);
    }

    [Fact]
    public async Task Corrupt_frame_stops_replay_of_that_segment_without_crashing()
    {
        var (j1, _) = NewJournal();
        await j1.StartAsync(default);
        await j1.AppendAsync(Batch(Now, "a", 1), default);
        await j1.AppendAsync(Batch(Now, "b", 2), default);
        await j1.StopAsync(default);

        var file = Segments().Single();
        var bytes = await File.ReadAllBytesAsync(file);
        bytes[^3] ^= 0xFF; // flip a bit inside the last frame's payload
        await File.WriteAllBytesAsync(file, bytes);

        var (j2, store2) = NewJournal();
        await j2.StartAsync(default);
        Assert.True(store2.TryGetSnapshot("a", out _));
        Assert.False(store2.TryGetSnapshot("b", out _));
        await j2.StopAsync(default);
    }

    [Fact]
    public async Task Garbage_and_empty_files_do_not_prevent_start_up()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "journal-000000000001.log"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "journal-000000000002.log"), []);
        await File.WriteAllTextAsync(Path.Combine(_dir, "unrelated.txt"), "ignore me");
        var (j, store) = NewJournal();
        await j.StartAsync(default);
        Assert.Equal(0, store.DeviceCount);
        await j.StopAsync(default);
    }

    [Fact]
    public async Task Prune_removes_only_segments_that_can_no_longer_matter()
    {
        var (j, _) = NewJournal();
        await j.StartAsync(default);
        await j.AppendAsync(Batch(Now, "a", 1), default);
        _time.Advance(TimeSpan.FromMinutes(3));
        await j.AppendAsync(Batch(Now, "b", 1), default);
        Assert.Equal(2, Segments().Length);

        Assert.Equal(0, j.Prune(Now));                                 // both still relevant
        _time.Advance(TimeSpan.FromMinutes(5));                        // first segment is > window + skew + segment old
        Assert.Equal(1, j.Prune(Now));
        Assert.Single(Segments());
        await j.StopAsync(default);
    }

    [Fact]
    public async Task Buffered_mode_does_not_wait_for_the_disk_but_loses_nothing_on_clean_shutdown()
    {
        var (j1, _) = NewJournal(JournalDurability.Buffered);
        await j1.StartAsync(default);
        for (int i = 0; i < 20; i++) await j1.AppendAsync(Batch(Now, "d", i), default);
        await j1.StopAsync(default);

        var (j2, store2) = NewJournal();
        await j2.StartAsync(default);
        Assert.True(store2.TryGetSnapshot("d", out var s));
        Assert.Equal(20, s.Count);
        await j2.StopAsync(default);
    }

    [Fact]
    public async Task Many_concurrent_batches_are_group_committed_and_all_recovered()
    {
        var (j1, _) = NewJournal();
        await j1.StartAsync(default);
        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => j1.AppendAsync(Batch(Now, "dev" + (i % 10), i, i + 1), default)));
        await j1.StopAsync(default);

        var (j2, store2) = NewJournal();
        await j2.StartAsync(default);
        long total = 0;
        for (int d = 0; d < 10; d++)
        {
            Assert.True(store2.TryGetSnapshot("dev" + d, out var s));
            total += s.Count;
        }
        Assert.Equal(400, total);
        await j2.StopAsync(default);
    }

    [Fact]
    public async Task A_write_failure_faults_the_request_instead_of_hanging()
    {
        var (j, _) = NewJournal();
        await j.StartAsync(default);
        Directory.Delete(_dir, recursive: true);
        await File.WriteAllTextAsync(_dir, "now a file, so segments cannot be created");

        await Assert.ThrowsAnyAsync<Exception>(() => j.AppendAsync(Batch(Now, "x", 1), default).WaitAsync(TimeSpan.FromSeconds(10)));
        File.Delete(_dir);
        await j.StopAsync(default);
    }

    [Fact]
    public async Task Service_restart_end_to_end_keeps_aggregates_when_the_journal_is_enabled()
    {
        (string, string)[] cfg = [("Journal:Enabled", "true"), ("Journal:Directory", _dir)];
        WebApplicationFactory<Program> Make() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in cfg) b.UseSetting(k, v);
        });

        string ts = DateTimeOffset.UtcNow.ToString("o");
        using (var first = Make())
        using (var client = first.CreateClient())
        {
            var post = await client.PostAsync("/api/v1/readings", new StringContent(
                $$"""[{"deviceId":"persist","timestamp":"{{ts}}","value":4},{"deviceId":"persist","timestamp":"{{ts}}","value":6}]""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        } // host stops here: "service restart"

        using var second = Make();
        using var client2 = second.CreateClient();
        var res = await client2.GetAsync("/api/v1/devices/persist/aggregates");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("count").GetInt64());
        Assert.Equal(5.0, body.GetProperty("average").GetDouble());
    }

    [Fact]
    public void Journal_options_are_validated()
    {
        Assert.Throws<InvalidOperationException>(() => new JournalOptions { Enabled = true, Directory = " " }.Validate());
        Assert.Throws<InvalidOperationException>(() => new JournalOptions { Enabled = true, SegmentDuration = TimeSpan.FromSeconds(1) }.Validate());
        new JournalOptions().Validate(); // disabled: nothing to validate
    }
}
