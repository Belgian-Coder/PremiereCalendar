using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

/// <summary>
/// ArrayPool.Shared retains rented arrays of up to 1 GiB, so checkpoint buffers above the codec's pool limit must be
/// plain arrays, while the stored bytes stay identical to the reference serializer.
/// </summary>
public sealed class ProviderWorkCheckpointCodecBufferTests
{
    private const int Limit = ProviderWorkCheckpointCodec.PooledBufferLimit;

    [Fact]
    public void BuffersAboveTheLimitAreNeverParkedInTheSharedPool()
    {
        var large = ProviderWorkCheckpointCodec.RentBuffer(Limit + 1);
        Assert.True(large.Length >= Limit + 1);

        ProviderWorkCheckpointCodec.ReturnBuffer(large);
        var second = ProviderWorkCheckpointCodec.RentBuffer(Limit + 1);

        // A pooled array would be handed out again after it was returned.
        Assert.NotSame(large, second);
        // The shared pool never saw the oversized array either.
        Assert.NotSame(large, ArrayPool<byte>.Shared.Rent(Limit + 1));
    }

    [Fact]
    public void BuffersUpToTheLimitStillComeFromThePool()
    {
        var buffer = ProviderWorkCheckpointCodec.RentBuffer(Limit);
        Assert.True(buffer.Length >= Limit && buffer.Length <= Limit);

        // Returning a pooled-size buffer is accepted and an empty or null buffer is ignored.
        ProviderWorkCheckpointCodec.ReturnBuffer(buffer);
        ProviderWorkCheckpointCodec.ReturnBuffer([]);
        ProviderWorkCheckpointCodec.ReturnBuffer(null);
    }

    [Fact]
    public void CheckpointsLargerThanThePoolLimitEncodeToTheSameBytesAndDecode()
    {
        var checkpoint = PerformanceGoldenSupport.Checkpoint(20_000);
        var reference = JsonSerializer.SerializeToUtf8Bytes(checkpoint,
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        Assert.True(reference.Length > Limit, $"the fixture must exceed the pool limit but was {reference.Length} bytes");

        var first = ProviderWorkCheckpointCodec.Encode(checkpoint);
        var second = ProviderWorkCheckpointCodec.Encode(checkpoint);

        Assert.Equal(reference.Length, first.RawBytes);
        Assert.Equal("br-base64", first.Encoding);
        Assert.Equal(first.Json, second.Json);
        using var decoded = ProviderWorkCheckpointCodec.Decode(first.Json);
        Assert.Equal(3, decoded.RootElement.GetProperty("Version").GetInt32());
        using var original = JsonDocument.Parse(reference);
        Assert.Equal(original.RootElement.GetRawText(), decoded.RootElement.GetRawText());
    }
}
