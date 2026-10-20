namespace EventIngestion.Core.Options;

public sealed class WindowOptions
{
    /// <summary>Length of the sliding window.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Readings stamped further ahead of server time than this are rejected.</summary>
    public TimeSpan FutureSkewTolerance { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Upper bound on tracked devices (the brief expects ~10,000). Readings for new devices beyond it are rejected
    /// so a client cannot exhaust memory with unbounded distinct ids. Soft limit: concurrent first-sightings may overshoot slightly.
    /// </summary>
    public int MaxDevices { get; init; } = 100_000;

    /// <summary>Fail fast at start-up on nonsensical configuration.</summary>
    public WindowOptions Validate()
    {
        if (Window <= TimeSpan.Zero) throw new InvalidOperationException("Window must be positive.");
        if (FutureSkewTolerance < TimeSpan.Zero) throw new InvalidOperationException("FutureSkewTolerance must not be negative.");
        if (MaxDevices <= 0) throw new InvalidOperationException("MaxDevices must be positive.");
        return this;
    }

    internal long WindowMs => (long)Window.TotalMilliseconds;
    internal long FutureSkewMs => (long)FutureSkewTolerance.TotalMilliseconds;
}

