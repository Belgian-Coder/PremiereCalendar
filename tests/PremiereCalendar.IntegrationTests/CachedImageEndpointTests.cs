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

// ImageMagick's native resource limits (memory, list length) are process-wide: concurrent decodes in other tests make
// "cache resources exhausted" fail. All image tests share one collection, so they run one after the other.
[Collection(ImageMagickCollection.Name)]
public sealed class CachedImageEndpointTests(CachedImageEndpointTests.HostFixture hosts)
    : IClassFixture<CachedImageEndpointTests.HostFixture>, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-cached-image-endpoint-{Guid.NewGuid():N}");

    [Fact]
    public async Task CachedImageEndpoint_UsesEncodedContentHashAndShortFreshnessForStaleImage()
    {
        var path = Path.Combine(_root, "stale.bin");
        Directory.CreateDirectory(_root);
        var bytes = new byte[] { 4, 3, 2, 1 };
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
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

        using var response = await client.GetAsync(factory.Request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{hash}\"", response.Headers.ETag?.Tag);
        Assert.Contains("max-age=60", response.Headers.CacheControl?.ToString());
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
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

        using var response = await client.GetAsync(factory.Request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task CachedImageEndpoint_StillMapsInvalidRemoteImageRequestToNotFound()
    {
        var cache = new StubImageCache((_, _, _, _, _) => Task.FromException<CachedImage>(new ExternalApiException("invalid image response")));
        using var factory = CreateFactory(cache);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(factory.Request, TestContext.Current.CancellationToken);

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
        using var factory = CreateFactory(cache, imageUrl);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(factory.Request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var contentHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{firstGeneration.ContentHash}\"", response.Headers.ETag?.Tag);
        Assert.Equal(firstGeneration.ContentHash, contentHash);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 1 }, body);
        Assert.True(File.Exists(firstGeneration.FilePath));
    }

    private async Task<CachedImage> WriteImageAsync(string contentType, string name = "image.bin")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        return new CachedImage(path, contentType, DateTimeOffset.UtcNow, "key", TimeSpan.FromMinutes(5), new string('b', 64));
    }

    [Fact]
    public async Task CachedImageEndpoint_SendsLockdownHeadersAndRasterContentType()
    {
        var image = await WriteImageAsync("IMAGE/JPEG; charset=binary");
        using var factory = CreateFactory(new StubImageCache((_, _, _, _, _) => Task.FromResult(image)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(factory.Request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("default-src 'none'; sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    public async Task CachedImageEndpoint_RejectsNonRasterContentTypeWithoutBody(string contentType)
    {
        var image = await WriteImageAsync(contentType);
        using var factory = CreateFactory(new StubImageCache((_, _, _, _, _) => Task.FromResult(image)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(factory.Request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("image/avif")]
    public async Task CachedImageEndpoint_ServesEveryAllowedRasterType(string contentType)
    {
        var image = await WriteImageAsync(contentType);
        using var factory = CreateFactory(new StubImageCache((_, _, _, _, _) => Task.FromResult(image)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(factory.Request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CachedImageEndpoint_FailuresAreEmptyBodiedInsteadOfHtmlStatusPages(HttpStatusCode expected)
    {
        Exception failure = expected switch
        {
            HttpStatusCode.BadRequest => new ArgumentException("bad url"),
            HttpStatusCode.NotFound => new ExternalApiException("invalid image response"),
            _ => new HttpRequestException("upstream unavailable")
        };
        using var factory = CreateFactory(new StubImageCache((_, _, _, _, _) => Task.FromException<CachedImage>(failure)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(factory.Request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CachedImageEndpoint_UnexpectedFailureIsEmptyBodied500WithLockdownHeaders()
    {
        using var factory = CreateFactory(new StubImageCache((_, _, _, _, _) =>
            Task.FromException<CachedImage>(new InvalidOperationException("boom"))));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(factory.Request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("default-src 'none'; sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task CachedImageEndpoint_IgnoresPublicRefreshParameter()
    {
        var image = await WriteImageAsync("image/jpeg");
        var forceRefreshValues = new List<bool>();
        using var factory = CreateFactory(new StubImageCache((_, forceRefresh, _, _, _) =>
        {
            forceRefreshValues.Add(forceRefresh);
            return Task.FromResult(image);
        }));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{factory.Request}&refresh=true", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([false], forceRefreshValues);
    }

    [Theory]
    [InlineData("&w=185", 185)]
    [InlineData("&w=64", 185)]
    [InlineData("&w=640", 185)]
    [InlineData("&w=300", 185)]
    [InlineData("&w=100000", 185)]
    [InlineData("&w=0", null)]
    [InlineData("&w=-7", null)]
    [InlineData("", null)]
    public async Task CachedImageEndpoint_SnapsWidthToAllowedVariants(string query, int? expectedWidth)
    {
        var image = await WriteImageAsync("image/jpeg");
        int? seenWidth = -1;
        using var factory = CreateFactory(new StubImageCache((_, _, _, width, _) =>
        {
            seenWidth = width;
            return Task.FromResult(image);
        }));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(factory.Request + query, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedWidth, seenWidth);
    }

    // The endpoint is served by one shared host. Each test registers its stub under a source URL of its own, which
    // the endpoint passes straight through to the image cache, so tests cannot see each other's stubs.
    private ImageEndpoint CreateFactory(IImageCache imageCache, string? sourceUrl = null)
        => hosts.Register(imageCache, sourceUrl ?? $"https://image.tmdb.org/t/p/w342/{Guid.NewGuid():N}.jpg");

    public sealed class ImageEndpoint(HostFixture host, string sourceUrl) : IDisposable
    {
        public string Request { get; } = $"/cached-image?url={Uri.EscapeDataString(sourceUrl)}";
        public HttpClient CreateClient() => host.Factory.CreateClient();
        public void Dispose() => host.Unregister(sourceUrl);
    }

    public sealed class HostFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-cached-image-host-{Guid.NewGuid():N}");
        private readonly PostgresTestDatabase _database;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IImageCache> _stubs = new();

        public HostFixture()
        {
            var settings = new Dictionary<string, string?>
            {
                ["ProviderScheduler:Enabled"] = "false",
                ["CalendarWarmup:Enabled"] = "false",
                ["ProviderDeltaSync:Enabled"] = "false",
                ["ImdbDataset:Enabled"] = "false",
                ["CalendarCache:Directory"] = Path.Combine(_root, "calendar-cache"),
                ["ImageCache:Directory"] = Path.Combine(_root, "image-cache"),
                ["Telemetry:LogDirectory"] = Path.Combine(_root, "logs")
            };
            _database = PostgresTestDatabase.Create();
            foreach (var setting in _database.AppSettings) settings[setting.Key] = setting.Value;
            var dispatcher = new DispatchingImageCache(_stubs);
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IImageCache>();
                    services.AddSingleton<IImageCache>(dispatcher);
                });
            });
        }

        public WebApplicationFactory<Program> Factory { get; }

        internal ImageEndpoint Register(IImageCache imageCache, string sourceUrl)
        {
            _stubs[sourceUrl] = imageCache;
            return new ImageEndpoint(this, sourceUrl);
        }

        internal void Unregister(string sourceUrl) => _stubs.TryRemove(sourceUrl, out _);

        public void Dispose()
        {
            Factory.Dispose();
            _database.Dispose();
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class DispatchingImageCache(System.Collections.Concurrent.ConcurrentDictionary<string, IImageCache> stubs) : IImageCache
    {
        public Task<CachedImage> GetOrAddAsync(
            string sourceUrl,
            bool forceRefresh,
            CancellationToken cancellationToken,
            int? width = null,
            ImageCacheFormat format = ImageCacheFormat.Original)
            => stubs.TryGetValue(sourceUrl, out var stub)
                ? stub.GetOrAddAsync(sourceUrl, forceRefresh, cancellationToken, width, format)
                : throw new InvalidOperationException($"No test stub is registered for {sourceUrl}.");
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
                Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, (byte)version])
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        }
    }
}
