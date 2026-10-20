namespace EventIngestion.Core.Options;

public sealed class WindowOptions
{
    /// <summary>Length of the sliding window.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Readings stamped further ahead of server time than this are rejected.</summary>
    public TimeSpan FutureSkewTolerance { get; init; } = TimeSpan.FromMinutes(1);

    internal long WindowMs => (long)Window.TotalMilliseconds;
    internal long FutureSkewMs => (long)FutureSkewTolerance.TotalMilliseconds;
}
