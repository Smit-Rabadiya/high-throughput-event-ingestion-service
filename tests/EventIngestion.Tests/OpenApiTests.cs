using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventIngestion.Tests;

public sealed class OpenApiTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EventIngestion.slnx"))) dir = dir.Parent;
        return Path.Combine([dir!.FullName, .. parts]);
    }

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static async Task<string> FetchSpec()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        return await client.GetStringAsync("/openapi/v1.json");
    }

    [Fact]
    public async Task Spec_documents_every_endpoint_response_and_the_api_key_scheme()
    {
        using var doc = JsonDocument.Parse(await FetchSpec());
        var root = doc.RootElement;

        Assert.StartsWith("3.0", root.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        Assert.Equal("1.0.0", root.GetProperty("info").GetProperty("version").GetString());

        var paths = root.GetProperty("paths");
        foreach (var path in new[] { "/api/v1/readings", "/api/v1/devices/{deviceId}/aggregates", "/api/v1/devices/aggregates", "/health", "/health/ready", "/version" })
            Assert.True(paths.TryGetProperty(path, out _), $"missing path {path}");

        var post = paths.GetProperty("/api/v1/readings").GetProperty("post");
        var codes = post.GetProperty("responses").EnumerateObject().Select(r => r.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "200", "400", "401", "403", "409", "413", "415", "429", "503" }, codes);
        Assert.Contains(post.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.Equal("array", post.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("type").GetString());

        var scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("X-Api-Key", scheme.GetProperty("name").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
    }

    [Fact]
    public async Task Committed_openapi_file_matches_the_generated_spec()
    {
        string generated = JsonSerializer.Serialize(JsonDocument.Parse(await FetchSpec()).RootElement, Pretty);
        string file = RepoFile("docs", "api", "openapi.json");

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1") await File.WriteAllTextAsync(file, generated + "\n");

        Assert.True(File.Exists(file), "docs/api/openapi.json missing: run tests once with UPDATE_OPENAPI=1");
        string committed = JsonSerializer.Serialize(JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement, Pretty);
        Assert.True(committed == generated, "docs/api/openapi.json is out of date: re-run tests with UPDATE_OPENAPI=1 and commit it.");
    }

    [Fact]
    public async Task Swagger_ui_is_served_when_docs_are_enabled()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/swagger/index.html");
        Assert.Contains("swagger-ui", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Docs_can_be_switched_off_and_then_neither_spec_nor_ui_exist()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Docs:Enabled", "false"));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode); // API itself unaffected
    }

    [Fact]
    public async Task Docs_are_anonymous_even_when_auth_is_on()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:Enabled", "true");
            b.UseSetting("Auth:Keys:0:Name", "k");
            b.UseSetting("Auth:Keys:0:Key", "0123456789-0123456789-0123456789");
            b.UseSetting("Auth:Keys:0:Scopes:0", "query");
        });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/devices/x/aggregates")).StatusCode);
    }
}

