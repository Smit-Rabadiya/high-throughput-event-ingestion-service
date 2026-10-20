using EventIngestion.Api.Endpoints;
using EventIngestion.Api.Hosting;
using EventIngestion.Api.Idempotency;
using EventIngestion.Api.Observability;
using EventIngestion.Api.Options;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Options;

var builder = WebApplication.CreateBuilder(args);

// Configuration (appsettings.json, environment variables such as Window__MaxDevices, command line). Fail fast if invalid.
var window = (builder.Configuration.GetSection("Window").Get<WindowOptions>() ?? new WindowOptions()).Validate();
var idempotencyOptions = (builder.Configuration.GetSection("Idempotency").Get<IdempotencyOptions>() ?? new IdempotencyOptions()).Validate();
var service = (builder.Configuration.GetSection("Service").Get<ServiceOptions>() ?? new ServiceOptions()).Validate();

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = service.MaxRequestBodyBytes);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(window);
builder.Services.AddSingleton(service);
builder.Services.AddSingleton(idempotencyOptions);
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DeviceStore>();
builder.Services.AddSingleton<ReadinessState>();
builder.Services.AddSingleton<IngestMetrics>();
builder.Services.AddHostedService(sp => new EvictionSweeper(
    sp.GetRequiredService<DeviceStore>(), sp.GetRequiredService<ILogger<EvictionSweeper>>(), service.SweepInterval, sp.GetRequiredService<IdempotencyStore>()));

var app = builder.Build();

var readiness = app.Services.GetRequiredService<ReadinessState>();
app.Lifetime.ApplicationStarted.Register(readiness.MarkReady);
app.Lifetime.ApplicationStopping.Register(readiness.MarkNotReady);

app.UseExceptionHandler();
app.MapReadingEndpoints();

app.Run();

public partial class Program;

