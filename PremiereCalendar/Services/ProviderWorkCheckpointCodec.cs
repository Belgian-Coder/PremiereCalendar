using System.Buffers;
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static EncodedProviderWorkCheckpoint Encode(ProviderWorkCheckpoint checkpoint)
    {
        var started = Stopwatch.GetTimestamp();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, JsonOptions);
        var serialized = Stopwatch.GetTimestamp();
        string json;
        var encoding = "json";
        if (bytes.Length is >= MinimumCompressionBytes and <= MaximumDecodedBytes)
        {
            using var output = new MemoryStream();
            using (var compression = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
                compression.Write(bytes);
            var envelope = JsonSerializer.Serialize(new CompressedCheckpoint(4, "br-base64", bytes.Length,
                Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length))));
            // Base64 and envelope overhead count against the actual database bytes.
            if (envelope.Length < bytes.Length)
            {
                json = envelope;
                encoding = "br-base64";
            }
            else json = Encoding.UTF8.GetString(bytes);
        }
        else json = Encoding.UTF8.GetString(bytes);
        return new(json, bytes.Length, Encoding.UTF8.GetByteCount(json), encoding,
            Stopwatch.GetElapsedTime(started, serialized).TotalMilliseconds,
            Stopwatch.GetElapsedTime(serialized).TotalMilliseconds);
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

    private sealed record CompressedCheckpoint(int Version, string Encoding, int UncompressedBytes, string Payload);
}

internal sealed record EncodedProviderWorkCheckpoint(
    string Json, int RawBytes, int StoredBytes, string Encoding,
    double SerializeMilliseconds, double EncodeMilliseconds);
