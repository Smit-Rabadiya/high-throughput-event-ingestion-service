using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EventIngestion.Api.Idempotency;
using EventIngestion.Api.Options;
using EventIngestion.Api.Streaming;
using EventIngestion.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventIngestion.Tests;

public class IdempotencyStoreTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly IngestOutcome Done = new(new IngestResult(3, 0, 0, 0), IngestError.None);

    [Fact]
    public void First_begin_starts_then_in_progress_then_replay_after_complete()
    {
        var store = new IdempotencyStore(new IdempotencyOptions(), new ManualTime());
        Assert.Equal(BeginStatus.Started, store.Begin("k", out _));
        Assert.Equal(BeginStatus.InProgress, store.Begin("k", out _));
        store.Complete("k", Done);
        Assert.Equal(BeginStatus.Replay, store.Begin("k", out var o));
        Assert.Equal(Done, o);
    }

    [Fact]
    public void Abandoned_key_can_be_retried()
    {
        var store = new IdempotencyStore(new IdempotencyOptions(), new ManualTime());
        store.Begin("k", out _);
        store.Abandon("k");
        Assert.Equal(0, store.Count);
        Assert.Equal(BeginStatus.Started, store.Begin("k", out _));
    }

    [Fact]
    public void Keys_expire_after_ttl_counted_from_completion()
    {
        var time = new ManualTime();
        var store = new IdempotencyStore(new IdempotencyOptions { Ttl = TimeSpan.FromMinutes(10) }, time);
        store.Begin("k", out _);
        time.Advance(TimeSpan.FromMinutes(9));
        store.Complete("k", Done);
        time.Advance(TimeSpan.FromMinutes(9)); // 18 min after begin, 9 after completion
        Assert.Equal(BeginStatus.Replay, store.Begin("k", out _));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, store.Sweep());
        Assert.Equal(BeginStatus.Started, store.Begin("k", out _));
    }

    [Fact]
    public void Full_store_refuses_new_keys_but_still_replays_known_ones()
    {
        var store = new IdempotencyStore(new IdempotencyOptions { MaxEntries = 1 }, new ManualTime());
        store.Begin("a", out _);
        store.Complete("a", Done);
        Assert.Equal(BeginStatus.Full, store.Begin("b", out _));
        Assert.Equal(BeginStatus.Replay, store.Begin("a", out _));
    }

    [Fact]
    public void Only_one_of_many_concurrent_requests_with_the_same_key_starts()
    {
        var store = new IdempotencyStore(new IdempotencyOptions(), new ManualTime());
        int started = 0;
        Parallel.For(0, 64, i =>
        {
            if (store.Begin("same", out _) == BeginStatus.Started) Interlocked.Increment(ref started);
        });
        Assert.Equal(1, started);
    }
}

public sealed class IdempotencyApiTests
{
    private static readonly string Body =
        $$"""[{"deviceId":"idem","timestamp":"{{DateTimeOffset.UtcNow:o}}","value":5},{"deviceId":"idem","timestamp":"{{DateTimeOffset.UtcNow:o}}","value":7}]""";

    private static HttpRequestMessage Post(string? key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/readings") { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        if (key is not null) req.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return req;
    }

    [Fact]
    public async Task Retry_with_same_key_is_not_double_counted_and_returns_the_original_result()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var first = await client.SendAsync(Post("batch-1"));
        var retry = await client.SendAsync(Post("batch-1"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(2, (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());

        var agg = await (await client.GetAsync("/api/v1/devices/idem/aggregates")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, agg.GetProperty("count").GetInt64()); // not 4
    }

    [Fact]
    public async Task Without_a_key_every_post_is_applied()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await client.SendAsync(Post(null));
        await client.SendAsync(Post(null));
        var agg = await (await client.GetAsync("/api/v1/devices/idem/aggregates")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, agg.GetProperty("count").GetInt64());
    }

    [Theory]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789")]
    [InlineData("has space")]
    public async Task Malformed_key_is_400(string key)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Post(key))).StatusCode);
    }

    [Fact]
    public async Task Different_keys_are_independent()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await client.SendAsync(Post("a"));
        var second = await client.SendAsync(Post("b"));
        Assert.False(second.Headers.Contains("Idempotent-Replayed"));
        var agg = await (await client.GetAsync("/api/v1/devices/idem/aggregates")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, agg.GetProperty("count").GetInt64());
    }
}


