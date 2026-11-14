using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PremiereCalendar.Services;

// Version 4 is only a storage envelope. Its decoded contents remain the complete
// version 3 checkpoint, including canonical items, source summaries and ages.
internal static class ProviderWorkCheckpointCodec
{
    internal const int MaximumDecodedBytes = 64 * 1024 * 1024;
    private const int MinimumCompressionBytes = 4096;
    // ArrayPool.Shared keeps rented arrays of up to 1 GiB alive; checkpoints can reach tens of megabytes, so only
    // buffers up to this size are pooled and anything larger is allocated (and later collected) directly.
    internal const int PooledBufferLimit = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static EncodedProviderWorkCheckpoint Encode(ProviderWorkCheckpoint checkpoint)
    {
        var started = Stopwatch.GetTimestamp();
        // The raw UTF-8 JSON lives in a pooled buffer while it stays below PooledBufferLimit, which avoids an
        // exact-size array per checkpoint. Larger payloads use plain arrays that become ordinary (LOH) garbage
        // instead of being parked in ArrayPool.Shared. Writer options match
        // JsonSerializer.SerializeToUtf8Bytes, so the bytes are identical to the previous implementation.
        using var raw = new PooledBufferWriter(initialCapacity: 16 * 1024);
        using (var writer = new Utf8JsonWriter(raw, new JsonWriterOptions { SkipValidation = true, MaxDepth = 64 }))
            JsonSerializer.Serialize(writer, checkpoint, JsonOptions);
        var rawBytes = raw.WrittenSpan;
        var serialized = Stopwatch.GetTimestamp();
        var encoding = "json";
        string json;
        if (rawBytes.Length is >= MinimumCompressionBytes and <= MaximumDecodedBytes
            && TryEncodeEnvelope(rawBytes, out var envelope))
        {
            using (envelope)
            {
                // Base64 and envelope overhead count against the actual database bytes.
                // The envelope is pure ASCII, so its byte length equals its string length.
                if (envelope.WrittenCount < rawBytes.Length)
                {
                    json = Encoding.UTF8.GetString(envelope.WrittenSpan);
                    encoding = "br-base64";
                    return Encoded(json, rawBytes.Length, envelope.WrittenCount, encoding, started, serialized);
                }
            }
        }
        json = Encoding.UTF8.GetString(rawBytes);
        return Encoded(json, rawBytes.Length, rawBytes.Length, encoding, started, serialized);
    }

    private static EncodedProviderWorkCheckpoint Encoded(string json, int rawBytes, int storedBytes, string encoding, long started, long serialized)
        => new(json, rawBytes, storedBytes, encoding,
            Stopwatch.GetElapsedTime(started, serialized).TotalMilliseconds,
            Stopwatch.GetElapsedTime(serialized).TotalMilliseconds);

    internal static byte[] RentBuffer(int minimumLength) => minimumLength > PooledBufferLimit
        ? GC.AllocateUninitializedArray<byte>(minimumLength)
        : ArrayPool<byte>.Shared.Rent(minimumLength);

    // Arrays above the limit were never rented, so they are simply dropped.
    internal static void ReturnBuffer(byte[]? buffer)
    {
        if (buffer is { Length: > 0 and <= PooledBufferLimit }) ArrayPool<byte>.Shared.Return(buffer);
    }

