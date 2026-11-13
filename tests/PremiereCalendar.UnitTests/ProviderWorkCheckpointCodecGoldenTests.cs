using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

/// <summary>
/// Persisted checkpoint payloads must stay byte-for-byte identical across the downgrade path, so the
/// reference below is the former implementation (copied verbatim) and every output is compared with it.
/// Brotli is deterministic for identical input, quality and window, so the envelope compares exactly.
/// </summary>
public sealed class ProviderWorkCheckpointCodecGoldenTests
{
    private static readonly JsonSerializerOptions ReferenceOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record ReferenceEnvelope(int Version, string Encoding, int UncompressedBytes, string Payload);

    private static (string Json, int RawBytes, int StoredBytes, string Encoding) Reference(ProviderWorkCheckpoint checkpoint)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, ReferenceOptions);
        string json;
        var encoding = "json";
        if (bytes.Length is >= 4096 and <= ProviderWorkCheckpointCodec.MaximumDecodedBytes)
        {
            using var output = new MemoryStream();
            using (var compression = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
                compression.Write(bytes);
            var envelope = JsonSerializer.Serialize(new ReferenceEnvelope(4, "br-base64", bytes.Length,
                Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length))));
            if (envelope.Length < bytes.Length)
            {
                json = envelope;
                encoding = "br-base64";
            }
            else json = Encoding.UTF8.GetString(bytes);
        }
        else json = Encoding.UTF8.GetString(bytes);
        return (json, bytes.Length, Encoding.UTF8.GetByteCount(json), encoding);
    }

    private static void AssertIdentical(ProviderWorkCheckpoint checkpoint, string expectedEncoding)
    {
        var expected = Reference(checkpoint);
        var actual = ProviderWorkCheckpointCodec.Encode(checkpoint);
        Assert.Equal(expectedEncoding, expected.Encoding);
        Assert.Equal(expected.Encoding, actual.Encoding);
        Assert.Equal(expected.RawBytes, actual.RawBytes);
        Assert.Equal(expected.StoredBytes, actual.StoredBytes);
        Assert.Equal(expected.Json, actual.Json); // Ordinal, byte-for-byte after UTF-8 encoding.
        Assert.True(Encoding.UTF8.GetBytes(expected.Json).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(actual.Json)));
        using var decoded = ProviderWorkCheckpointCodec.Decode(actual.Json);
        Assert.Equal(3, decoded.RootElement.GetProperty("Version").GetInt32());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(40)]
    [InlineData(400)]
    public void RepresentativeCheckpointsEncodeToTheSameBytes(int items)
        => AssertIdentical(PerformanceGoldenSupport.Checkpoint(items), items == 1 ? "json" : "br-base64");

    [Fact]
    public void LargeCheckpointEncodesToTheSameBytes()
        => AssertIdentical(PerformanceGoldenSupport.Checkpoint(4000), "br-base64");

    [Fact]
    public void CompressedEnvelopeEscapesPlusAsTheDefaultEncoderDid()
    {
        var checkpoint = PerformanceGoldenSupport.Checkpoint(60);
        var expected = Reference(checkpoint).Json;
        var actual = ProviderWorkCheckpointCodec.Encode(checkpoint).Json;
        Assert.Equal(expected, actual);
        Assert.DoesNotContain('+', actual);
        Assert.StartsWith("{\"Version\":4,\"Encoding\":\"br-base64\",\"UncompressedBytes\":", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompressibleLargePayloadStaysUncompressedExactlyAsBefore()
    {
        var random = new Random(7);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!#$%()*,-./:;=?@[]^_{|}~";
        var noise = string.Create(4200, random, static (span, rng) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = alphabet[rng.Next(alphabet.Length)];
        });
        // High-entropy text past the compression threshold: Brotli plus base64 cannot beat the raw JSON.
        var checkpoint = new ProviderWorkCheckpoint(noise, null, null, null,
            new Dictionary<string, IReadOnlyList<PremiereCalendar.Models.PremiereItem>>());
        AssertIdentical(checkpoint, "json");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(3900)]
    [InlineData(4000)]
    [InlineData(4050)]
    [InlineData(4100)]
    [InlineData(4300)]
    [InlineData(9000)]
    public void CompressionThresholdAndSizeDecisionsMatchAcrossMessageLengths(int messageLength)
    {
        var message = string.Concat(Enumerable.Range(0, messageLength / 8 + 1).Select(i => (i * 2654435761u % 99991u).ToString("x7") + "+")).Substring(0, messageLength);
        var checkpoint = new ProviderWorkCheckpoint(message, 1, 2, null, new Dictionary<string, IReadOnlyList<PremiereCalendar.Models.PremiereItem>>());
        var expected = Reference(checkpoint);
        var actual = ProviderWorkCheckpointCodec.Encode(checkpoint);
        Assert.Equal(expected.Encoding, actual.Encoding);
        Assert.Equal(expected.Json, actual.Json);
        Assert.Equal(expected.RawBytes, actual.RawBytes);
        Assert.Equal(expected.StoredBytes, actual.StoredBytes);
    }

    [Fact]
    public void NonAsciiMessageIsEscapedIdenticallyInRawAndCompressedForms()
    {
        var message = string.Concat(Enumerable.Repeat("Café 日本 😀 <tag> & 'quote' ", 300));
        var checkpoint = new ProviderWorkCheckpoint(message, 1, 2, null, new Dictionary<string, IReadOnlyList<PremiereCalendar.Models.PremiereItem>>());
        var expected = Reference(checkpoint);
        var actual = ProviderWorkCheckpointCodec.Encode(checkpoint);
        Assert.Equal(expected.Json, actual.Json);
        Assert.Equal(expected.StoredBytes, actual.StoredBytes);
        Assert.Equal(actual.Json.Length, actual.StoredBytes); // ASCII-only, so the size comparison used chars and bytes alike.
    }

    [Fact]
    public void RepeatedEncodesAreStableWhilePooledBuffersAreReused()
    {
        var first = PerformanceGoldenSupport.Checkpoint(200, seed: 3);
        var second = PerformanceGoldenSupport.Checkpoint(30, seed: 4);
        var expectedFirst = Reference(first).Json;
        var expectedSecond = Reference(second).Json;
        for (var round = 0; round < 5; round++)
        {
            Assert.Equal(expectedFirst, ProviderWorkCheckpointCodec.Encode(first).Json);
            Assert.Equal(expectedSecond, ProviderWorkCheckpointCodec.Encode(second).Json);
        }
    }
}
