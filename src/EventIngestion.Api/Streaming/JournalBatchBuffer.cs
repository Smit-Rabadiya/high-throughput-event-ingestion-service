using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace EventIngestion.Api.Streaming;

/// <summary>Receives each reading the moment it is accepted into the window (used by the journal). Called on the hot path: keep it allocation-free.</summary>
public abstract class ReadingRecorder
{
    public abstract void Record(ReadOnlySpan<char> deviceId, long timestampUnixMs, double value);
}

/// <summary>
/// Encodes one batch's accepted readings as: repeat { int64 ts, double value, uint16 idLength, id UTF-8 }.
/// Backed by one pooled buffer that is handed to the journal writer, which returns it to the pool.
/// </summary>
public sealed class JournalBatchBuffer : ReadingRecorder
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
    private int _length;

    public int Count { get; private set; }

    public override void Record(ReadOnlySpan<char> deviceId, long timestampUnixMs, double value)
    {
        int idBytes = Encoding.UTF8.GetMaxByteCount(deviceId.Length);
        Ensure(8 + 8 + 2 + idBytes);
        var span = _buffer.AsSpan(_length);
        BinaryPrimitives.WriteInt64LittleEndian(span, timestampUnixMs);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], BitConverter.DoubleToInt64Bits(value));
        int written = Encoding.UTF8.GetBytes(deviceId, span[18..]);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], (ushort)written);
        _length += 18 + written;
        Count++;
    }

    /// <summary>Transfers ownership of the pooled buffer to the caller (who must return it to <see cref="ArrayPool{T}.Shared"/>).</summary>
    public (byte[] Buffer, int Length) Detach()
    {
        var result = (_buffer, _length);
        _buffer = [];
        _length = 0;
        return result;
    }

    /// <summary>Returns the buffer to the pool if it was never handed to the journal.</summary>
    public void Release()
    {
        if (_buffer.Length > 0) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _length = 0;
    }

    private void Ensure(int extra)
    {
        if (_length + extra <= _buffer.Length) return;
        var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + extra));
        Buffer.BlockCopy(_buffer, 0, bigger, 0, _length);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = bigger;
    }
}
