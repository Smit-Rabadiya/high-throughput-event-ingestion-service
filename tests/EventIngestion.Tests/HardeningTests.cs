using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EventIngestion.Tests;

public sealed class HardeningTests
{
    private static WebApplicationFactory<Program> With(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });

    [Fact]
    public async Task Device_limit_from_configuration_is_enforced_and_reported()
    {
        using var factory = With(("Window:MaxDevices", "2"));
        using var client = factory.CreateClient();
        string ts = DateTimeOffset.UtcNow.ToString("o");
        var json = $$"""[{"deviceId":"a","timestamp":"{{ts}}","value":1},{"deviceId":"b","timestamp":"{{ts}}","value":1},{"deviceId":"c","timestamp":"{{ts}}","value":1}]""";

        var res = await client.PostAsync("/api/v1/readings", new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, body.GetProperty("accepted").GetInt32());
        Assert.Equal(1, body.GetProperty("rejected").GetProperty("deviceLimit").GetInt32());
        Assert.Equal(3, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public void Invalid_configuration_fails_fast_at_startup()
    {
        using var factory = With(("Window:MaxDevices", "0"));
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Readiness_is_up_once_started_and_liveness_is_independent()
    {
        using var factory = With();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