    // Produces {"Version":4,"Encoding":"br-base64","UncompressedBytes":n,"Payload":"..."} exactly as
    // JsonSerializer.Serialize of the former CompressedCheckpoint record did, including default-encoder escaping of '+'.
    private static bool TryEncodeEnvelope(ReadOnlySpan<byte> raw, out PooledBufferWriter envelope)
    {
        envelope = null!;
        byte[]? compressedBuffer = null, base64Buffer = null;
        try
        {
            // BrotliStream(Fastest) is BrotliEncoder(quality 1, window 22) fed with PROCESS
            // and then finished with an empty FINISH call. Mirror that exactly.
            compressedBuffer = RentBuffer(BrotliEncoder.GetMaxCompressedLength(raw.Length));
            var compressed = 0;
            using (var encoder = new BrotliEncoder(quality: 1, window: 22))
            {
                var remaining = raw;
                while (!remaining.IsEmpty)
                {
                    var status = encoder.Compress(remaining, compressedBuffer.AsSpan(compressed), out var consumed, out var written, isFinalBlock: false);
                    remaining = remaining[consumed..];
                    compressed += written;
                    if (status == OperationStatus.InvalidData || (consumed == 0 && written == 0)) return false;
                }
                OperationStatus finish;
                do
                {
                    finish = encoder.Compress(ReadOnlySpan<byte>.Empty, compressedBuffer.AsSpan(compressed), out _, out var written, isFinalBlock: true);
                    compressed += written;
                    if (finish == OperationStatus.InvalidData || (finish != OperationStatus.Done && written == 0)) return false;
                } while (finish != OperationStatus.Done);
            }
            base64Buffer = RentBuffer(Base64.GetMaxEncodedToUtf8Length(compressed));
            if (Base64.EncodeToUtf8(compressedBuffer.AsSpan(0, compressed), base64Buffer, out _, out var encoded) != OperationStatus.Done)
                return false;
            var result = new PooledBufferWriter(encoded + 128);
            try
            {
                using (var writer = new Utf8JsonWriter(result, new JsonWriterOptions { SkipValidation = true, MaxDepth = 64 }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("Version", 4);
                    writer.WriteString("Encoding", "br-base64");
                    writer.WriteNumber("UncompressedBytes", raw.Length);
                    writer.WriteString("Payload", base64Buffer.AsSpan(0, encoded));
                    writer.WriteEndObject();
                }
            }
            catch
            {
                result.Dispose();
                throw;
            }
            envelope = result;
            return true;
        }
        finally
        {
            ReturnBuffer(compressedBuffer);
            ReturnBuffer(base64Buffer);
        }
    }

    internal static JsonDocument Decode(string json)
    {
        var document = JsonDocument.Parse(json);
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid checkpoint object.");
            if (!root.TryGetProperty("Version", out var version)) return document;
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
                throw new JsonException("Invalid checkpoint version.");
            if (number != 4)
                return document;
            if (!root.TryGetProperty("Encoding", out var encoding) || encoding.ValueKind != JsonValueKind.String
                || encoding.GetString() != "br-base64"
                || !root.TryGetProperty("UncompressedBytes", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt32(out var length)
                || length is <= 0 or > MaximumDecodedBytes
                || !root.TryGetProperty("Payload", out var payload) || payload.ValueKind != JsonValueKind.String)
                throw new JsonException("Invalid checkpoint envelope.");
            var base64 = payload.GetString()!;
            if (base64.Length > ((MaximumDecodedBytes + 2L) / 3) * 4)
                throw new JsonException("Checkpoint envelope exceeds its size limit.");
            byte[] compressed;
            try { compressed = Convert.FromBase64String(base64); }
            catch (FormatException exception) { throw new JsonException("Invalid checkpoint encoding.", exception); }
            var decoded = new byte[length];
            using var decompressor = new BrotliDecoder();
            if (decompressor.Decompress(compressed, decoded, out var consumed, out var written) != OperationStatus.Done
                || consumed != compressed.Length || written != length)
                throw new JsonException("Invalid checkpoint compressed payload.");
            var inner = JsonDocument.Parse(decoded);
            if (inner.RootElement.ValueKind != JsonValueKind.Object
                || !inner.RootElement.TryGetProperty("Version", out var innerVersion)
                || innerVersion.ValueKind != JsonValueKind.Number || !innerVersion.TryGetInt32(out var innerNumber) || innerNumber != 3)
            {
                inner.Dispose();
                throw new JsonException("Invalid checkpoint payload version.");
            }
            document.Dispose();
            return inner;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private sealed class PooledBufferWriter(int initialCapacity) : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = RentBuffer(Math.Max(initialCapacity, 256));
        private int _written;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);
        public int WrittenCount => _written;

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count > _buffer.Length - _written) throw new InvalidOperationException("Advanced past the end of the buffer.");
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void Ensure(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            if (sizeHint == 0) sizeHint = 1;
            if (sizeHint <= _buffer.Length - _written) return;
            var next = RentBuffer((int)Math.Min(Array.MaxLength, Math.Max((long)_buffer.Length * 2, (long)_written + sizeHint)));
            _buffer.AsSpan(0, _written).CopyTo(next);
            ReturnBuffer(_buffer);
            _buffer = next;
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = [];
            ReturnBuffer(buffer);
        }
    }
}

internal sealed record EncodedProviderWorkCheckpoint(
    string Json, int RawBytes, int StoredBytes, string Encoding,
    double SerializeMilliseconds, double EncodeMilliseconds);
