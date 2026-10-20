using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EventIngestion.Core.Aggregation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventIngestion.Tests;

public sealed class ApiTests : IDisposable
{
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 8, 9, 5, 0, TimeSpan.Zero));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(_time))));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static StringContent Json(string body, string contentType = "application/json") =>
        new(body, Encoding.UTF8, contentType);

    private const string Sample = """
        [
          { "deviceId": "sensor-0042", "timestamp": "2026-10-08T09:04:01Z", "value": 21.5 },
          { "deviceId": "sensor-0042", "timestamp": "2026-10-08T09:04:03Z", "value": 22.5 }
        ]
        """;

    [Fact]
    public async Task Post_then_get_returns_aggregates()
    {
        var post = await _client.PostAsync("/api/v1/readings", Json(Sample));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var body = await post.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("accepted").GetInt32());
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        Assert.Equal(0, body.GetProperty("rejected").GetProperty("invalid").GetInt32());

        var get = await _client.GetAsync("/api/v1/devices/sensor-0042/aggregates");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var agg = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sensor-0042", agg.GetProperty("deviceId").GetString());
        Assert.Equal(2, agg.GetProperty("count").GetInt64());
        Assert.Equal(21.5, agg.GetProperty("min").GetDouble());
        Assert.Equal(22.5, agg.GetProperty("max").GetDouble());
        Assert.Equal(22.0, agg.GetProperty("average").GetDouble());
        Assert.Equal(TimeSpan.FromMinutes(5), agg.GetProperty("windowEnd").GetDateTimeOffset() - agg.GetProperty("windowStart").GetDateTimeOffset());
    }

    [Fact]
    public async Task Mixed_batch_reports_each_rejection_reason()
    {
        var json = """
            [
              { "deviceId": "a", "timestamp": "2026-10-08T09:04:01Z", "value": 1 },
              { "deviceId": "a", "timestamp": "2026-10-08T08:00:00Z", "value": 1 },
              { "deviceId": "a", "timestamp": "2026-10-08T11:00:00Z", "value": 1 },
              { "deviceId": "a", "timestamp": "nope", "value": 1 }
            ]
            """;
        var body = await (await _client.PostAsync("/api/v1/readings", Json(json))).Content.ReadFromJsonAsync<JsonElement>();
        var rejected = body.GetProperty("rejected");
        Assert.Equal((1, 1, 1, 1, 4), (body.GetProperty("accepted").GetInt32(), rejected.GetProperty("stale").GetInt32(),
            rejected.GetProperty("future").GetInt32(), rejected.GetProperty("invalid").GetInt32(), body.GetProperty("total").GetInt32()));
    }

    [Fact]
    public async Task Unknown_device_is_404_with_problem_details()
    {
        var res = await _client.GetAsync("/api/v1/devices/ghost/aggregates");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var p = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Unknown device", p.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Device_whose_readings_all_aged_out_is_404_with_distinct_message()
    {
        await _client.PostAsync("/api/v1/readings", Json(Sample));
        _time.Advance(TimeSpan.FromMinutes(6));

        var res = await _client.GetAsync("/api/v1/devices/sensor-0042/aggregates");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var p = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("No readings in window", p.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Url_encoded_device_id_is_decoded()
    {
        var json = """[{ "deviceId": "plant 1 line#2 é", "timestamp": "2026-10-08T09:04:01Z", "value": 3 }]""";
        await _client.PostAsync("/api/v1/readings", Json(json));
        var res = await _client.GetAsync("/api/v1/devices/" + Uri.EscapeDataString("plant 1 line#2 é") + "/aggregates");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Device_id_containing_slash_is_queryable_via_query_string()
    {
        await _client.PostAsync("/api/v1/readings", Json("""[{ "deviceId": "plant/line-2", "timestamp": "2026-10-08T09:04:01Z", "value": 3 }]"""));
        var ok = await _client.GetAsync("/api/v1/devices/aggregates?deviceId=" + Uri.EscapeDataString("plant/line-2"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/devices/aggregates")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/v1/devices/aggregates?deviceId=nope")).StatusCode);
    }

    [Fact]
    public async Task Wrong_content_type_is_415()
    {
        var res = await _client.PostAsync("/api/v1/readings", Json(Sample, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{ \"deviceId\": ")]
    [InlineData("")]
    public async Task Bad_body_is_400_problem_details(string body)
    {
        var res = await _client.PostAsync("/api/v1/readings", Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var p = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, p.GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task Partial_batch_before_syntax_error_reports_applied_count()
    {
        var res = await _client.PostAsync("/api/v1/readings",
            Json("""[{ "deviceId": "p", "timestamp": "2026-10-08T09:04:01Z", "value": 1 }, {oops"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var p = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, p.GetProperty("accepted").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/devices/p/aggregates")).StatusCode);
    }

    [Fact]
    public async Task More_than_50000_readings_is_413()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < 50_001; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("""{"deviceId":"d","timestamp":"2026-10-08T09:04:01Z","value":1}""");
        }
        sb.Append(']');

        var res = await _client.PostAsync("/api/v1/readings", Json(sb.ToString()));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Fact]
    public async Task Health_is_ok()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Sweeper_removes_idle_devices_in_the_running_host()
    {
        // The host runs the real EvictionSweeper (30 s default), so drive the same store method it calls.
        await _client.PostAsync("/api/v1/readings", Json(Sample));
        var store = _factory.Services.GetRequiredService<DeviceStore>();
        Assert.Equal(1, store.DeviceCount);
        _time.Advance(TimeSpan.FromMinutes(6));
        store.SweepIdle(store.NowUnixMs);
        Assert.Equal(0, store.DeviceCount);
    }
}


