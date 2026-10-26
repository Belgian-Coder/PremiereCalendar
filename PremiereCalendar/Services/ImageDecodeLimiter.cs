using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

/// <summary>
/// Shares image decode capacity across transient cache instances and bounds the number of
/// resized source images that can be staged while they wait for a decoder.
/// </summary>
public sealed class ImageDecodeLimiter
{
    private readonly SemaphoreSlim _decodeGate;
    private readonly SemaphoreSlim _stagingGate;

    public ImageDecodeLimiter(IOptions<ImageCacheOptions> options)
    {
        var decodeLimit = Math.Clamp(options.Value.MaxConcurrentDecodes, 1, 64);
        var downloadLimit = Math.Clamp(options.Value.MaxConcurrentDownloads, 1, 64);
        _decodeGate = new SemaphoreSlim(decodeLimit, decodeLimit);

        // At most one download's worth of queued source files can accumulate in addition
        // to the images currently being decoded.
        var stagingLimit = Math.Clamp(decodeLimit + downloadLimit, 1, 128);
        _stagingGate = new SemaphoreSlim(stagingLimit, stagingLimit);
    }

    public async ValueTask<IDisposable> AcquireDecodeAsync(CancellationToken cancellationToken)
    {
        await _decodeGate.WaitAsync(cancellationToken);
        return new SemaphoreLease(_decodeGate);
    }

    public async ValueTask<IDisposable> AcquireStagingSlotAsync(CancellationToken cancellationToken)
    {
        await _stagingGate.WaitAsync(cancellationToken);
        return new SemaphoreLease(_stagingGate);
    }

    private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
