using System.Diagnostics.Metrics;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Models;

namespace EventIngestion.Api.Observability;

/// <summary>
/// System.Diagnostics.Metrics instruments (no extra packages; scrape with OpenTelemetry / dotnet-counters).
/// Counters are updated once per batch, never per reading, so the hot path is untouched.
/// </summary>
public sealed class IngestMetrics
{
    public const string MeterName = "EventIngestion";

    private readonly Counter<long> _readings;
    private readonly Counter<long> _batches;
    private readonly Counter<long> _failedBatches;

    public IngestMetrics(IMeterFactory meterFactory, DeviceStore store)
    {
        var meter = meterFactory.Create(MeterName);
        _readings = meter.CreateCounter<long>("ingest.readings", "{reading}", "Readings by outcome");
        _batches = meter.CreateCounter<long>("ingest.batches", "{batch}", "Batches processed");
        _failedBatches = meter.CreateCounter<long>("ingest.batches.failed", "{batch}", "Batches that stopped early (400/413)");
        meter.CreateObservableGauge("devices.active", () => store.DeviceCount, "{device}", "Devices currently tracked in memory");
    }

    public void Record(IngestResult r, bool failed)
    {
        _batches.Add(1);
        if (failed) _failedBatches.Add(1);
        if (r.Accepted > 0) _readings.Add(r.Accepted, new KeyValuePair<string, object?>("outcome", "accepted"));
        if (r.Stale > 0) _readings.Add(r.Stale, new KeyValuePair<string, object?>("outcome", "stale"));
        if (r.Future > 0) _readings.Add(r.Future, new KeyValuePair<string, object?>("outcome", "future"));
        if (r.Invalid > 0) _readings.Add(r.Invalid, new KeyValuePair<string, object?>("outcome", "invalid"));
        if (r.DeviceLimit > 0) _readings.Add(r.DeviceLimit, new KeyValuePair<string, object?>("outcome", "device_limit"));
    }
}
