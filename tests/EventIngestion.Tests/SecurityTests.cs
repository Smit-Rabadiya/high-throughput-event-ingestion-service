using System.Net;
using System.Security.Cryptography;
using System.Text;
using EventIngestion.Api.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventIngestion.Tests;

public sealed class SecurityTests
{
    private const string IngestKey = "ingest-key-0123456789-abcdefghij";
    private const string QueryKey = "query-key-0123456789-abcdefghijk";

    private static WebApplicationFactory<Program> With(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });

    private static (string, string)[] AuthSettings() =>
    [
        ("Auth:Enabled", "true"),
        ("Auth:Keys:0:Name", "gateway"), ("Auth:Keys:0:Key", IngestKey), ("Auth:Keys:0:Scopes:0", "ingest"),
        ("Auth:Keys:1:Name", "dashboard"), ("Auth:Keys:1:KeySha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(QueryKey)))),
        ("Auth:Keys:1:Scopes:0", "query"),
    ];

    private static HttpRequestMessage Post(string? key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/readings") { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
        if (key is not null) req.Headers.Add("X-Api-Key", key);
        return req;
    }

    private static HttpRequestMessage Get(string? key)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/devices/x/aggregates");
        if (key is not null) req.Headers.Add("X-Api-Key", key);
        return req;
    }

    [Fact]
    public async Task Auth_is_off_by_default()
    {
        using var factory = With();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(null))).StatusCode);
    }

    [Fact]
    public async Task Missing_or_wrong_key_is_401_with_challenge()
    {
        using var factory = With(AuthSettings());
        using var client = factory.CreateClient();
        var none = await client.SendAsync(Post(null));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Contains("ApiKey", none.Headers.WwwAuthenticate.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post("wrong-key-wrong-key-wrong-key"))).StatusCode);
    }

    [Fact]
    public async Task Scopes_are_enforced_per_endpoint()
    {
        using var factory = With(AuthSettings());
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(IngestKey))).StatusCode);          // ingest key may ingest
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Post(QueryKey))).StatusCode);    // query key may not
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Get(QueryKey))).StatusCode);       // query key may query (404 = authorised, no data)
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get(IngestKey))).StatusCode);    // ingest key may not
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get(null))).StatusCode);
    }

    [Fact]
    public async Task Health_probes_stay_anonymous_when_auth_is_on()
    {
        using var factory = With(AuthSettings());
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public void Enabled_auth_without_keys_or_with_weak_keys_fails_fast()
    {
        using var none = With(("Auth:Enabled", "true"));
        Assert.ThrowsAny<Exception>(() => none.CreateClient());

        using var weak = With(("Auth:Enabled", "true"), ("Auth:Keys:0:Name", "a"), ("Auth:Keys:0:Key", "short"), ("Auth:Keys:0:Scopes:0", "ingest"));
        Assert.ThrowsAny<Exception>(() => weak.CreateClient());
    }

    [Fact]
    public async Task Rate_limit_returns_429_with_retry_after_per_client_and_leaves_probes_alone()
    {
        using var factory = With(("RateLimiting:Enabled", "true"), ("RateLimiting:IngestPermitsPerWindow", "2"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(null))).StatusCode);
        var limited = await client.SendAsync(Post(null));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);       // probes never throttled
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Get(null))).StatusCode); // queries have their own budget
    }

    [Fact]
    public async Task Rate_limits_are_per_authenticated_caller()
    {
        using var factory = With(AuthSettings().Concat([("RateLimiting:Enabled", "true"), ("RateLimiting:QueryPermitsPerWindow", "1")]).ToArray());
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Get(QueryKey))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get(QueryKey))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(IngestKey))).StatusCode); // another caller is unaffected
    }

    [Fact]
    public void Options_validation_rejects_bad_proxy_and_ratelimit_settings()
    {
        Assert.Throws<InvalidOperationException>(() => new ProxyOptions { Enabled = true }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ProxyOptions { Enabled = true, KnownNetworks = ["not-a-cidr"] }.Validate());
        Assert.Throws<InvalidOperationException>(() => new RateLimitOptions { Enabled = true, IngestPermitsPerWindow = 0 }.Validate());
        new ProxyOptions { Enabled = true, KnownNetworks = ["10.0.0.0/8"], KnownProxies = ["127.0.0.1"] }.Validate();
    }
}

public sealed class TlsTests
{
    [Fact]
    public async Task Require_https_blocks_plain_http_api_calls_but_not_probes_and_allows_https()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("Tls:RequireHttps", "true"));

        using var plain = factory.CreateClient(); // http://localhost
        var blocked = await plain.GetAsync("/api/v1/devices/x/aggregates");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsync("/api/v1/readings", new StringContent("[]", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await plain.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await plain.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await plain.GetAsync("/version")).StatusCode);

        using var secure = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.NotFound, (await secure.GetAsync("/api/v1/devices/x/aggregates")).StatusCode); // allowed through (no such device)
    }

    [Fact]
    public async Task Https_is_not_required_by_default()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/devices/x/aggregates")).StatusCode);
    }
}
