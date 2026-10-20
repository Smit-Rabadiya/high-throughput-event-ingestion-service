using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using EventIngestion.Core.Aggregation;
using EventIngestion.Core.Models;

namespace EventIngestion.Api.Streaming;

public enum IngestError
{
    None,
    MalformedJson,
    NotAnArray,
    TooManyReadings,
}

/// <summary>Counts of what was applied, plus why parsing stopped early (if it did). Readings before an error stay applied.</summary>
public readonly record struct IngestOutcome(IngestResult Result, IngestError Error);

/// <summary>
/// Streams a JSON array of readings straight from a <see cref="PipeReader"/> into the <see cref="DeviceStore"/>.
/// The body is never materialised: Utf8JsonReader is resumable, so a token (or a whole reading) may be split
/// across network chunks, and each reading is applied the moment its closing brace is seen. Working memory is a
/// single pooled char buffer for the current device id, regardless of batch size.
/// </summary>
public static class ReadingStreamParser
{
    public const int MaxBatchSize = 50_000;
    public const int MaxDeviceIdChars = 256;

    // CopyString needs room for the unescaped result; chars never exceed UTF-8 bytes, and an escape (\uXXXX) is 6 bytes.
    private const int IdBufferChars = MaxDeviceIdChars * 6;

    private enum Stage { Start, Items, Done }
    private enum Prop { None, DeviceId, Timestamp, Value, Unknown }

    private struct State
    {
        public JsonReaderState Reader;
        public Stage Stage;
        public bool InObject;
        public int SkipDepth;
        public Prop Current;

        public char[] IdBuffer;
        public int IdLength;
        public long Timestamp;
        public double Value;
        public bool HasId, HasTimestamp, HasValue, Bad;

        public int Accepted, Stale, Future, Invalid, DeviceLimit;
        public IngestError Error;

        public readonly int Seen => Accepted + Stale + Future + Invalid + DeviceLimit;
        public readonly IngestResult Result => new(Accepted, Stale, Future, Invalid, DeviceLimit);
    }

