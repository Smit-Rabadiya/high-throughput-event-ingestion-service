namespace EventIngestion.Api.Options;

/// <summary>Idempotent retry support (configuration section "Idempotency").</summary>
public sealed class IdempotencyOptions
{
    /// <summary>When false the Idempotency-Key header is ignored.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How long a key (and its recorded outcome) is remembered. Must outlive the longest sensible device retry period.</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Memory bound. When full, requests that carry a key are refused with 503 rather than silently losing the guarantee.</summary>
    public int MaxEntries { get; init; } = 100_000;

    public IdempotencyOptions Validate()
    {
        if (Ttl <= TimeSpan.Zero) throw new InvalidOperationException("Idempotency:Ttl must be positive.");
        if (MaxEntries <= 0) throw new InvalidOperationException("Idempotency:MaxEntries must be positive.");
        return this;
    }
}
