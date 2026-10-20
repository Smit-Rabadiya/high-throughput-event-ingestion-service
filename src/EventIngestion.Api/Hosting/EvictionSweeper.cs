using EventIngestion.Api.Durability;
using EventIngestion.Api.Idempotency;
using EventIngestion.Core.Aggregation;

namespace EventIngestion.Api.Hosting;

/// <summary>
/// Periodically drops devices whose windows are empty so memory stays bounded by active devices x
/// readings-in-window even when a device goes silent and is never queried again. Also expires idempotency keys.
/// </summary>
public sealed partial class EvictionSweeper(
    DeviceStore store, ILogger<EvictionSweeper> logger, TimeSpan? interval = null, IdempotencyStore? idempotency = null, FileReadingJournal? journal = null)
    : BackgroundService
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _interval = interval ?? DefaultInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                int removed = store.SweepIdle(store.NowUnixMs);
                int keys = idempotency?.Sweep() ?? 0;
                PruneJournal(store.NowUnixMs);
                if (removed > 0 || keys > 0) LogSwept(removed, store.DeviceCount, keys);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void PruneJournal(long nowUnixMs)
    {
        try
        {
            journal?.Prune(nowUnixMs);
        }
        catch (IOException ex)
        {
            LogPruneFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Journal pruning failed")]
    private partial void LogPruneFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Swept {Removed} idle devices ({Remaining} remain), {Keys} expired idempotency keys")]
    private partial void LogSwept(int removed, int remaining, int keys);
}

