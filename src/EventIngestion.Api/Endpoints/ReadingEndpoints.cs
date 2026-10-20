using EventIngestion.Api.Hosting;
using EventIngestion.Api.Idempotency;
using EventIngestion.Api.Options;
using EventIngestion.Api.Observability;
using EventIngestion.Api.Streaming;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Models;

namespace EventIngestion.Api.Endpoints;

public sealed record RejectedCounts(int Stale, int Future, int Invalid, int DeviceLimit);

public sealed record IngestResponse(int Accepted, RejectedCounts Rejected, int Total);

public sealed record DeviceAggregatesResponse(
    string DeviceId, long Count, double Min, double Max, double Average, DateTimeOffset WindowStart, DateTimeOffset WindowEnd);

public static class ReadingEndpoints
{
    public static IEndpointRouteBuilder MapReadingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapPost("/readings", IngestAsync);
        api.MapGet("/devices/{deviceId}/aggregates", GetAggregates);
        // Same query for ids that cannot travel in a URL path segment (e.g. containing "/").
        api.MapGet("/devices/aggregates", (string? deviceId, DeviceStore store) =>
            string.IsNullOrEmpty(deviceId)
                ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Missing deviceId",
                    detail: "Provide the deviceId query parameter.")
                : GetAggregates(deviceId, store));
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/health/ready", (ReadinessState state) =>
            state.IsReady ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "not ready" }, statusCode: StatusCodes.Status503ServiceUnavailable));
        return app;
    }

    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    private const int MaxIdempotencyKeyLength = 128;

    private static async Task<IResult> IngestAsync(
        HttpContext http, DeviceStore store, IdempotencyStore idempotency, IdempotencyOptions idempotencyOptions,
        IngestMetrics metrics, ILogger<IngestLog> log)
    {
        if (!http.Request.HasJsonContentType())
            return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported media type",
                detail: "Content-Type must be application/json.");

        string? key = null;
        if (idempotencyOptions.Enabled && http.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var header))
        {
            key = header.Count == 1 ? header[0] : null;
            if (!IsValidKey(key))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid Idempotency-Key",
                    detail: $"Send exactly one {IdempotencyKeyHeader} of 1-{MaxIdempotencyKeyLength} printable ASCII characters.");

            switch (idempotency.Begin(key!, out var previous))
            {
                case BeginStatus.Replay:
                    http.Response.Headers[ReplayedHeader] = "true";
                    return Render(previous);
                case BeginStatus.InProgress:
                    http.Response.Headers.RetryAfter = "1";
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Request in progress",
                        detail: "A request with this Idempotency-Key is still being processed. Retry shortly.");
                case BeginStatus.Full:
                    http.Response.Headers.RetryAfter = "30";
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Idempotency store full",
                        detail: "Too many outstanding idempotency keys; retry later.");
            }
        }

        IngestOutcome outcome;
        try
        {
            outcome = await ReadingStreamParser.ParseAsync(http.Request.BodyReader, store, http.RequestAborted);
        }
        catch (BadHttpRequestException ex) // Kestrel enforcing MaxRequestBodySize surfaces here as 413
        {
            if (key is not null) idempotency.Abandon(key);
            return Results.Problem(statusCode: ex.StatusCode, title: "Request rejected", detail: ex.Message);
        }
        catch
        {
            if (key is not null) idempotency.Abandon(key); // client disconnected mid-batch: allow a retry
            throw;
        }

        if (key is not null) idempotency.Complete(key, outcome);

        var r = outcome.Result;
        metrics.Record(r, outcome.Error != IngestError.None);
        if (outcome.Error != IngestError.None) IngestLog.Stopped(log, outcome.Error, r.Accepted, r.Total);
        else IngestLog.Processed(log, r.Accepted, r.Stale, r.Future, r.Invalid, r.DeviceLimit);
        return Render(outcome);
    }

    private static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxIdempotencyKeyLength) return false;
        foreach (char c in key)
            if (c is < '!' or > '~') return false;
        return true;
    }

    private static IResult Render(IngestOutcome outcome)
    {
        var r = outcome.Result;
        return outcome.Error switch
        {
            IngestError.None => Results.Ok(new IngestResponse(r.Accepted, new RejectedCounts(r.Stale, r.Future, r.Invalid, r.DeviceLimit), r.Total)),
            IngestError.NotAnArray => Failure(StatusCodes.Status400BadRequest, "Body must be a JSON array of readings.", r),
            IngestError.TooManyReadings => Failure(StatusCodes.Status413PayloadTooLarge,
                $"A batch may contain at most {ReadingStreamParser.MaxBatchSize} readings.", r),
            _ => Failure(StatusCodes.Status400BadRequest, "Malformed or truncated JSON.", r),
        };
    }

    // Readings parsed before the failure stay applied; the counts tell the client exactly how many.
    private static IResult Failure(int status, string detail, IngestResult r) =>
        Results.Problem(statusCode: status, title: "Batch not fully processed",
            detail: $"{detail} Readings before the error were applied.",
            extensions: new Dictionary<string, object?>
            {
                ["accepted"] = r.Accepted,
                ["rejected"] = new RejectedCounts(r.Stale, r.Future, r.Invalid, r.DeviceLimit),
            });

    private static IResult GetAggregates(string deviceId, DeviceStore store)
    {
        long now = store.NowUnixMs;
        switch (store.Lookup(deviceId, now, out var s))
        {
            case AggregateLookup.Found:
                return Results.Ok(new DeviceAggregatesResponse(deviceId, s.Count, s.Min, s.Max, s.Average,
                    DateTimeOffset.FromUnixTimeMilliseconds(now) - store.Window, DateTimeOffset.FromUnixTimeMilliseconds(now)));
            case AggregateLookup.NoReadingsInWindow:
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No readings in window",
                    detail: $"Device '{deviceId}' has no readings in the last {store.Window.TotalMinutes:0.##} minutes.");
            default:
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown device",
                    detail: $"Device '{deviceId}' is not known.");
        }
    }
}







