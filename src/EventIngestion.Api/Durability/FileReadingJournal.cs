using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Threading.Channels;
using EventIngestion.Api.Options;
using EventIngestion.Api.Streaming;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Models;
using EventIngestion.Core.Options;

namespace EventIngestion.Api.Durability;

/// <summary>
/// Append-only write-ahead journal of accepted readings, so a restart rebuilds the sliding windows instead of starting empty.
/// <para>
/// Layout: segment files <c>journal-{startUnixSeconds}.log</c>, each a sequence of frames
/// <c>[int32 payloadLength][uint32 crc32][payload]</c>, one frame per accepted batch. A crash can only leave a torn final
/// frame; recovery verifies length and CRC and stops reading a segment at the first bad frame, keeping everything before it.
/// </para>
/// <para>
/// A single writer task owns the files. Requests hand it pooled buffers through a bounded channel (backpressure), and all
/// frames that arrive together share one fsync (group commit), so durability cost is per group, not per request.
/// Whole segments are deleted once nothing in them can still fall inside the window.
/// </para>
/// </summary>
public sealed partial class FileReadingJournal : IHostedService
{
    private const int HeaderSize = 8;
    private const int MaxFrameBytes = 128 * 1024 * 1024;
    private const int MaxIdBytes = 1024;

    private readonly record struct Item(byte[] Buffer, int Length, TaskCompletionSource? Done);

    private readonly JournalOptions _options;
    private readonly WindowOptions _window;
    private readonly DeviceStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<FileReadingJournal> _logger;
    private readonly Channel<Item> _channel;
    private readonly Counter<long> _bytesWritten;
    private readonly Counter<long> _errors;
    private readonly Counter<long> _recovered;
    private Task? _writer;

    public FileReadingJournal(JournalOptions options, WindowOptions window, DeviceStore store, TimeProvider time,
        IMeterFactory meters, ILogger<FileReadingJournal> logger)
    {
        _options = options;
        _window = window;
        _store = store;
        _time = time;
        _logger = logger;
        _channel = Channel.CreateBounded<Item>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var meter = meters.Create("EventIngestion");
        _bytesWritten = meter.CreateCounter<long>("journal.bytes_written", "By");
        _errors = meter.CreateCounter<long>("journal.errors", "{error}");
        _recovered = meter.CreateCounter<long>("journal.recovered_readings", "{reading}");
    }

