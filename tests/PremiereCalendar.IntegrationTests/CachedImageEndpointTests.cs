using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class CachedImageEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-cached-image-endpoint-{Guid.NewGuid():N}");

    [Fact]
    public async Task CachedImageEndpoint_UsesEncodedContentHashAndShortFreshnessForStaleImage()
    {
        var path = Path.Combine(_root, "stale.bin");
        Directory.CreateDirectory(_root);
        var bytes = new byte[] { 4, 3, 2, 1 };
        await File.WriteAllBytesAsync(path, bytes);
        var hash = new string('a', 64);
        var cache = new StubImageCache((_, _, _, _, _) => Task.FromResult(new CachedImage(
            path,
            "image/jpeg",
            DateTimeOffset.UtcNow,
            "cache-key-is-not-the-validator",
            TimeSpan.FromMinutes(1),
            hash,
            IsStale: true)));
        using var factory = CreateFactory(cache);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/cached-image?url=https%3A%2F%2Fimage.tmdb.org%2Ft%2Fp%2Fw342%2Fposter.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{hash}\"", response.Headers.ETag?.Tag);
        Assert.Contains("max-age=60", response.Headers.CacheControl?.ToString());
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedImageEndpoint_ReturnsNoStore503ForTransientImageFailure(bool nativeCapacityFailure)
    {
        Exception failure = nativeCapacityFailure
            ? new IOException("Native image cache capacity is temporarily exhausted.")
            : new HttpRequestException("upstream unavailable");
        var cache = new StubImageCache((_, _, _, _, _) => Task.FromException<CachedImage>(failure));
        using var factory = CreateFactory(cache);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/cached-image?url=https%3A%2F%2Fimage.tmdb.org%2Ft%2Fp%2Fw342%2Fposter.jpg");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task CachedImageEndpoint_StillMapsInvalidRemoteImageRequestToNotFound()
    {
        var cache = new StubImageCache((_, _, _, _, _) => Task.FromException<CachedImage>(new ExternalApiException("invalid image response")));
        using var factory = CreateFactory(cache);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/cached-image?url=https%3A%2F%2Fimage.tmdb.org%2Ft%2Fp%2Fw342%2Fposter.jpg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CachedImageEndpoint_RefreshAfterLookupKeepsEtagMatchedToOpenedGeneration()
    {
        const string imageUrl = "https://image.tmdb.org/t/p/w342/poster.jpg";
        var cacheDirectory = Path.Combine(_root, "real-image-cache");
        using var httpClient = new HttpClient(new VersionedImageHandler());
        var realCache = new FileImageCache(
            httpClient,
            Microsoft.Extensions.Options.Options.Create(new ImageCacheOptions
            {
                Enabled = true,
                Directory = cacheDirectory,
                AllowedHosts = ["image.tmdb.org"]
            }),
            new FakeWebHostEnvironment(_root),
            NullLogger<FileImageCache>.Instance);
        var firstGeneration = await realCache.GetOrAddAsync(imageUrl, false, CancellationToken.None);
        var cache = new StubImageCache(async (url, _, cancellationToken, width, format) =>
        {
            var endpointSnapshot = await realCache.GetOrAddAsync(url, false, cancellationToken, width, format);
            await realCache.GetOrAddAsync(url, true, cancellationToken, width, format);
            return endpointSnapshot;
        });
        using var factory = CreateFactory(cache);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/cached-image?url=https%3A%2F%2Fimage.tmdb.org%2Ft%2Fp%2Fw342%2Fposter.jpg");
        var body = await response.Content.ReadAsByteArrayAsync();
        var contentHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{firstGeneration.ContentHash}\"", response.Headers.ETag?.Tag);
        Assert.Equal(firstGeneration.ContentHash, contentHash);
        Assert.Equal(new byte[] { 1, 2, 3 }, body);
        Assert.True(File.Exists(firstGeneration.FilePath));
    }

    private WebApplicationFactory<Program> CreateFactory(IImageCache imageCache)
    {
        var instanceRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string?>
        {
            ["ProviderScheduler:Enabled"] = "false",
            ["CalendarWarmup:Enabled"] = "false",
            ["ProviderDeltaSync:Enabled"] = "false",
            ["ImdbDataset:Enabled"] = "false",
            ["AppDatabase:Path"] = Path.Combine(instanceRoot, "data", "calendar.db"),
            ["AppDatabase:MigrationBackupDirectory"] = Path.Combine(instanceRoot, "backups"),
            ["CalendarCache:Directory"] = Path.Combine(instanceRoot, "calendar-cache"),
            ["ImageCache:Directory"] = Path.Combine(instanceRoot, "image-cache"),
            ["Telemetry:LogDirectory"] = Path.Combine(instanceRoot, "logs")
        };

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IImageCache>();
                services.AddSingleton(imageCache);
            });
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class StubImageCache(
        Func<string, bool, CancellationToken, int?, ImageCacheFormat, Task<CachedImage>> getOrAdd)
        : IImageCache
    {
        public Task<CachedImage> GetOrAddAsync(
            string sourceUrl,
            bool forceRefresh,
            CancellationToken cancellationToken,
            int? width = null,
            ImageCacheFormat format = ImageCacheFormat.Original)
        {
            return getOrAdd(sourceUrl, forceRefresh, cancellationToken, width, format);
        }
    }

    private sealed class FakeWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public string WebRootPath { get; set; } = Path.Combine(contentRootPath, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = default!;
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = default!;
    }

    private sealed class VersionedImageHandler : HttpMessageHandler
    {
        private int _version;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var version = Interlocked.Increment(ref _version);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([(byte)version, 2, 3])
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        }
    }
}
