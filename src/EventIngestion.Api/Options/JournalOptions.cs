namespace EventIngestion.Api.Options;

public enum JournalDurability
{
    /// <summary>The POST is answered only after its batch is fsynced (group-committed with concurrent batches). Acknowledged means on disk.</summary>
    Fsync,
    /// <summary>The POST is answered immediately; data is flushed to the OS at once and fsynced every <see cref="JournalOptions.FsyncInterval"/>. A power loss can lose up to that interval.</summary>
    Buffered,
}

/// <summary>Write-ahead journal so a restart can rebuild the sliding windows (configuration section "Journal"). Off by default.</summary>
public sealed class JournalOptions
{
    public bool Enabled { get; init; }

    /// <summary>Directory for segment files. Mount a persistent volume here in containers.</summary>
    public string Directory { get; init; } = "data/journal";

    public JournalDurability Durability { get; init; } = JournalDurability.Fsync;

    public TimeSpan FsyncInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A new segment file is started this often; whole segments are deleted once nothing in them can still be in the window.</summary>
    public TimeSpan SegmentDuration { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Batches waiting for the writer. When full, requests wait (backpressure) instead of buffering unbounded memory.</summary>
    public int QueueCapacity { get; init; } = 256;

    public JournalOptions Validate()
    {
        if (!Enabled) return this;
        if (string.IsNullOrWhiteSpace(Directory)) throw new InvalidOperationException("Journal:Directory is required when the journal is enabled.");
        if (FsyncInterval < TimeSpan.FromMilliseconds(10)) throw new InvalidOperationException("Journal:FsyncInterval must be at least 10 ms.");
        if (SegmentDuration < TimeSpan.FromSeconds(10)) throw new InvalidOperationException("Journal:SegmentDuration must be at least 10 seconds.");
        if (QueueCapacity <= 0) throw new InvalidOperationException("Journal:QueueCapacity must be positive.");
        return this;
    }
}
