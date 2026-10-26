using System.Net;
using System.Net.Http.Headers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Options;
using PremiereCalendar.Services;
using ImageMagick;

namespace PremiereCalendar.IntegrationTests;

public sealed class FileImageCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-image-cache-{Guid.NewGuid():N}");
    private static readonly string MagickConfigurationRoot = Path.Combine(
        Path.GetTempPath(),
        $"premiere-image-cache-magick-config-{Guid.NewGuid():N}");

    static FileImageCacheTests()
    {
        ImageMagickRuntime.EnsureInitialized(MagickConfigurationRoot);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            var configRoot = Path.GetFullPath(MagickConfigurationRoot);
            if (configRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(configRoot))
            {
                Directory.Delete(configRoot, recursive: true);
            }
        };
    }

    [Fact]
    public void ImageMagickRuntime_UsesTheShippedStrictPolicyInItsWritableCacheDirectory()
    {
        var shippedPolicy = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ImageMagick", "policy.xml"));
        var initializedPolicy = File.ReadAllText(Path.Combine(ImageMagickRuntime.ConfigurationPath!, "policy.xml"));

        Assert.Equal(shippedPolicy, initializedPolicy);
    }

    [Fact]
    public void ImageMagickRuntime_NativePolicyRejectsPseudoFormats()
    {
        ImageMagickRuntime.EnsureInitialized(_root);
        Directory.CreateDirectory(_root);
        var svgPath = Path.Combine(_root, "policy-denied.svg");
        File.WriteAllText(svgPath, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1\" height=\"1\"><rect width=\"1\" height=\"1\" fill=\"red\"/></svg>");

        Assert.Throws<MagickPolicyErrorException>(() => new MagickImage(svgPath));
    }

    [Fact]
    public void ApngValidator_PropagatesAlreadyCancelledValidation()
    {
        var png = AnimatedPngBytes();
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "cancelled.apng");
        File.WriteAllBytes(path, png);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ApngValidator.ValidateIfAnimated(
            path,
            maxDecodedPixels: 40_000_000,
            cancellation.Token));
    }

    [Fact]
    public async Task GetOrAddAsync_MapsNativeResourceLimitFailureToIOException()
    {
        var sample = SamplePngBytes();
        var handler = new StubHttpMessageHandler(_ => Image(sample, "image/png"));
        var cache = CreateCache(handler);
        var originalMemoryLimit = ResourceLimits.Memory;

        try
        {
            ResourceLimits.Memory = 1;
            var error = await Assert.ThrowsAsync<IOException>(() => cache.GetOrAddAsync(
                "https://static.tvmaze.com/uploads/images/original_untouched/resource-limit.png",
                forceRefresh: false,
                CancellationToken.None,
                width: 80));

            Assert.True(error.InnerException is MagickResourceLimitErrorException or MagickCacheErrorException,
                $"Expected a native resource/cache exception, received {error.InnerException?.GetType().FullName ?? "no inner exception"}.");
        }
        finally
        {
            ResourceLimits.Memory = originalMemoryLimit;
        }
    }

    [Fact]
    public async Task GetOrAddAsync_IdentifiesNativeListLimitForOverCapGifFrames()
    {
        var overCapGif = RepeatedOnePixelGifFrameBytes(ImageMagickRuntime.MaxFrameCount + 1);
        var handler = new StubHttpMessageHandler(_ => Image(overCapGif, "image/gif"));
        var cache = CreateCache(handler, new ImageCacheOptions
        {
            Enabled = true,
            Directory = "cache/images",
            CacheDays = 30,
            MaxBytes = 1024 * 1024,
            AllowedHosts = ["static.tvmaze.com"]
        });

        var error = await Assert.ThrowsAsync<ExternalApiException>(() => cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/too-many-frames.gif",
            forceRefresh: false,
            CancellationToken.None,
            width: 80));

        Assert.Contains("decoded frame limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("list length", error.InnerException?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetOrAddAsync_FetchesAndReusesCachedImage()
    {
        var handler = new StubHttpMessageHandler(_ => Image([1, 2, 3]));
        var cache = CreateCache(handler);

        var first = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/poster.jpg",
            forceRefresh: false,
            CancellationToken.None);
        var second = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/poster.jpg",
            forceRefresh: false,
            CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(await File.ReadAllBytesAsync(first.FilePath), await File.ReadAllBytesAsync(second.FilePath));
        Assert.Equal("image/jpeg", first.ContentType);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(first.FilePath))).ToLowerInvariant(), first.ContentHash);
        Assert.True(Directory.Exists(Path.Combine(_root, "cache", "images")));
    }

    [Fact]
    public async Task GetOrAddAsync_CoalescesConcurrentFetchesForSameImage()
    {
        var handler = new BlockingImageHandler([4, 5, 6]);
        var firstCache = CreateCache(handler);
        var secondCache = CreateCache(handler);
        const string imageUrl = "https://image.tmdb.org/t/p/w342/concurrent.jpg";

        var first = firstCache.GetOrAddAsync(
            imageUrl,
            forceRefresh: false,
            CancellationToken.None);
        await handler.RequestStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var second = secondCache.GetOrAddAsync(
            imageUrl,
            forceRefresh: false,
            CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal(1, handler.RequestCount);

        handler.Release();
        var images = await Task.WhenAll(first, second);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(images[0].FilePath, images[1].FilePath);
        Assert.Equal(await File.ReadAllBytesAsync(images[0].FilePath), await File.ReadAllBytesAsync(images[1].FilePath));
    }

    [Fact]
    public async Task GetOrAddAsync_ForceRefreshFetchesRemoteImageAgain()
    {
        byte imageVersion = 0;
        var handler = new StubHttpMessageHandler(_ => Image([++imageVersion]));
        var cache = CreateCache(handler);

        var first = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/poster.jpg",
            forceRefresh: false,
            CancellationToken.None);
        var firstBytes = await File.ReadAllBytesAsync(first.FilePath);
        var second = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/poster.jpg",
            forceRefresh: true,
            CancellationToken.None);
        var secondBytes = await File.ReadAllBytesAsync(second.FilePath);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, firstBytes[0]);
        Assert.Equal(2, secondBytes[0]);
        Assert.NotEqual(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public async Task GetOrAddAsync_RefreshPublishesAnImmutableGenerationForExistingReaders()
    {
        byte imageVersion = 0;
        var handler = new StubHttpMessageHandler(_ => Image([++imageVersion, 2, 3]));
        var cache = CreateCache(handler);
        var first = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/atomic.jpg",
            forceRefresh: false,
            CancellationToken.None);
        await using var oldReader = new FileStream(first.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/atomic.jpg",
            forceRefresh: true,
            CancellationToken.None);

        using var oldSnapshot = new MemoryStream();
        await oldReader.CopyToAsync(oldSnapshot);
        Assert.Equal(new byte[] { 1, 2, 3 }, oldSnapshot.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(first.FilePath));
        Assert.NotEqual(first.FilePath, (await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/atomic.jpg",
            forceRefresh: false,
            CancellationToken.None)).FilePath);
    }

    [Fact]
    public async Task GetOrAddAsync_ReadOnlyMetadataKeepsThePlatformCommitAndBodyPairConsistent()
    {
        byte imageVersion = 0;
        var handler = new StubHttpMessageHandler(_ => Image([++imageVersion, 4, 5]));
        var cache = CreateCache(handler);
        const string imageUrl = "https://image.tmdb.org/t/p/w342/metadata-failure.jpg";
        var first = await cache.GetOrAddAsync(imageUrl, false, CancellationToken.None);
        var metadataPath = MetadataPath(first);
        var oldMetadata = await File.ReadAllBytesAsync(metadataPath);
        var oldBytes = await File.ReadAllBytesAsync(first.FilePath);

        File.SetAttributes(metadataPath, File.GetAttributes(metadataPath) | FileAttributes.ReadOnly);
        CachedImage refreshed;
        try
        {
            refreshed = await cache.GetOrAddAsync(imageUrl, true, CancellationToken.None);
        }
        finally
        {
            File.SetAttributes(metadataPath, FileAttributes.Normal);
        }

        var current = await cache.GetOrAddAsync(imageUrl, false, CancellationToken.None);
        if (OperatingSystem.IsWindows())
        {
            // Windows rejects this replacement. The failed commit must retain the old pair.
            Assert.True(refreshed.IsStale);
            Assert.Equal(oldMetadata, await File.ReadAllBytesAsync(metadataPath));
            Assert.Equal(first.ContentHash, current.ContentHash);
        }
        else
        {
            // Unix directory permissions allow replacing an owned read-only file atomically.
            Assert.False(refreshed.IsStale);
            Assert.NotEqual(oldMetadata, await File.ReadAllBytesAsync(metadataPath));
            Assert.NotEqual(first.ContentHash, current.ContentHash);
            Assert.Equal(refreshed.ContentHash, current.ContentHash);
            Assert.Equal(new byte[] { 2, 4, 5 }, await File.ReadAllBytesAsync(current.FilePath));
        }

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(first.FilePath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(current.FilePath))).ToLowerInvariant(), current.ContentHash);
    }

    [Fact]
    public async Task GetOrAddAsync_UsesStaleCachedImageWhenForcedRefreshTimesOut()
    {
        var timeout = false;
        var handler = new StubHttpMessageHandler(_ =>
        {
            if (timeout)
            {
                throw new TaskCanceledException("Simulated HTTP timeout.");
            }

            return Image([7, 7, 7]);
        });
        var cache = CreateCache(handler);
        var cached = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/stale.jpg",
            forceRefresh: false,
            CancellationToken.None);
        var metadataPath = MetadataPath(cached);
        File.Copy(cached.FilePath, Path.Combine(Path.GetDirectoryName(cached.FilePath)!, $"{cached.CacheKey}.bin"));
        await File.WriteAllTextAsync(
            metadataPath,
            """
            {"sourceUrl":"https://image.tmdb.org/t/p/w342/stale.jpg","contentType":"image/jpeg","fetchedUtc":"2026-02-01T00:00:00+00:00"}
            """);
        timeout = true;

        var stale = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/stale.jpg",
            forceRefresh: true,
            CancellationToken.None);

        Assert.Equal(cached.FilePath, stale.FilePath);
        Assert.True(stale.IsStale);
        Assert.Equal(TimeSpan.FromMinutes(1), stale.BrowserMaxAge);
        Assert.Equal([7, 7, 7], await File.ReadAllBytesAsync(stale.FilePath));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetOrAddAsync_TimeoutDuringResponseBodyReadServesStaleImage()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ => Interlocked.Increment(ref requestCount) == 1
            ? Image([6, 5, 4])
            : StalledImage());
        var cache = CreateCache(handler, new ImageCacheOptions
        {
            Enabled = true,
            Directory = "cache/images",
            CacheDays = 30,
            MaxBytes = 1024,
            RequestTimeoutSeconds = 1,
            AllowedHosts = ["image.tmdb.org"]
        });
        const string imageUrl = "https://image.tmdb.org/t/p/w342/stalled-body.jpg";
        var cached = await cache.GetOrAddAsync(imageUrl, false, CancellationToken.None);

        var stale = await cache.GetOrAddAsync(imageUrl, true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stale.IsStale);
        Assert.Equal(cached.ContentHash, stale.ContentHash);
        Assert.Equal(new byte[] { 6, 5, 4 }, await File.ReadAllBytesAsync(stale.FilePath));
    }

    [Fact]
    public async Task GetOrAddAsync_LazilyAddsContentHashToLegacyMetadata()
    {
        var handler = new StubHttpMessageHandler(_ => Image([2, 4, 6]));
        var cache = CreateCache(handler);
        var first = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/legacy-hash.jpg",
            forceRefresh: false,
            CancellationToken.None);
        var metadataPath = MetadataPath(first);
        var legacyBodyPath = Path.Combine(Path.GetDirectoryName(first.FilePath)!, $"{first.CacheKey}.bin");
        File.Copy(first.FilePath, legacyBodyPath);
        File.Delete(first.FilePath);
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!;
        metadata.AsObject().Remove("contentHash");
        metadata.AsObject().Remove("bodyGenerationVersion");
        await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());

        var reused = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/legacy-hash.jpg",
            forceRefresh: false,
            CancellationToken.None);
        var updatedMetadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!;

        Assert.Single(handler.Requests);
        Assert.Equal(first.ContentHash, reused.ContentHash);
        Assert.EndsWith($".{first.ContentHash}.bin", reused.FilePath, StringComparison.Ordinal);
        Assert.Equal(reused.ContentHash, updatedMetadata["contentHash"]?.GetValue<string>());
        Assert.Equal(2, updatedMetadata["bodyGenerationVersion"]?.GetValue<int>());
        Assert.True(File.Exists(legacyBodyPath));
    }

    [Fact]
    public async Task GetOrAddAsync_RepairsCorruptMetadataByFetchingImageAgain()
    {
        var handler = new StubHttpMessageHandler(_ => Image([8, 9, 10]));
        var cache = CreateCache(handler);
        var first = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/corrupt-metadata.jpg",
            forceRefresh: false,
            CancellationToken.None);
        var metadataPath = MetadataPath(first);
        await File.WriteAllTextAsync(metadataPath, "{invalid json");

        var repaired = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/corrupt-metadata.jpg",
            forceRefresh: false,
            CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(repaired.FilePath))).ToLowerInvariant(), repaired.ContentHash);
        Assert.NotNull(JsonNode.Parse(await File.ReadAllTextAsync(metadataPath)));
    }

    [Fact]
    public async Task GetOrAddAsync_RejectsSourceAboveDecodedPixelLimitBeforeLoadingIt()
    {
        var oversizedDimensions = PngBytesWithDimensions(SamplePngBytes(), 8000, 6000);
        var handler = new StubHttpMessageHandler(_ => Image(oversizedDimensions, "image/png"));
        var options = new ImageCacheOptions
        {
            Enabled = true,
            Directory = "cache/images",
            CacheDays = 30,
            MaxBytes = 1024 * 1024,
            MaxDecodedPixels = 40_000_000,
            AllowedHosts = ["static.tvmaze.com"]
        };
        var cache = CreateCache(handler, options);

        var error = await Assert.ThrowsAsync<ExternalApiException>(() => cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/large.png",
            forceRefresh: false,
            CancellationToken.None,
            width: 80));

        Assert.Contains("decoded pixel limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "cache", "images"))
            && Directory.EnumerateFiles(Path.Combine(_root, "cache", "images"), "*.bin").Any());
    }

    [Fact]
    public async Task GetOrAddAsync_EnforcesDecodedPixelBudgetAcrossAnimatedFrames()
    {
        var animatedBytes = AnimatedWebpBytes(iterations: 0);

        var handler = new StubHttpMessageHandler(_ => Image(animatedBytes, "image/webp"));
        var cache = CreateCache(handler, new ImageCacheOptions
        {
            Enabled = true,
            Directory = "cache/images",
            CacheDays = 30,
            MaxBytes = 1024 * 1024,
            MaxDecodedPixels = 15_000,
            AllowedHosts = ["static.tvmaze.com"]
        });

        var error = await Assert.ThrowsAsync<ExternalApiException>(() => cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/animated.webp",
            false,
            CancellationToken.None,
            width: 80));

        Assert.Contains("across 2 frame", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.Exists(Path.Combine(_root, "cache", "images"))
            ? Directory.EnumerateFiles(Path.Combine(_root, "cache", "images"), "*.bin")
            : []);
    }

    [Fact]
    public async Task GetOrAddAsync_PreservesAnimatedWebpFramesTimingAndFiniteLoopCount()
    {
        var animatedBytes = AnimatedWebpBytes(iterations: 4);
        var handler = new StubHttpMessageHandler(_ => Image(animatedBytes, "image/webp"));
        var cache = CreateCache(handler);

        var cached = await cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/animated-output.webp",
            false,
            CancellationToken.None,
            width: 80,
            format: ImageCacheFormat.Webp);

        using var originalFrames = new MagickImageCollection(animatedBytes);
        using var cachedFrames = new MagickImageCollection(cached.FilePath);
        Assert.Equal(2, originalFrames.Count);
        Assert.Equal(2, cachedFrames.Count);
        Assert.Equal(4u, originalFrames[0].AnimationIterations);
        Assert.Equal(originalFrames[0].AnimationIterations, cachedFrames[0].AnimationIterations);
        Assert.Equal(originalFrames.Select(frame => frame.AnimationDelay), cachedFrames.Select(frame => frame.AnimationDelay));
        Assert.All(cachedFrames, frame =>
        {
            Assert.Equal(80u, frame.Width);
            Assert.Equal(80u, frame.Height);
        });
    }

    [Theory]
    [InlineData(ImageCacheFormat.Original)]
    [InlineData(ImageCacheFormat.Webp)]
    public async Task GetOrAddAsync_PreservesApngBytesForWidthVariants(ImageCacheFormat format)
    {
        var animatedBytes = AnimatedPngBytes();
        var handler = new StubHttpMessageHandler(_ => Image(animatedBytes, "image/png"));
        var cache = CreateCache(handler);

        var cached = await cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/animated.png",
            forceRefresh: false,
            CancellationToken.None,
            width: 80,
            format: format);

        Assert.Equal("image/png", cached.ContentType);
        Assert.Equal(animatedBytes, await File.ReadAllBytesAsync(cached.FilePath));
    }

    [Fact]
    public async Task GetOrAddAsync_RejectsApngDeclaredFrameCountAboveLimit()
    {
        var handler = new StubHttpMessageHandler(_ => Image(AnimatedPngBytes(declaredFrameCount: 1001), "image/png"));
        var cache = CreateCache(handler);

        var error = await Assert.ThrowsAsync<ExternalApiException>(() => cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/too-many-frames.png",
            forceRefresh: false,
            CancellationToken.None,
            width: 80));

        Assert.Contains("decoded frame limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetOrAddAsync_RejectsApngCanvasPixelsAcrossDeclaredFrames()
    {
        var handler = new StubHttpMessageHandler(_ => Image(AnimatedPngBytes(), "image/png"));
        var cache = CreateCache(handler, new ImageCacheOptions
        {
            Enabled = true,
            Directory = "cache/images",
            CacheDays = 30,
            MaxBytes = 1024 * 1024,
            MaxDecodedPixels = 15_000,
            AllowedHosts = ["static.tvmaze.com"]
        });

        var error = await Assert.ThrowsAsync<ExternalApiException>(() => cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/too-many-pixels.png",
            forceRefresh: false,
            CancellationToken.None,
            width: 80));

        Assert.Contains("decoded pixel limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("bounds")]
    [InlineData("chunk-length")]
    public async Task GetOrAddAsync_RejectsMalformedApngStructure(string corruption)
    {
        var malformedBytes = corruption switch
        {
            "bounds" => AnimatedPngBytes(secondFrameX: 1),
            "chunk-length" => ApngWithOversizedFrameControlChunk(),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        var handler = new StubHttpMessageHandler(_ => Image(malformedBytes, "image/png"));
        var cache = CreateCache(handler);

        var error = await Assert.ThrowsAsync<ExternalApiException>(() => cache.GetOrAddAsync(
            $"https://static.tvmaze.com/uploads/images/original_untouched/malformed-{corruption}.png",
            forceRefresh: false,
            CancellationToken.None,
            width: 80));

        Assert.Contains("APNG", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetOrAddAsync_UsesSharedDecodeLimitAcrossTransientCacheInstances()
    {
        var options = new ImageCacheOptions
        {
            Enabled = true,
            Directory = "cache/images",
            CacheDays = 30,
            MaxBytes = 1024 * 1024,
            MaxConcurrentDownloads = 2,
            MaxConcurrentDecodes = 1,
            AllowedHosts = ["static.tvmaze.com"]
        };
        var sharedDecodeLimiter = new ImageDecodeLimiter(Microsoft.Extensions.Options.Options.Create(options));
        using var heldDecodeSlot = await sharedDecodeLimiter.AcquireDecodeAsync(CancellationToken.None);
        var sample = SamplePngBytes();
        var firstCache = CreateCache(new StubHttpMessageHandler(_ => Image(sample, "image/png")), options, sharedDecodeLimiter);
        var secondCache = CreateCache(new StubHttpMessageHandler(_ => Image(sample, "image/png")), options, sharedDecodeLimiter);
        var first = firstCache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/decode-a.png",
            forceRefresh: false,
            CancellationToken.None,
            width: 80);
        var second = secondCache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/decode-b.png",
            forceRefresh: false,
            CancellationToken.None,
            width: 80);
        var cacheDirectory = Path.Combine(_root, "cache", "images");

        await WaitForStagedSourcesAsync(cacheDirectory, count: 2);
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.bin"));

        heldDecodeSlot.Dispose();
        await Task.WhenAll(first, second);
        Assert.Equal(2, Directory.EnumerateFiles(cacheDirectory, "*.bin").Count());
    }

    [Fact]
    public async Task GetOrAddAsync_BoundsStagedResizeQueueBeforeStartingAnotherDownload()
    {
        var options = new ImageCacheOptions
        {
            Enabled = true,
            Directory = "cache/images",
            CacheDays = 30,
            MaxBytes = 1024 * 1024,
            MaxConcurrentDownloads = 1,
            MaxConcurrentDecodes = 1,
            AllowedHosts = ["static.tvmaze.com"]
        };
        var limiter = new ImageDecodeLimiter(Microsoft.Extensions.Options.Options.Create(options));
        using var firstStageSlot = await limiter.AcquireStagingSlotAsync(CancellationToken.None);
        using var secondStageSlot = await limiter.AcquireStagingSlotAsync(CancellationToken.None);
        var handler = new StubHttpMessageHandler(_ => Image(SamplePngBytes(), "image/png"));
        var cache = CreateCache(handler, options, limiter);
        var request = cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/queued.png",
            forceRefresh: false,
            CancellationToken.None,
            width: 80);

        await Task.Delay(50);
        Assert.Empty(handler.Requests);

        firstStageSlot.Dispose();
        await request;
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetOrAddAsync_WithWidthCachesResizedJpegVariant()
    {
        var handler = new StubHttpMessageHandler(_ => Image(SamplePngBytes(), "image/png"));
        var cache = CreateCache(handler);

        var resized = await cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/1/1.png",
            forceRefresh: false,
            CancellationToken.None,
            width: 80);
        var bytes = await File.ReadAllBytesAsync(resized.FilePath);

        Assert.Single(handler.Requests);
        Assert.Equal("image/jpeg", resized.ContentType);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        using var image = new MagickImage(resized.FilePath);
        Assert.Equal(80u, image.Width);
        Assert.Equal(80u, image.Height);
    }

    [Fact]
    public async Task GetOrAddAsync_UsesImageBytesWhenJpegMimeAndUrlMislabelPng()
    {
        var handler = new StubHttpMessageHandler(_ => Image(SamplePngBytes(), "image/jpeg"));
        var cache = CreateCache(handler);

        var resized = await cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/mislabeled.jpg",
            forceRefresh: false,
            CancellationToken.None,
            width: 80);

        Assert.Equal("image/jpeg", resized.ContentType);
        using var image = new MagickImage(resized.FilePath);
        Assert.Equal(80u, image.Width);
        Assert.Equal(80u, image.Height);
    }

    [Fact]
    public async Task GetOrAddAsync_UsesImageBytesWhenIconMimeAndUrlMislabelPng()
    {
        var handler = new StubHttpMessageHandler(_ => Image(SamplePngBytes(), "image/x-icon"));
        var cache = CreateCache(handler);

        var resized = await cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/favicon.ico",
            forceRefresh: false,
            CancellationToken.None,
            width: 80);

        Assert.Equal("image/jpeg", resized.ContentType);
        using var image = new MagickImage(resized.FilePath);
        Assert.Equal(80u, image.Width);
        Assert.Equal(80u, image.Height);
    }

    [Fact]
    public async Task GetOrAddAsync_WithWebpNegotiationCachesSeparateWebpVariant()
    {
        var handler = new StubHttpMessageHandler(_ => Image(SamplePngBytes(), "image/png"));
        var cache = CreateCache(handler);

        var jpeg = await cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/1/format.png",
            forceRefresh: false,
            CancellationToken.None,
            width: 80);
        var webp = await cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/1/format.png",
            forceRefresh: false,
            CancellationToken.None,
            width: 80,
            format: ImageCacheFormat.Webp);
        var bytes = await File.ReadAllBytesAsync(webp.FilePath);

        Assert.Equal(2, handler.Requests.Count);
        Assert.NotEqual(jpeg.CacheKey, webp.CacheKey);
        Assert.Equal("image/webp", webp.ContentType);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
    }

    [Fact]
    public async Task GetOrAddAsync_WithTmdbRequestedWidthStoresRemoteVariantWithoutDecoding()
    {
        var originalBytes = new byte[] { 9, 8, 7, 6 };
        var handler = new StubHttpMessageHandler(_ => Image(originalBytes));
        var cache = CreateCache(handler);

        var cached = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w185/poster.jpg",
            forceRefresh: false,
            CancellationToken.None,
            width: 185);
        var bytes = await File.ReadAllBytesAsync(cached.FilePath);

        Assert.Single(handler.Requests);
        Assert.Equal(originalBytes, bytes);
        Assert.Equal("image/jpeg", cached.ContentType);
    }

    [Fact]
    public async Task GetOrAddAsync_WithLargerTmdbVariantStillResizesForCardWidth()
    {
        var handler = new StubHttpMessageHandler(_ => Image(SamplePngBytes(), "image/png"));
        var cache = CreateCache(handler);

        var resized = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/poster.png",
            forceRefresh: false,
            CancellationToken.None,
            width: 185);
        var bytes = await File.ReadAllBytesAsync(resized.FilePath);

        Assert.Single(handler.Requests);
        Assert.Equal("image/jpeg", resized.ContentType);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        using var image = new MagickImage(resized.FilePath);
        Assert.Equal(100u, image.Width);
        Assert.Equal(100u, image.Height);
    }

    [Fact]
    public async Task GetOrAddAsync_ConvertsMultipageTiffFromCommonsToResizedJpeg()
    {
        var tiffBytes = MultipageTiffBytes();
        var handler = new StubHttpMessageHandler(_ => Image(tiffBytes, "image/tiff"));
        var cache = CreateCache(handler, new ImageCacheOptions
        {
            Enabled = true,
            Directory = "cache/images",
            CacheDays = 30,
            MaxBytes = 1024 * 1024,
            AllowedHosts = ["static.tvmaze.com"]
        });

        var cached = await cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/multipage.tiff",
            false,
            CancellationToken.None,
            width: 80);

        using var output = new MagickImage(cached.FilePath);
        Assert.Equal("image/jpeg", cached.ContentType);
        Assert.Equal(80u, output.Width);
        Assert.Equal(80u, output.Height);
    }

    [Theory]
    [InlineData(MagickFormat.Bmp)]
    [InlineData(MagickFormat.Gif)]
    [InlineData(MagickFormat.Ico)]
    [InlineData(MagickFormat.Jpeg)]
    [InlineData(MagickFormat.Pbm)]
    [InlineData(MagickFormat.Pgm)]
    [InlineData(MagickFormat.Pnm)]
    [InlineData(MagickFormat.Png)]
    [InlineData(MagickFormat.Ppm)]
    [InlineData(MagickFormat.Qoi)]
    [InlineData(MagickFormat.Tga)]
    [InlineData(MagickFormat.Tiff)]
    [InlineData(MagickFormat.WebP)]
    public async Task GetOrAddAsync_ConvertsExistingRasterFormats(MagickFormat sourceFormat)
    {
        var bytes = RasterFixtureBytes(sourceFormat);
        var handler = new StubHttpMessageHandler(_ => Image(bytes, "image/octet-stream"));
        var cache = CreateCache(handler, new ImageCacheOptions
        {
            Enabled = true,
            Directory = "cache/images",
            CacheDays = 30,
            MaxBytes = 1024 * 1024,
            AllowedHosts = ["static.tvmaze.com"]
        });

        var cached = await cache.GetOrAddAsync(
            $"https://static.tvmaze.com/uploads/images/original_untouched/image.{sourceFormat.ToString().ToLowerInvariant()}",
            false,
            CancellationToken.None,
            width: 80);

        using var output = new MagickImage(cached.FilePath);
        Assert.Equal("image/jpeg", cached.ContentType);
        Assert.Equal(80u, output.Width);
        Assert.Equal(80u, output.Height);
    }

    [Fact]
    public async Task GetOrAddAsync_WithWidthRejectsOversizedSourceAndCleansTemporaryFiles()
    {
        var handler = new StubHttpMessageHandler(_ => Image(Enumerable.Repeat((byte)1, 2048).ToArray(), "image/png"));
        var cache = CreateCache(handler);

        await Assert.ThrowsAsync<ExternalApiException>(() => cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/1/oversized.png",
            forceRefresh: false,
            CancellationToken.None,
            width: 80));

        Assert.False(Directory.Exists(Path.Combine(_root, "cache", "images"))
            && Directory.EnumerateFiles(Path.Combine(_root, "cache", "images"), "*.tmp").Any());
    }

    [Fact]
    public async Task GetOrAddAsync_RejectsTruncatedEncodedImageAsControlledDecodeError()
    {
        var truncatedPng = SamplePngBytes()[..30];
        var handler = new StubHttpMessageHandler(_ => Image(truncatedPng, "image/png"));
        var cache = CreateCache(handler);

        var error = await Assert.ThrowsAsync<ExternalApiException>(() => cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/truncated.png",
            false,
            CancellationToken.None,
            width: 80));

        Assert.Contains("could not", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "cache", "images"))
            && Directory.EnumerateFiles(Path.Combine(_root, "cache", "images"), "*.tmp").Any());
    }

    [Theory]
    [InlineData("image/svg+xml", "<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://example.invalid/payload\"/></svg>")]
    [InlineData("image/x-msl", "push graphic-context\nimage over 0,0 1,1 'https://example.invalid/payload'\npop graphic-context\n") ]
    public async Task GetOrAddAsync_RejectsNonRasterCodersBeforeProcessing(string contentType, string content)
    {
        var handler = new StubHttpMessageHandler(_ => Image(System.Text.Encoding.UTF8.GetBytes(content), contentType));
        var cache = CreateCache(handler);

        var error = await Assert.ThrowsAsync<ExternalApiException>(() => cache.GetOrAddAsync(
            "https://static.tvmaze.com/uploads/images/original_untouched/disallowed.bin",
            false,
            CancellationToken.None,
            width: 80));

        Assert.Contains("image", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, "cache", "images"))
            && Directory.EnumerateFiles(Path.Combine(_root, "cache", "images"), "*.bin").Any());
    }

    [Fact]
    public async Task GetOrAddAsync_RejectsDeclaredOversizedResponseBeforeWriting()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = Image([1, 2, 3]);
            response.Content.Headers.ContentLength = 4096;
            return response;
        });
        var cache = CreateCache(handler);

        await Assert.ThrowsAsync<ExternalApiException>(() => cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/declared-large.jpg",
            forceRefresh: false,
            CancellationToken.None));

        var cacheDirectory = Path.Combine(_root, "cache", "images");
        Assert.True(!Directory.Exists(cacheDirectory) || !Directory.EnumerateFiles(cacheDirectory).Any());
    }

    [Fact]
    public async Task GetOrAddAsync_RejectsNonAllowedHosts()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Unexpected image fetch."));
        var cache = CreateCache(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetOrAddAsync(
            "https://example.com/poster.jpg",
            forceRefresh: false,
            CancellationToken.None));
    }

    [Fact]
    public async Task CleanupAsync_RemovesImageFilesOlderThanRetention()
    {
        var handler = new StubHttpMessageHandler(_ => Image([1, 2, 3]));
        var cache = CreateCache(handler);
        var oldImage = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/old.jpg",
            forceRefresh: false,
            CancellationToken.None);
        var currentImage = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/current.jpg",
            forceRefresh: false,
            CancellationToken.None);
        var oldMetadataPath = MetadataPath(oldImage);
        var oldTimestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        await File.WriteAllTextAsync(
            oldMetadataPath,
            """
            {"sourceUrl":"https://image.tmdb.org/t/p/w342/old.jpg","contentType":"image/jpeg","fetchedUtc":"2026-02-01T00:00:00+00:00"}
            """);
        File.SetLastWriteTimeUtc(oldImage.FilePath, oldTimestamp);
        File.SetLastWriteTimeUtc(oldMetadataPath, oldTimestamp);

        var removed = await ((IImageCacheMaintenance)cache).CleanupAsync(
            new DateTimeOffset(2026, 5, 8, 10, 0, 0, TimeSpan.Zero),
            TimeSpan.FromDays(60),
            CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldImage.FilePath));
        Assert.False(File.Exists(oldMetadataPath));
        Assert.True(File.Exists(currentImage.FilePath));
        Assert.True(File.Exists(MetadataPath(currentImage)));
    }

    [Fact]
    public async Task CleanupAsync_KeepsARecentlyRetiredGenerationForLookupsThatHaveNotOpenedIt()
    {
        byte version = 0;
        var cache = CreateCache(new StubHttpMessageHandler(_ => Image([++version])));
        const string url = "https://image.tmdb.org/t/p/w342/retired.jpg";
        var old = await cache.GetOrAddAsync(url, false, CancellationToken.None);
        File.SetLastWriteTimeUtc(old.FilePath, DateTime.UtcNow.AddDays(-100));
        var current = await cache.GetOrAddAsync(url, true, CancellationToken.None);

        await cache.CleanupAsync(DateTimeOffset.UtcNow, TimeSpan.FromDays(60), CancellationToken.None);

        Assert.True(File.Exists(old.FilePath));
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(old.FilePath));
        Assert.Equal(new byte[] { 2 }, await File.ReadAllBytesAsync(current.FilePath));
    }

    [Fact]
    public async Task CleanupAsync_PreservesCurrentGenerationAndRemovesExpiredOrphanGeneration()
    {
        byte imageVersion = 0;
        var handler = new StubHttpMessageHandler(_ => Image([++imageVersion, 3, 8]));
        var cache = CreateCache(handler);
        const string imageUrl = "https://image.tmdb.org/t/p/w342/generations.jpg";
        var oldGeneration = await cache.GetOrAddAsync(imageUrl, false, CancellationToken.None);
        var currentGeneration = await cache.GetOrAddAsync(imageUrl, true, CancellationToken.None);
        File.SetLastWriteTimeUtc(oldGeneration.FilePath, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await ((IImageCacheMaintenance)cache).CleanupAsync(
            DateTimeOffset.UtcNow,
            TimeSpan.FromDays(60),
            CancellationToken.None);

        Assert.False(File.Exists(oldGeneration.FilePath));
        Assert.True(File.Exists(currentGeneration.FilePath));
        Assert.True(File.Exists(MetadataPath(currentGeneration)));
        Assert.Equal(currentGeneration.ContentHash, (await cache.GetOrAddAsync(imageUrl, false, CancellationToken.None)).ContentHash);
    }

    [Fact]
    public async Task CleanupAsync_RemovesOldCorruptMetadataAndMatchingImageFile()
    {
        var handler = new StubHttpMessageHandler(_ => Image([1, 2, 3]));
        var cache = CreateCache(handler);
        var oldImage = await cache.GetOrAddAsync(
            "https://image.tmdb.org/t/p/w342/corrupt.jpg",
            forceRefresh: false,
            CancellationToken.None);
        var metadataPath = MetadataPath(oldImage);
        var oldTimestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        await File.WriteAllTextAsync(metadataPath, "{not valid json");
        File.SetLastWriteTimeUtc(oldImage.FilePath, oldTimestamp);
        File.SetLastWriteTimeUtc(metadataPath, oldTimestamp);

        var removed = await ((IImageCacheMaintenance)cache).CleanupAsync(
            new DateTimeOffset(2026, 5, 8, 10, 0, 0, TimeSpan.Zero),
            TimeSpan.FromDays(60),
            CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldImage.FilePath));
        Assert.False(File.Exists(metadataPath));
    }

    [Fact]
    public async Task CleanupAsync_RemovesOldTemporaryImageCacheFiles()
    {
        var handler = new StubHttpMessageHandler(_ => Image([1, 2, 3]));
        var cache = CreateCache(handler);
        var cacheDirectory = Path.Combine(_root, "cache", "images");
        Directory.CreateDirectory(cacheDirectory);
        var tempImagePath = Path.Combine(cacheDirectory, "poster.bin.abandoned.tmp");
        var tempSourcePath = Path.Combine(cacheDirectory, "poster.bin.abandoned.source.tmp");
        await File.WriteAllTextAsync(tempImagePath, "partial", CancellationToken.None);
        await File.WriteAllTextAsync(tempSourcePath, "source", CancellationToken.None);
        var oldTimestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(tempImagePath, oldTimestamp);
        File.SetLastWriteTimeUtc(tempSourcePath, oldTimestamp);

        var removed = await ((IImageCacheMaintenance)cache).CleanupAsync(
            new DateTimeOffset(2026, 5, 8, 10, 0, 0, TimeSpan.Zero),
            TimeSpan.FromDays(60),
            CancellationToken.None);

        Assert.Equal(2, removed);
        Assert.False(File.Exists(tempImagePath));
        Assert.False(File.Exists(tempSourcePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileImageCache CreateCache(
        HttpMessageHandler handler,
        ImageCacheOptions? options = null,
        ImageDecodeLimiter? decodeLimiter = null)
    {
        return new FileImageCache(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(options ?? new ImageCacheOptions
            {
                Enabled = true,
                Directory = "cache/images",
                CacheDays = 30,
                MaxBytes = 1024,
                AllowedHosts = ["image.tmdb.org", ".media-amazon.com", "static.tvmaze.com"]
            }),
            new FakeWebHostEnvironment(_root),
            NullLogger<FileImageCache>.Instance,
            decodeLimiter: decodeLimiter);
    }

    private static string MetadataPath(CachedImage image)
        => Path.Combine(Path.GetDirectoryName(image.FilePath)!, $"{image.CacheKey}.json");

    private static async Task WaitForStagedSourcesAsync(string directory, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.source.tmp").Count() >= count)
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"Expected {count} staged image sources in {directory}.");
    }

    private static HttpResponseMessage Image(byte[] bytes, string contentType = "image/jpeg")
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers =
                {
                    ContentType = new MediaTypeHeaderValue(contentType)
                }
            }
        };
    }

    private static HttpResponseMessage StalledImage()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingReadStream())
            {
                Headers =
                {
                    ContentType = new MediaTypeHeaderValue("image/jpeg")
                }
            }
        };
    }

    private byte[] SamplePngBytes(byte red = 220, byte green = 30, byte blue = 30)
    {
        ImageMagickRuntime.EnsureInitialized(_root);
        const int width = 100;
        const int height = 100;
        using var rawPixels = new MemoryStream();
        for (var row = 0; row < height; row++)
        {
            rawPixels.WriteByte(0); // PNG filter type: None
            for (var column = 0; column < width; column++)
            {
                rawPixels.WriteByte(red);
                rawPixels.WriteByte(green);
                rawPixels.WriteByte(blue);
                rawPixels.WriteByte(255);
            }
        }

        using var compressedPixels = new MemoryStream();
        using (var compressor = new ZLibStream(compressedPixels, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            rawPixels.Position = 0;
            rawPixels.CopyTo(compressor);
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..8], height);
        header[8] = 8;
        header[9] = 6; // RGBA
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WritePngChunk(png, "IHDR"u8, header);
        WritePngChunk(png, "IDAT"u8, compressedPixels.ToArray());
        WritePngChunk(png, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private byte[] AnimatedWebpBytes(uint iterations)
    {
        ImageMagickRuntime.EnsureInitialized(_root);
        using var frames = new MagickImageCollection();
        var first = new MagickImage(SamplePngBytes(255, 0, 0))
        {
            AnimationDelay = 12,
            AnimationIterations = iterations
        };
        var second = new MagickImage(SamplePngBytes(0, 0, 255))
        {
            AnimationDelay = 25,
            AnimationIterations = iterations
        };
        frames.Add(first);
        frames.Add(second);
        return frames.ToByteArray(MagickFormat.WebP);
    }

    private byte[] MultipageTiffBytes()
    {
        ImageMagickRuntime.EnsureInitialized(_root);
        using var pages = new MagickImageCollection();
        pages.Add(new MagickImage(SamplePngBytes(255, 0, 0)));
        pages.Add(new MagickImage(SamplePngBytes(0, 0, 255)));
        return pages.ToByteArray(MagickFormat.Tiff);
    }

    private byte[] AnimatedPngBytes(uint declaredFrameCount = 2, uint secondFrameX = 0)
    {
        var staticPng = SamplePngBytes();
        var header = ReadPngChunkData(staticPng, "IHDR"u8);
        var imageData = ReadPngChunkData(staticPng, "IDAT"u8);
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        WritePngChunk(png, "IHDR"u8, header);

        Span<byte> animationControl = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(animationControl[..4], declaredFrameCount);
        BinaryPrimitives.WriteUInt32BigEndian(animationControl[4..], 0);
        WritePngChunk(png, "acTL"u8, animationControl);

        WritePngChunk(png, "fcTL"u8, FrameControl(sequence: 0, xOffset: 0));
        WritePngChunk(png, "IDAT"u8, imageData);
        WritePngChunk(png, "fcTL"u8, FrameControl(sequence: 1, xOffset: secondFrameX));
        var frameData = new byte[sizeof(uint) + imageData.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frameData.AsSpan(0, sizeof(uint)), 2);
        imageData.CopyTo(frameData.AsSpan(sizeof(uint)));
        WritePngChunk(png, "fdAT"u8, frameData);
        WritePngChunk(png, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static byte[] FrameControl(uint sequence, uint xOffset)
    {
        var frameControl = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(frameControl.AsSpan(0, 4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(frameControl.AsSpan(4, 4), 100);
        BinaryPrimitives.WriteUInt32BigEndian(frameControl.AsSpan(8, 4), 100);
        BinaryPrimitives.WriteUInt32BigEndian(frameControl.AsSpan(12, 4), xOffset);
        BinaryPrimitives.WriteUInt32BigEndian(frameControl.AsSpan(16, 4), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frameControl.AsSpan(20, 2), 12);
        BinaryPrimitives.WriteUInt16BigEndian(frameControl.AsSpan(22, 2), 100);
        frameControl[24] = 0; // APNG_DISPOSE_OP_NONE
        frameControl[25] = 0; // APNG_BLEND_OP_SOURCE
        return frameControl;
    }

    private byte[] ApngWithOversizedFrameControlChunk()
    {
        var png = AnimatedPngBytes();
        var frameControlType = "fcTL"u8;
        var chunkTypeOffset = FindSequence(png, frameControlType);
        Assert.True(chunkTypeOffset >= sizeof(uint));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(chunkTypeOffset - sizeof(uint), sizeof(uint)), uint.MaxValue);
        return png;
    }

    private static byte[] ReadPngChunkData(byte[] png, ReadOnlySpan<byte> chunkType)
    {
        var offset = 8;
        while (offset <= png.Length - 12)
        {
            var chunkLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            var dataOffset = offset + 8;
            if (png.AsSpan(offset + 4, 4).SequenceEqual(chunkType))
            {
                return png.AsSpan(dataOffset, chunkLength).ToArray();
            }

            offset = checked(dataOffset + chunkLength + sizeof(uint));
        }

        throw new InvalidOperationException($"PNG chunk {System.Text.Encoding.ASCII.GetString(chunkType)} was not found.");
    }

    private static int FindSequence(byte[] bytes, ReadOnlySpan<byte> sequence)
    {
        for (var offset = 0; offset <= bytes.Length - sequence.Length; offset++)
        {
            if (bytes.AsSpan(offset, sequence.Length).SequenceEqual(sequence))
            {
                return offset;
            }
        }

        return -1;
    }

    private byte[] RasterFixtureBytes(MagickFormat format)
    {
        ImageMagickRuntime.EnsureInitialized(_root);
        using var image = new MagickImage(SamplePngBytes());
        return image.ToByteArray(format);
    }

    private byte[] RepeatedOnePixelGifFrameBytes(int frameCount)
    {
        ImageMagickRuntime.EnsureInitialized(_root);
        using var image = new MagickImage(SamplePngBytes());
        image.Resize(new MagickGeometry(1, 1));
        var singleFrameGif = image.ToByteArray(MagickFormat.Gif);
        var imageSeparator = Array.IndexOf(singleFrameGif, (byte)0x2c);
        Assert.True(imageSeparator >= 0, "Expected the native single-frame GIF fixture to contain an image descriptor.");

        var cursor = imageSeparator + 10;
        var descriptorPacked = singleFrameGif[imageSeparator + 9];
        if ((descriptorPacked & 0x80) != 0)
        {
            cursor += 3 * (1 << ((descriptorPacked & 0x07) + 1));
        }

        cursor++; // LZW minimum code size
        while (true)
        {
            Assert.True(cursor < singleFrameGif.Length, "Expected a complete GIF image data block.");
            var subBlockLength = singleFrameGif[cursor++];
            if (subBlockLength == 0)
            {
                break;
            }

            cursor += subBlockLength;
        }

        Assert.InRange(cursor, 1, singleFrameGif.Length);
        var prefix = singleFrameGif.AsSpan(0, imageSeparator).ToArray();
        var frame = singleFrameGif.AsSpan(imageSeparator, cursor - imageSeparator).ToArray();
        var suffix = singleFrameGif.AsSpan(cursor).ToArray();
        using var repeated = new MemoryStream(prefix.Length + (long)frame.Length * frameCount + suffix.Length > int.MaxValue
            ? throw new InvalidOperationException("Repeated GIF fixture is too large.")
            : prefix.Length + frame.Length * frameCount + suffix.Length);
        repeated.Write(prefix);
        for (var index = 0; index < frameCount; index++)
        {
            repeated.Write(frame);
        }

        repeated.Write(suffix);
        return repeated.ToArray();
    }

    private static void WritePngChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        output.Write(length);
        output.Write(type);
        output.Write(data);

        var crc = uint.MaxValue;
        foreach (var value in type)
        {
            crc = UpdatePngCrc(crc, value);
        }
        foreach (var value in data)
        {
            crc = UpdatePngCrc(crc, value);
        }

        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        output.Write(length);
    }

    private static uint UpdatePngCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
        }

        return crc;
    }

    private static byte[] PngBytesWithDimensions(byte[] source, int width, int height)
    {
        var png = (byte[])source.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), (uint)height);

        var crc = uint.MaxValue;
        for (var index = 12; index < 29; index++)
        {
            crc ^= png[index];
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29, 4), ~crc);
        return png;
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public FakeWebHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            WebRootPath = Path.Combine(contentRootPath, "wwwroot");
        }

        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; } = default!;
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = default!;
    }

    private sealed class BlockingImageHandler(byte[] imageBytes) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public Task RequestStarted => _requestStarted.Task;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public void Release()
        {
            _release.TrySetResult();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            _requestStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return Image(imageBytes);
        }
    }

    private sealed class StallingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
