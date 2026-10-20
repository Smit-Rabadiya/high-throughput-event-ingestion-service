using EventIngestion.Api.Endpoints;
using EventIngestion.Api.Hosting;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Options;

var builder = WebApplication.CreateBuilder(args);

// ~50,000 readings is roughly 5-15 MB of JSON; cap the body so a client cannot stream unbounded data.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 16 * 1024 * 1024);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(new WindowOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DeviceStore>();
builder.Services.AddHostedService<EvictionSweeper>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapReadingEndpoints();

app.Run();

public partial class Program;
