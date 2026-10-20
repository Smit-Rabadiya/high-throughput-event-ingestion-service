using EventIngestion.Api.Streaming;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Models;

namespace EventIngestion.Api.Endpoints;

public sealed record RejectedCounts(int Stale, int Future, int Invalid);

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
        return app;
    }

    private static async Task<IResult> IngestAsync(HttpContext http, DeviceStore store)
    {
        if (!http.Request.HasJsonContentType())
            return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported media type",
                detail: "Content-Type must be application/json.");

        IngestOutcome outcome;
        try
        {
            outcome = await ReadingStreamParser.ParseAsync(http.Request.BodyReader, store, http.RequestAborted);
        }
        catch (BadHttpRequestException ex) // Kestrel enforcing MaxRequestBodySize surfaces here as 413
        {
            return Results.Problem(statusCode: ex.StatusCode, title: "Request rejected", detail: ex.Message);
        }

        var r = outcome.Result;
        return outcome.Error switch
        {
            IngestError.None => Results.Ok(new IngestResponse(r.Accepted, new RejectedCounts(r.Stale, r.Future, r.Invalid), r.Total)),
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
                ["rejected"] = new RejectedCounts(r.Stale, r.Future, r.Invalid),
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


