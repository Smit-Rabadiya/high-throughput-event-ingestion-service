using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using EventIngestion.Api.Documentation;
using EventIngestion.Api.Durability;
using EventIngestion.Api.Endpoints;
using EventIngestion.Api.Hosting;
using EventIngestion.Api.Idempotency;
using EventIngestion.Api.Observability;
using EventIngestion.Api.Options;
using EventIngestion.Api.Security;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Configuration: appsettings.json -> appsettings.{Environment}.json -> environment variables (Section__Key) -> command line.
// Everything is validated here so a bad value stops start-up with a clear message instead of failing under load.
T Bind<T>(string section) where T : new() => builder.Configuration.GetSection(section).Get<T>() ?? new T();
var window = Bind<WindowOptions>("Window").Validate();
var service = Bind<ServiceOptions>("Service").Validate();
var idempotencyOptions = Bind<IdempotencyOptions>("Idempotency").Validate();
var auth = Bind<AuthOptions>("Auth").Validate();
var rate = Bind<RateLimitOptions>("RateLimiting").Validate();
var proxy = Bind<ProxyOptions>("Proxy").Validate();
var journalOptions = Bind<JournalOptions>("Journal").Validate();
var tls = Bind<TlsOptions>("Tls");
var docs = Bind<DocsOptions>("Docs");

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false; // do not advertise the server stack
    // Any HTTPS endpoint (Kestrel__Endpoints__Https__*) accepts TLS 1.2 and 1.3 only; no effect on plain-HTTP endpoints.
    k.ConfigureHttpsDefaults(h => h.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13);
    k.Limits.MaxRequestBodySize = service.MaxRequestBodyBytes;
});

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(window);
builder.Services.AddSingleton(service);
builder.Services.AddSingleton(idempotencyOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DeviceStore>();
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton<ReadinessState>();
builder.Services.AddSingleton<IngestMetrics>();
if (journalOptions.Enabled)
{
    // Registered before the sweeper and before the web server: recovery finishes before traffic is accepted.
    builder.Services.AddSingleton(journalOptions);
    builder.Services.AddSingleton<FileReadingJournal>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<FileReadingJournal>());
}

builder.Services.AddHostedService(sp => new EvictionSweeper(
    sp.GetRequiredService<DeviceStore>(), sp.GetRequiredService<ILogger<EvictionSweeper>>(), service.SweepInterval,
    sp.GetRequiredService<IdempotencyStore>(), sp.GetService<FileReadingJournal>()));

if (docs.Enabled) builder.Services.AddApiDocs();

if (auth.Enabled)
{
    builder.Services.AddSingleton(auth);
    builder.Services.AddSingleton<ApiKeyValidator>();
    builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(Scopes.Ingest, p => p.RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, Scopes.Ingest))
        .AddPolicy(Scopes.Query, p => p.RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, Scopes.Query));
}

if (rate.Enabled)
{
    // Partition by authenticated caller name, else by client IP (real IP only if Proxy is configured).
    static string Client(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.Name) ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.OnRejected = async (ctx, ct) =>
        {
            if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                ctx.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retry.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            else
                ctx.HttpContext.Response.Headers.RetryAfter = "1";
            await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests",
                detail: "Rate limit exceeded; honour Retry-After.").ExecuteAsync(ctx.HttpContext);
        };

        SlidingWindowRateLimiterOptions Sliding(int permits) => new()
        {
            PermitLimit = permits, Window = rate.Window, SegmentsPerWindow = 6, QueueLimit = 0,
        };
        o.AddPolicy("ingest", http => RateLimitPartition.GetSlidingWindowLimiter(Client(http), _ => Sliding(rate.IngestPermitsPerWindow)));
        o.AddPolicy("query", http => RateLimitPartition.GetSlidingWindowLimiter(Client(http), _ => Sliding(rate.QueryPermitsPerWindow)));

        // Global cap on batches being processed at once (all clients): bounds CPU and in-flight memory.
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            http.Request.Method == HttpMethods.Post && http.Request.Path.StartsWithSegments("/api/v1/readings")
                ? RateLimitPartition.GetConcurrencyLimiter("ingest-concurrency", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = rate.MaxConcurrentIngests, QueueLimit = rate.ConcurrentIngestQueue,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                })
                : RateLimitPartition.GetNoLimiter("none"));
    });
}

if (proxy.Enabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = 1;
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Clear();
        foreach (var ip in proxy.KnownProxies) o.KnownProxies.Add(IPAddress.Parse(ip));
        foreach (var net in proxy.KnownNetworks) o.KnownIPNetworks.Add(ProxyOptions.ParseNetwork(net));
    });
}

var app = builder.Build();

var readiness = app.Services.GetRequiredService<ReadinessState>();
app.Lifetime.ApplicationStarted.Register(readiness.MarkReady);
app.Lifetime.ApplicationStopping.Register(readiness.MarkNotReady);

var startupLog = app.Services.GetRequiredService<ILogger<Program>>();
StartupLog.Starting(startupLog, BuildInfo.Current.InformationalVersion, app.Environment.EnvironmentName);
if (!auth.Enabled) StartupLog.AuthDisabled(startupLog);
if (!rate.Enabled) StartupLog.RateLimitDisabled(startupLog);
if (!tls.RequireHttps && app.Environment.IsProduction()) StartupLog.HttpsNotEnforced(startupLog);

if (proxy.Enabled) app.UseForwardedHeaders();
if (tls.RequireHttps)
{
    app.Use(async (ctx, next) =>
    {
        bool probe = ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/version");
        if (ctx.Request.IsHttps || probe)
        {
            await next();
            return;
        }
        await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "HTTPS required",
            detail: "Send API calls over HTTPS.").ExecuteAsync(ctx);
    });
}
app.UseExceptionHandler();
if (auth.Enabled) app.UseAuthentication();
if (rate.Enabled) app.UseRateLimiter(); // after authentication so limits can key on the caller
if (auth.Enabled) app.UseAuthorization();
app.MapReadingEndpoints(auth.Enabled, rate.Enabled);
if (docs.Enabled) app.MapApiDocs();

app.Run();

public partial class Program;





