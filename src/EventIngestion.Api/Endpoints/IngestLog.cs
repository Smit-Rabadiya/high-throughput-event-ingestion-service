using EventIngestion.Api.Streaming;

namespace EventIngestion.Api.Endpoints;

/// <summary>Logger category + source-generated (allocation-free when disabled) log messages for ingestion.</summary>
public sealed partial class IngestLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Batch stopped early: {Error} after {Accepted} accepted of {Total} read")]
    public static partial void Stopped(ILogger<IngestLog> logger, IngestError error, int accepted, int total);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Batch processed: {Accepted} accepted, {Stale} stale, {Future} future, {Invalid} invalid, {DeviceLimit} over device limit")]
    public static partial void Processed(ILogger<IngestLog> logger, int accepted, int stale, int future, int invalid, int deviceLimit);
}