    public static async ValueTask<IngestOutcome> ParseAsync(
        PipeReader pipe, DeviceStore store, CancellationToken cancellationToken = default)
    {
        var state = new State { Reader = new JsonReaderState(), IdBuffer = ArrayPool<char>.Shared.Rent(IdBufferChars) };
        long now = store.NowUnixMs; // one clock read per batch: consistent window for every reading in it
        try
        {
            while (true)
            {
                var read = await pipe.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = read.Buffer;
                SequencePosition consumed;

                try
                {
                    consumed = Process(ref state, buffer, read.IsCompleted, store, now);
                }
                catch (JsonException)
                {
                    state.Error = IngestError.MalformedJson;
                    consumed = buffer.End;
                }

                // Examined = End tells the pipe to wait for more data instead of handing the same bytes back.
                pipe.AdvanceTo(consumed, buffer.End);

                if (state.Error != IngestError.None || state.Stage == Stage.Done) break;
                if (read.IsCompleted)
                {
                    state.Error = IngestError.MalformedJson; // stream ended before the closing ']'
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(state.IdBuffer);
        }

        return new IngestOutcome(state.Result, state.Error);
    }

    private static SequencePosition Process(
        ref State st, in ReadOnlySequence<byte> buffer, bool isFinalBlock, DeviceStore store, long now)
    {
        var reader = new Utf8JsonReader(buffer, isFinalBlock, st.Reader);

        while (reader.Read())
        {
            var token = reader.TokenType;

            // Inside a value we do not care about: only track nesting until it closes.
            if (st.SkipDepth > 0)
            {
                if (token is JsonTokenType.StartObject or JsonTokenType.StartArray) st.SkipDepth++;
                else if (token is JsonTokenType.EndObject or JsonTokenType.EndArray) st.SkipDepth--;
                continue;
            }

            if (st.Stage == Stage.Start)
            {
                if (token != JsonTokenType.StartArray)
                {
                    st.Error = IngestError.NotAnArray;
                    break;
                }
                st.Stage = Stage.Items;
            }
            else if (st.InObject)
            {
                switch (token)
                {
                    case JsonTokenType.PropertyName:
                        st.Current = MatchProperty(ref reader);
                        break;
                    case JsonTokenType.EndObject:
                        st.InObject = false;
                        CompleteReading(ref st, store, now);
                        break;
                    default:
                        AssignValue(ref reader, ref st, token);
                        break;
                }
            }
            else
            {
                switch (token)
                {
                    case JsonTokenType.StartObject:
                        if (st.Seen >= MaxBatchSize)
                        {
                            st.Error = IngestError.TooManyReadings;
                            return reader.Position;
                        }
                        BeginReading(ref st);
                        break;
                    case JsonTokenType.EndArray:
                        st.Stage = Stage.Done; // anything after the closing bracket is ignored
                        st.Reader = reader.CurrentState;
                        return reader.Position;
                    case JsonTokenType.StartArray:
                        if (!CountNonObject(ref st)) return reader.Position;
                        st.SkipDepth = 1;
                        break;
                    default:
                        if (!CountNonObject(ref st)) return reader.Position;
                        break;
                }
            }

            if (st.Error != IngestError.None) return reader.Position;
        }

        st.Reader = reader.CurrentState;
        return reader.Position;
    }

    private static bool CountNonObject(ref State st)
    {
        if (st.Seen >= MaxBatchSize)
        {
            st.Error = IngestError.TooManyReadings;
            return false;
        }
        st.Invalid++;
        return true;
    }

    private static void BeginReading(ref State st)
    {
        st.InObject = true;
        st.Current = Prop.None;
        st.HasId = st.HasTimestamp = st.HasValue = st.Bad = false;
        st.IdLength = 0;
    }

    private static Prop MatchProperty(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("deviceId"u8)) return Prop.DeviceId;
        if (reader.ValueTextEquals("timestamp"u8)) return Prop.Timestamp;
        if (reader.ValueTextEquals("value"u8)) return Prop.Value;
        return Prop.Unknown;
    }

    private static void AssignValue(ref Utf8JsonReader reader, ref State st, JsonTokenType token)
    {
        bool isContainer = token is JsonTokenType.StartObject or JsonTokenType.StartArray;

        switch (st.Current)
        {
            case Prop.DeviceId:
                st.HasId = false;
                if (token != JsonTokenType.String) { st.Bad = true; break; }
                long bytes = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
                if (bytes > st.IdBuffer.Length) { st.Bad = true; break; }
                int chars = reader.CopyString(st.IdBuffer);
                if (chars is 0 or > MaxDeviceIdChars) { st.Bad = true; break; }
                st.IdLength = chars;
                st.HasId = true;
                break;

            case Prop.Timestamp:
                st.HasTimestamp = false;
                if (token == JsonTokenType.String && reader.TryGetDateTimeOffset(out var when))
                {
                    st.Timestamp = when.ToUnixTimeMilliseconds();
                    st.HasTimestamp = true;
                }
                else st.Bad = true;
                break;

            case Prop.Value:
                st.HasValue = false;
                if (token == JsonTokenType.Number && reader.TryGetDouble(out double v) && double.IsFinite(v))
                {
                    st.Value = v;
                    st.HasValue = true;
                }
                else st.Bad = true;
                break;
        }

        if (isContainer) st.SkipDepth = 1;
    }

    private static void CompleteReading(ref State st, DeviceStore store, long now)
    {
        if (st.Bad || !st.HasId || !st.HasTimestamp || !st.HasValue)
        {
            st.Invalid++;
            return;
        }

        switch (store.Add(st.IdBuffer.AsSpan(0, st.IdLength), st.Timestamp, st.Value, now))
        {
            case AddResult.Accepted: st.Accepted++; break;
            case AddResult.Stale: st.Stale++; break;
            case AddResult.DeviceLimit: st.DeviceLimit++; break;
            default: st.Future++; break;
        }
    }
}

