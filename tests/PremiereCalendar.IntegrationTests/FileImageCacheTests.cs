using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Options;
using PremiereCalendar.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PremiereCalendar.IntegrationTests;

public sealed class FileImageCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-image-cache-{Guid.NewGuid():N}");

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
        var metadataPath = Path.ChangeExtension(cached.FilePath, ".json");
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
        Assert.Equal([7, 7, 7], await File.ReadAllBytesAsync(stale.FilePath));
        Assert.Equal(2, handler.Requests.Count);
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
        var oldMetadataPath = Path.ChangeExtension(oldImage.FilePath, ".json");
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
        Assert.True(File.Exists(Path.ChangeExtension(currentImage.FilePath, ".json")));
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
        var metadataPath = Path.ChangeExtension(oldImage.FilePath, ".json");
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

    private FileImageCache CreateCache(HttpMessageHandler handler)
    {
        return new FileImageCache(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(new ImageCacheOptions
            {
                Enabled = true,
                Directory = "cache/images",
                CacheDays = 30,
                MaxBytes = 1024,
                AllowedHosts = ["image.tmdb.org", ".media-amazon.com", "static.tvmaze.com"]
            }),
            new FakeWebHostEnvironment(_root),
            NullLogger<FileImageCache>.Instance);
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

    private static byte[] SamplePngBytes()
    {
        using var image = new Image<Rgba32>(100, 100, new Rgba32(220, 30, 30));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
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
}
