using System.Net;

namespace EventIngestion.Api.Options;

public static class Scopes
{
    public const string Ingest = "ingest";
    public const string Query = "query";
}

public sealed class ApiKeyEntry
{
    /// <summary>Caller name, used for logs and as the rate-limit partition.</summary>
    public string Name { get; init; } = "";

    /// <summary>Plain key (supply via environment / secret store, never commit). Prefer <see cref="KeySha256"/>.</summary>
    public string? Key { get; init; }

    /// <summary>Hex SHA-256 of the key, so the secret itself never has to sit in configuration.</summary>
    public string? KeySha256 { get; init; }

    public string[] Scopes { get; init; } = [];
}

/// <summary>API-key authentication (configuration section "Auth"). Off by default; Production profile turns it on.</summary>
public sealed class AuthOptions
{
    public bool Enabled { get; init; }

    public List<ApiKeyEntry> Keys { get; init; } = [];

    public AuthOptions Validate()
    {
        if (!Enabled) return this;
        if (Keys.Count == 0) throw new InvalidOperationException("Auth is enabled but no keys are configured (Auth:Keys).");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in Keys)
        {
            if (string.IsNullOrWhiteSpace(k.Name) || !names.Add(k.Name)) throw new InvalidOperationException("Each Auth:Keys entry needs a unique Name.");
            if (k.Key is null == k.KeySha256 is null) throw new InvalidOperationException($"Auth key '{k.Name}': set exactly one of Key or KeySha256.");
            if (k.Key is { Length: < 24 }) throw new InvalidOperationException($"Auth key '{k.Name}' is shorter than 24 characters.");
            if (k.KeySha256 is not null && (k.KeySha256.Length != 64 || !k.KeySha256.All(Uri.IsHexDigit)))
                throw new InvalidOperationException($"Auth key '{k.Name}': KeySha256 must be 64 hex characters.");
            if (k.Scopes.Length == 0 || k.Scopes.Any(s => s is not (Options.Scopes.Ingest or Options.Scopes.Query)))
                throw new InvalidOperationException($"Auth key '{k.Name}': Scopes must be a non-empty subset of '{Options.Scopes.Ingest}', '{Options.Scopes.Query}'.");
        }
        return this;
    }
}

/// <summary>Per-client rate limits (configuration section "RateLimiting"). Rejections are HTTP 429 with Retry-After.</summary>
public sealed class RateLimitOptions
{
    public bool Enabled { get; init; }

    /// <summary>Batches (POSTs) per client per <see cref="Window"/>.</summary>
    public int IngestPermitsPerWindow { get; init; } = 600;

    /// <summary>Aggregate queries per client per <see cref="Window"/>.</summary>
    public int QueryPermitsPerWindow { get; init; } = 6000;

    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Batches processed at once across all clients: bounds CPU and in-flight memory. Excess waits in a small queue, then 429.</summary>
    public int MaxConcurrentIngests { get; init; } = 64;

    public int ConcurrentIngestQueue { get; init; } = 16;

    public RateLimitOptions Validate()
    {
        if (!Enabled) return this;
        if (IngestPermitsPerWindow <= 0 || QueryPermitsPerWindow <= 0 || MaxConcurrentIngests <= 0 || ConcurrentIngestQueue < 0)
            throw new InvalidOperationException("RateLimiting permits/concurrency must be positive.");
        if (Window < TimeSpan.FromSeconds(1)) throw new InvalidOperationException("RateLimiting:Window must be at least 1 second.");
        return this;
    }
}

/// <summary>Trusted reverse proxy / load balancer (configuration section "Proxy") so client IPs (rate-limit key) are real.</summary>
public sealed class ProxyOptions
{
    /// <summary>Honour X-Forwarded-For/Proto from the listed proxies only (never trust them blindly: it would let clients spoof their IP).</summary>
    public bool Enabled { get; init; }

    public string[] KnownProxies { get; init; } = [];

    /// <summary>CIDR ranges such as "10.0.0.0/8".</summary>
    public string[] KnownNetworks { get; init; } = [];

    public ProxyOptions Validate()
    {
        if (!Enabled) return this;
        if (KnownProxies.Length == 0 && KnownNetworks.Length == 0)
            throw new InvalidOperationException("Proxy is enabled but neither KnownProxies nor KnownNetworks is set.");
        foreach (var p in KnownProxies)
            if (!IPAddress.TryParse(p, out _)) throw new InvalidOperationException($"Proxy:KnownProxies contains an invalid IP '{p}'.");
        foreach (var n in KnownNetworks) ParseNetwork(n);
        return this;
    }

    public static System.Net.IPNetwork ParseNetwork(string cidr) =>
        System.Net.IPNetwork.TryParse(cidr, out var net) ? net : throw new InvalidOperationException($"Proxy:KnownNetworks contains an invalid CIDR '{cidr}'.");
}
