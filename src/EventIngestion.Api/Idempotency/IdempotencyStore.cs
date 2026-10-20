using System.Collections.Concurrent;
using EventIngestion.Api.Options;
using EventIngestion.Api.Streaming;

namespace EventIngestion.Api.Idempotency;

public enum BeginStatus
{
    /// <summary>First time this key is seen: the caller must process the batch and then Complete or Abandon.</summary>
    Started,
    /// <summary>The same key is being processed right now by another request.</summary>
    InProgress,
    /// <summary>The key already completed: return the recorded outcome without applying anything.</summary>
    Replay,
    /// <summary>The store is at capacity.</summary>
    Full,
}

/// <summary>
/// Remembers the outcome of each Idempotency-Key for a TTL so a device that retries a batch (after a timeout,
/// restart or lost response) is answered from the record instead of being counted twice.
/// Granularity is the whole batch: the key must identify one exact batch (reusing a key for a different body returns
/// the first batch's outcome; the body is streamed and deliberately not hashed).
/// </summary>
public sealed class IdempotencyStore(IdempotencyOptions options, TimeProvider time)
{
    private sealed class Entry
    {
        public volatile bool Completed;
        public IngestOutcome Outcome; // written before Completed is set (volatile publish)
        public long ExpiresAtMs;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _count;
    private readonly long _ttlMs = (long)options.Ttl.TotalMilliseconds;

    public int Count => Volatile.Read(ref _count);

    private long NowMs => time.GetUtcNow().ToUnixTimeMilliseconds();

    public BeginStatus Begin(string key, out IngestOutcome outcome)
    {
        outcome = default;
        long now = NowMs;
        while (true)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (Volatile.Read(ref existing.ExpiresAtMs) <= now)
                {
                    Remove(key, existing); // expired: forget it and treat as new
                    continue;
                }
                if (!existing.Completed) return BeginStatus.InProgress;
                outcome = existing.Outcome;
                return BeginStatus.Replay;
            }

            if (Count >= options.MaxEntries) return BeginStatus.Full;

            var entry = new Entry { ExpiresAtMs = now + _ttlMs };
            if (_entries.TryAdd(key, entry))
            {
                Interlocked.Increment(ref _count);
                return BeginStatus.Started;
            }
        }
    }

    public void Complete(string key, IngestOutcome outcome)
    {
        if (!_entries.TryGetValue(key, out var entry)) return;
        entry.Outcome = outcome;
        Volatile.Write(ref entry.ExpiresAtMs, NowMs + _ttlMs); // TTL counts from completion
        entry.Completed = true;
    }

    /// <summary>Processing did not finish (cancelled / transport error): forget the key so the client can retry.</summary>
    public void Abandon(string key)
    {
        if (_entries.TryGetValue(key, out var entry)) Remove(key, entry);
    }

    /// <summary>Drops expired entries; returns how many.</summary>
    public int Sweep()
    {
        long now = NowMs;
        int removed = 0;
        foreach (var pair in _entries)
        {
            if (Volatile.Read(ref pair.Value.ExpiresAtMs) <= now && Remove(pair.Key, pair.Value)) removed++;
        }
        return removed;
    }

    private bool Remove(string key, Entry entry)
    {
        if (!_entries.TryRemove(new KeyValuePair<string, Entry>(key, entry))) return false;
        Interlocked.Decrement(ref _count);
        return true;
    }
}