    /// <summary>True when POSTs wait for their batch to be fsynced before being answered.</summary>
    public bool WaitsForFlush => _options.Durability == JournalDurability.Fsync;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.Directory);
        await Task.Run(() => Recover(cancellationToken), cancellationToken).ConfigureAwait(false); // before the server accepts traffic
        _writer = Task.Factory.StartNew(WriteLoopAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete(); // writer drains what is queued, fsyncs, closes
        if (_writer is not null) await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues the batch for the writer and, in <see cref="JournalDurability.Fsync"/> mode, waits until it is on disk. Takes ownership of the buffer.</summary>
    public async Task AppendAsync(JournalBatchBuffer batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            batch.Release();
            return;
        }

        var (buffer, length) = batch.Detach();
        var done = WaitsForFlush ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        try
        {
            await _channel.Writer.WriteAsync(new Item(buffer, length, done), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer); // never reached the writer
            throw;
        }

        if (done is not null) await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes segments that can no longer contain a reading inside the window.</summary>
    public int Prune(long nowUnixMs)
    {
        long keepMs = (long)(_options.SegmentDuration + _window.FutureSkewTolerance + _window.Window).TotalMilliseconds;
        int removed = 0;
        foreach (var file in Directory.EnumerateFiles(_options.Directory, "journal-*.log"))
        {
            if (!TryParseSegmentStart(file, out long startMs) || startMs + keepMs > nowUnixMs) continue;
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException ex)
            {
                LogPruneFailed(ex, file);
            }
        }
        return removed;
    }

    // ---- writer ----

    private async Task WriteLoopAsync()
    {
        var reader = _channel.Reader;
        FileStream? file = null;
        long segmentStartMs = -1;
        bool unsynced = false;
        var completions = new List<TaskCompletionSource>();

        try
        {
            while (true)
            {
                bool more;
                if (unsynced)
                {
                    using var timeout = new CancellationTokenSource(_options.FsyncInterval);
                    try
                    {
                        more = await reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Sync(file);
                        unsynced = false;
                        continue;
                    }
                }
                else
                {
                    more = await reader.WaitToReadAsync().ConfigureAwait(false);
                }
                if (!more) break;

                completions.Clear();
                try
                {
                    while (reader.TryRead(out var item))
                    {
                        try
                        {
                            if (item.Done is not null) completions.Add(item.Done); // before the write, so a failure faults it instead of hanging the request
                            file = EnsureSegment(file, ref segmentStartMs);
                            WriteFrame(file, item);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(item.Buffer);
                        }
                    }

                    file!.Flush(); // to the OS: survives a process crash
                    if (WaitsForFlush) { Sync(file); unsynced = false; }
                    else unsynced = true;
                    foreach (var c in completions) c.TrySetResult();
                }
                catch (Exception ex)
                {
                    _errors.Add(1);
                    LogWriteFailed(ex);
                    foreach (var c in completions) c.TrySetException(ex);
                    file?.Dispose(); // reopen a fresh segment on the next batch
                    file = null;
                    segmentStartMs = -1;
                    unsynced = false;
                }
            }
        }
        finally
        {
            try { Sync(file); } catch (IOException) { /* best effort at shutdown */ }
            file?.Dispose();
        }
    }

    private static void Sync(FileStream? file) => file?.Flush(flushToDisk: true);

    private FileStream EnsureSegment(FileStream? current, ref long segmentStartMs)
    {
        long segMs = (long)_options.SegmentDuration.TotalMilliseconds;
        long nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        long start = nowMs / segMs * segMs;
        if (current is not null && start == segmentStartMs) return current;

        current?.Dispose(); // flushes buffered data
        segmentStartMs = start;
        string path = Path.Combine(_options.Directory, $"journal-{start / 1000:D12}.log");
        return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.None);
    }

    private void WriteFrame(FileStream file, Item item)
    {
        var payload = item.Buffer.AsSpan(0, item.Length);
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header, item.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Crc32.HashToUInt32(payload));
        file.Write(header);
        file.Write(payload);
        _bytesWritten.Add(HeaderSize + item.Length);
    }

    // ---- recovery ----

    private void Recover(CancellationToken cancellationToken)
    {
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        long applied = 0, notLive = 0;
        int files = 0, damaged = 0;

        var segments = Directory.EnumerateFiles(_options.Directory, "journal-*.log")
            .Where(f => TryParseSegmentStart(f, out _))
            .OrderBy(f => f, StringComparer.Ordinal); // zero-padded names sort chronologically

        foreach (var path in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files++;
            try
            {
                if (!ReplaySegment(path, now, ref applied, ref notLive)) damaged++;
            }
            catch (IOException ex)
            {
                damaged++;
                LogSegmentUnreadable(ex, path);
            }
        }

        _recovered.Add(applied);
        LogRecovered(applied, notLive, files, damaged);
    }

    /// <returns>false when the segment ended in a torn or corrupt frame (everything before it was still applied).</returns>
    private bool ReplaySegment(string path, long now, ref long applied, ref long notLive)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[HeaderSize];
        while (true)
        {
            int got = stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false);
            if (got == 0) return true;           // clean end
            if (got < HeaderSize) return false;  // torn header

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (length <= 0 || length > MaxFrameBytes) return false;

            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                if (stream.ReadAtLeast(buffer.AsSpan(0, length), length, throwOnEndOfStream: false) < length) return false; // torn payload
                if (Crc32.HashToUInt32(buffer.AsSpan(0, length)) != crc) return false;
                if (!ApplyFrame(buffer.AsSpan(0, length), now, ref applied, ref notLive)) return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private bool ApplyFrame(ReadOnlySpan<byte> payload, long now, ref long applied, ref long notLive)
    {
        Span<char> id = stackalloc char[MaxIdBytes];
        while (!payload.IsEmpty)
        {
            if (payload.Length < 18) return false;
            long ts = BinaryPrimitives.ReadInt64LittleEndian(payload);
            double value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload[8..]));
            int idLen = BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]);
            if (idLen > MaxIdBytes || payload.Length < 18 + idLen) return false;

            int chars = Encoding.UTF8.GetChars(payload.Slice(18, idLen), id);
            if (_store.Add(id[..chars], ts, value, now) == AddResult.Accepted) applied++;
            else notLive++; // aged out of the window since it was written (or over the device limit)
            payload = payload[(18 + idLen)..];
        }
        return true;
    }

    private static bool TryParseSegmentStart(string path, out long startMs)
    {
        string name = Path.GetFileNameWithoutExtension(path); // journal-000001760000
        startMs = 0;
        if (!name.StartsWith("journal-", StringComparison.Ordinal)
            || !long.TryParse(name.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)) return false;
        startMs = seconds * 1000;
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Journal recovery: {Applied} readings restored, {NotLive} already outside the window, {Files} segments, {Damaged} damaged")]
    private partial void LogRecovered(long applied, long notLive, int files, int damaged);

    [LoggerMessage(Level = LogLevel.Error, Message = "Journal write failed; a new segment will be opened on the next batch")]
    private partial void LogWriteFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read journal segment {Path}")]
    private partial void LogSegmentUnreadable(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete journal segment {Path}")]
    private partial void LogPruneFailed(Exception ex, string path);
}

