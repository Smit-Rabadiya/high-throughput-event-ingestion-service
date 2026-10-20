using EventIngestion.Api.Hosting;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace EventIngestion.Api.Documentation;

/// <summary>Interactive API documentation (configuration section "Docs"). On by default for local use, off in the Production profile.</summary>
public sealed class DocsOptions
{
    public bool Enabled { get; init; } = true;
}

/// <summary>Request-body shape, used only to document the POST body (the endpoint streams and parses it by hand).</summary>
public sealed record ReadingRequest(string DeviceId, DateTimeOffset Timestamp, double Value);

public static class OpenApiSetup
{
    public const string DocumentPath = "/openapi/v1.json";
    public const string UiPath = "/swagger";

    public static IServiceCollection AddApiDocs(this IServiceCollection services) =>
        services.AddOpenApi("v1", options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0; // widest tool support (Postman, Bruno, codegen)
            options.AddSchemaTransformer((schema, context, _) =>
            {
                // Describe what the parser actually accepts (it rejects numeric strings like "NaN", over-long ids, non-ISO dates).
                if (context.JsonTypeInfo.Type != typeof(ReadingRequest) || schema.Properties is null) return Task.CompletedTask;
                schema.Properties["deviceId"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String, MinLength = 1, MaxLength = 256, Description = "Case-sensitive device id, 1-256 characters.",
                    Examples = [JsonValue.Create("sensor-0042")],
                };
                schema.Properties["timestamp"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String, Format = "date-time",
                    Description = "ISO-8601, e.g. 2026-10-08T09:00:01Z (no Z/offset = UTC). Use a current time: readings 5 minutes or older are rejected as stale.",
                };
                schema.Properties["value"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Number, Format = "double", Description = "Finite sensor reading (JSON number; strings, NaN and Infinity are invalid).",
                    Examples = [JsonValue.Create(21.5)],
                };
                return Task.CompletedTask;
            });            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Event Ingestion Service",
                    Version = BuildInfo.Current.Version,
                    Description = """
                        High-throughput IoT ingestion: POST batches of up to 50,000 readings, query exact rolling 5-minute
                        count / min / max / average per device.

                        **Authentication** (when enabled, always in the Production profile): send `X-Api-Key`. Scopes: `ingest` for POST,
                        `query` for GETs. Probes (`/health`, `/health/ready`, `/version`) are anonymous. In local development auth is off by default.

                        **Timestamps** are ISO-8601 UTC; a value with no `Z`/offset is treated as UTC. Readings older than the window are `stale`,
                        more than 1 minute ahead are `future`; bad readings are skipped and counted as `invalid`.
                        """,
                };

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "X-Api-Key",
                    Description = "API key (required only when Auth:Enabled=true).",
                };
                document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", document)] = [] }];
                return Task.CompletedTask;
            });
        });

    /// <summary>Adds the optional Idempotency-Key header to an operation's documentation.</summary>
    public static RouteHandlerBuilder WithIdempotencyKeyHeader(this RouteHandlerBuilder builder) =>
        builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Idempotency-Key",
                In = ParameterLocation.Header,
                Required = false,
                Description = "Optional. 1-128 printable ASCII chars identifying this exact batch. Retrying with the same key returns the original "
                    + "result (header `Idempotent-Replayed: true`) and applies nothing twice. Remembered for 15 minutes by default.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 128 },
            });
            return Task.CompletedTask;
        });

    public static void MapApiDocs(this WebApplication app)
    {
        app.MapOpenApi(DocumentPath).ExcludeFromDescription();
        app.UseSwaggerUI(ui =>
        {
            ui.RoutePrefix = UiPath.TrimStart('/');
            ui.SwaggerEndpoint(DocumentPath, "Event Ingestion Service v1");
            ui.DocumentTitle = "Event Ingestion Service API";
            ui.EnablePersistAuthorization();
            ui.DisplayRequestDuration();
        });
    }
}


