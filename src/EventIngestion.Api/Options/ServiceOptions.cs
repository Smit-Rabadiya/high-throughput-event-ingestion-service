namespace EventIngestion.Api.Options;

/// <summary>Host-level settings (configuration section "Service").</summary>
public sealed class ServiceOptions
{
    /// <summary>How often idle devices are swept out of memory.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Kestrel request body cap. 50,000 readings are ~5 MB typical, ~17 MB with 256-char ids.</summary>
    public long MaxRequestBodyBytes { get; init; } = 32L * 1024 * 1024;

    public ServiceOptions Validate()
    {
        if (SweepInterval <= TimeSpan.Zero) throw new InvalidOperationException("SweepInterval must be positive.");
        if (MaxRequestBodyBytes < 1024) throw new InvalidOperationException("MaxRequestBodyBytes is unreasonably small.");
        return this;
    }
}
