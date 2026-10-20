namespace EventIngestion.Core.Models;

/// <summary>Per-batch outcome counts. Every element of the array lands in exactly one bucket.</summary>
public readonly record struct IngestResult(int Accepted, int Stale, int Future, int Invalid, int DeviceLimit = 0)
{
    public int Total => Accepted + Stale + Future + Invalid + DeviceLimit;
}
