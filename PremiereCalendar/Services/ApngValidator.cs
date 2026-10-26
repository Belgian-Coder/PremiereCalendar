using System.Buffers.Binary;
using System.Text;

namespace PremiereCalendar.Services;

/// <summary>
/// Performs bounded structural checks for APNG data before a raster decoder
/// reads its default PNG image. Animation frames are retained as the original
/// encoded PNG; this helper does not decode or coalesce them.
/// </summary>
internal static class ApngValidator
{
    private const int MaximumCapturedChunkLength = 26;
    private const int BufferSize = 8192;
    private static readonly uint[] CrcTable = CreateCrcTable();

    internal static bool ValidateIfAnimated(string path, long maxDecodedPixels, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length < 8)
        {
            return false;
        }

        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return false;
        }

        var fileLength = stream.Length;
        var chunkIndex = 0;
        var sawHeader = false;
        var sawAnimationControl = false;
        var sawImageData = false;
        var sawEnd = false;
        var canvasWidth = 0u;
        var canvasHeight = 0u;
        var declaredFrameCount = 0u;
        var frameControlCount = 0u;
        var expectedSequenceNumber = 0u;
        var currentFrameHasData = false;
        var currentFrameUsesImageData = false;
        var chunkHeaderBuffer = new byte[8];
        var crcBytesBuffer = new byte[sizeof(uint)];
        var buffer = new byte[BufferSize];

        while (stream.Position < fileLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fileLength - stream.Position < 12)
            {
                if (sawAnimationControl)
                {
                    throw Malformed("APNG ends inside a chunk.");
                }

                return false;
            }

            Span<byte> chunkHeader = chunkHeaderBuffer;
            stream.ReadExactly(chunkHeader);
            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader[..4]);
            var chunkTypeBytes = chunkHeader[4..8];
            var chunkType = Encoding.ASCII.GetString(chunkTypeBytes);
            var remainingAfterHeader = fileLength - stream.Position;
            if (remainingAfterHeader < 4 || (ulong)chunkLength > (ulong)(remainingAfterHeader - 4))
            {
                if (sawAnimationControl || chunkType is "acTL" or "fcTL" or "fdAT")
                {
                    throw Malformed($"APNG {chunkType} chunk exceeds the staged image bounds.");
                }

                return false;
            }

            if (chunkIndex == 0 && chunkType != "IHDR")
            {
                return false;
            }

            byte[]? capturedData = null;
            if (chunkType is "IHDR" or "acTL" or "fcTL")
            {
                if (chunkLength > MaximumCapturedChunkLength)
                {
                    if (sawAnimationControl || chunkType == "acTL")
                    {
                        throw Malformed($"APNG {chunkType} chunk has an invalid length.");
                    }

                    return false;
                }

                capturedData = new byte[checked((int)chunkLength)];
            }
            else if (chunkType == "fdAT")
            {
                if (chunkLength < sizeof(uint))
                {
                    throw Malformed("APNG fdAT chunk is shorter than its sequence number.");
                }

                capturedData = new byte[sizeof(uint)];
            }

            var crc = uint.MaxValue;
            foreach (var value in chunkTypeBytes)
            {
                crc = UpdateCrc(crc, value);
            }

            var remainingChunkBytes = chunkLength;
            var capturedOffset = 0;
            while (remainingChunkBytes > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readLength = (int)Math.Min(buffer.Length, remainingChunkBytes);
                var read = stream.Read(buffer, 0, readLength);
                if (read <= 0)
                {
                    throw Malformed("APNG chunk data is truncated.");
                }

                crc = UpdateCrc(crc, buffer.AsSpan(0, read));
                if (capturedData is not null && capturedOffset < capturedData.Length)
                {
                    var copyLength = Math.Min(read, capturedData.Length - capturedOffset);
                    buffer.AsSpan(0, copyLength).CopyTo(capturedData.AsSpan(capturedOffset));
                    capturedOffset += copyLength;
                }

                remainingChunkBytes -= (uint)read;
            }

            Span<byte> crcBytes = crcBytesBuffer;
            stream.ReadExactly(crcBytes);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(crcBytes);
            if (~crc != expectedCrc)
            {
                if (sawAnimationControl || chunkType is "acTL" or "fcTL" or "fdAT")
                {
                    throw Malformed($"APNG {chunkType} chunk has an invalid checksum.");
                }

                return false;
            }

            switch (chunkType)
            {
                case "IHDR":
                    if (chunkIndex != 0 || sawHeader || chunkLength != 13 || capturedData is null)
                    {
                        if (sawAnimationControl)
                        {
                            throw Malformed("APNG has an invalid IHDR chunk.");
                        }

                        return false;
                    }

                    canvasWidth = BinaryPrimitives.ReadUInt32BigEndian(capturedData.AsSpan(0, 4));
                    canvasHeight = BinaryPrimitives.ReadUInt32BigEndian(capturedData.AsSpan(4, 4));
                    if (canvasWidth == 0 || canvasHeight == 0)
                    {
                        throw Malformed("PNG canvas dimensions must be positive.");
                    }

                    sawHeader = true;
                    break;

                case "acTL":
                    if (!sawHeader || sawAnimationControl || sawImageData || chunkLength != 8 || capturedData is null)
                    {
                        throw Malformed("APNG has an invalid animation control chunk.");
                    }

                    declaredFrameCount = BinaryPrimitives.ReadUInt32BigEndian(capturedData.AsSpan(0, 4));
                    ValidateFrameBudget(canvasWidth, canvasHeight, declaredFrameCount, maxDecodedPixels);
                    sawAnimationControl = true;
                    break;

                case "IDAT":
                    if (!sawHeader)
                    {
                        if (sawAnimationControl)
                        {
                            throw Malformed("APNG image data appears before IHDR.");
                        }

                        return false;
                    }

                    sawImageData = true;
                    if (currentFrameUsesImageData)
                    {
                        currentFrameHasData = true;
                    }

                    break;

                case "fcTL":
                    if (!sawAnimationControl || capturedData is null || chunkLength != 26)
                    {
                        throw Malformed("APNG has an invalid frame control chunk.");
                    }

                    if (frameControlCount > 0 && !currentFrameHasData)
                    {
                        throw Malformed("APNG frame is missing image data.");
                    }

                    var sequenceNumber = BinaryPrimitives.ReadUInt32BigEndian(capturedData.AsSpan(0, 4));
                    ValidateSequenceNumber(sequenceNumber, ref expectedSequenceNumber);
                    var frameWidth = BinaryPrimitives.ReadUInt32BigEndian(capturedData.AsSpan(4, 4));
                    var frameHeight = BinaryPrimitives.ReadUInt32BigEndian(capturedData.AsSpan(8, 4));
                    var frameX = BinaryPrimitives.ReadUInt32BigEndian(capturedData.AsSpan(12, 4));
                    var frameY = BinaryPrimitives.ReadUInt32BigEndian(capturedData.AsSpan(16, 4));
                    if (frameWidth == 0
                        || frameHeight == 0
                        || frameX > canvasWidth
                        || frameY > canvasHeight
                        || frameWidth > canvasWidth - frameX
                        || frameHeight > canvasHeight - frameY
                        || capturedData[24] > 2
                        || capturedData[25] > 1)
                    {
                        throw Malformed("APNG frame dimensions or offsets exceed the PNG canvas.");
                    }

                    frameControlCount++;
                    if (frameControlCount > declaredFrameCount)
                    {
                        throw Malformed("APNG contains more frame controls than declared.");
                    }

                    currentFrameHasData = false;
                    currentFrameUsesImageData = frameControlCount == 1 && !sawImageData;
                    break;

                case "fdAT":
                    if (!sawAnimationControl || !sawImageData || frameControlCount == 0 || currentFrameUsesImageData || capturedData is null)
                    {
                        throw Malformed("APNG fdAT chunk appears outside an animation frame.");
                    }

                    var frameDataSequence = BinaryPrimitives.ReadUInt32BigEndian(capturedData);
                    ValidateSequenceNumber(frameDataSequence, ref expectedSequenceNumber);
                    currentFrameHasData = true;
                    break;

                case "IEND":
                    if (chunkLength != 0 || !sawImageData)
                    {
                        if (sawAnimationControl)
                        {
                            throw Malformed("APNG has an invalid IEND chunk.");
                        }

                        return false;
                    }

                    if (sawAnimationControl)
                    {
                        if (frameControlCount != declaredFrameCount || !currentFrameHasData)
                        {
                            throw Malformed("APNG frame count or frame data does not match acTL.");
                        }

                        if (stream.Position != fileLength)
                        {
                            throw Malformed("APNG contains bytes after IEND.");
                        }

                        sawEnd = true;
                    }

                    break;
            }

            chunkIndex++;
            if (chunkType == "IEND")
            {
                break;
            }
        }

        if (!sawAnimationControl)
        {
            return false;
        }

        if (!sawEnd)
        {
            throw Malformed("APNG is missing IEND.");
        }

        return true;
    }

    private static void ValidateFrameBudget(uint canvasWidth, uint canvasHeight, uint frameCount, long maxDecodedPixels)
    {
        if (frameCount == 0 || frameCount > ImageMagickRuntime.MaxFrameCount)
        {
            throw new ExternalApiException(
                $"Image exceeds the decoded frame limit of {ImageMagickRuntime.MaxFrameCount}.");
        }

        var pixelLimit = Math.Max(1, maxDecodedPixels);
        if (canvasWidth == 0
            || canvasHeight == 0
            || canvasWidth > (ulong)pixelLimit / canvasHeight)
        {
            throw PixelBudgetExceeded(pixelLimit, frameCount);
        }

        var canvasPixels = (long)canvasWidth * canvasHeight;
        if (frameCount > (ulong)pixelLimit / (ulong)canvasPixels)
        {
            throw PixelBudgetExceeded(pixelLimit, frameCount);
        }
    }

    private static void ValidateSequenceNumber(uint sequenceNumber, ref uint expectedSequenceNumber)
    {
        if (sequenceNumber != expectedSequenceNumber || expectedSequenceNumber == uint.MaxValue)
        {
            throw Malformed("APNG sequence numbers are invalid.");
        }

        expectedSequenceNumber++;
    }

    private static ExternalApiException PixelBudgetExceeded(long maxDecodedPixels, uint frameCount)
    {
        return new ExternalApiException(
            $"Image exceeds the decoded pixel limit of {maxDecodedPixels} across {frameCount} frame(s).");
    }

    private static ExternalApiException Malformed(string message) => new($"Image contains malformed APNG data: {message}");

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> values)
    {
        foreach (var value in values)
        {
            crc = UpdateCrc(crc, value);
        }

        return crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        return (crc >> 8) ^ CrcTable[(crc ^ value) & 0xff];
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            var crc = value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
            }

            table[value] = crc;
        }

        return table;
    }
}
