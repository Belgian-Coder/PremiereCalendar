using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class OmdbClientIntegrationTests
{
    [Fact]
    public async Task GetByImdbIdAsync_SkipsHttpWhenDisabled()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("OMDb should not be called."));
        var client = CreateOmdbClient(handler, enabled: false);

        var result = await client.GetByImdbIdAsync("tt0000100", CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetByImdbIdAsync_ReturnsFalseResponseWithoutThrowing()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("omdb/response-false.json")));
        var client = CreateOmdbClient(handler, enabled: true);

        var result = await client.GetByImdbIdAsync("tt-missing", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("False", result.Response);
    }

    [Fact]
    public async Task GetByImdbIdAsync_ThrowsHelpfulExceptionWhenRequestLimitIsReached()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                "{\"Response\":\"False\",\"Error\":\"Request limit reached!\"}",
                System.Text.Encoding.UTF8,
                "application/json")
        });
        var client = CreateOmdbClient(handler, enabled: true);

        var error = await Assert.ThrowsAsync<ExternalApiException>(() =>
            client.GetByImdbIdAsync("tt0000100", CancellationToken.None));

        Assert.Contains("Request limit reached", error.Message);
    }

    [Fact]
    public async Task GetByImdbIdAsync_CachesSuccessfulResponse()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("omdb/by-imdb-id-success.json")));
        var client = CreateOmdbClient(handler, enabled: true);

        await client.GetByImdbIdAsync("tt0000100", CancellationToken.None);
        await client.GetByImdbIdAsync("tt0000100", CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetByImdbIdAsync_CoalescesConcurrentCacheMisses()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var handler = new AsyncStubHttpMessageHandler(async _ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return StubHttpMessageHandler.Json(Fixture.Read("omdb/by-imdb-id-success.json"));
        });
        var client = CreateOmdbClient(handler, enabled: true);

        var first = client.GetByImdbIdAsync("tt0000100", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.GetByImdbIdAsync("tt0000100", CancellationToken.None);

        release.SetResult();

        Assert.NotNull(await first);
        Assert.NotNull(await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetByImdbIdAsync_ForceRefreshBypassesCacheForSameRequest()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("omdb/by-imdb-id-success.json")));
        var client = CreateOmdbClient(handler, enabled: true);

        await client.GetByImdbIdAsync("tt0000100", CancellationToken.None);
        await client.GetByImdbIdAsync("tt0000100", CancellationToken.None);
        await client.GetByImdbIdAsync("tt0000100", CancellationToken.None, forceRefresh: true);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetByImdbIdAsync_ReturnsNullWhenHttpClientTimesOutWithoutCallerCancellation()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException("Simulated HTTP timeout."));
        var client = CreateOmdbClient(handler, enabled: true);

        var result = await client.GetByImdbIdAsync("tt0000100", CancellationToken.None);

        Assert.Null(result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetByImdbIdAsync_UsesPersistentCacheAcrossClientInstances()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateCacheStore(root);
            var firstHandler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("omdb/by-imdb-id-success.json")));
            var firstClient = CreateOmdbClient(firstHandler, enabled: true, cacheStore: store);

            await firstClient.GetByImdbIdAsync("tt0000100", CancellationToken.None);

            var secondHandler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Persisted OMDb cache should be used."));
            var secondClient = CreateOmdbClient(secondHandler, enabled: true, cacheStore: CreateCacheStore(root));

            var cached = await secondClient.GetByImdbIdAsync("tt0000100", CancellationToken.None);

            Assert.NotNull(cached);
            Assert.Equal("8.2", cached.ImdbRating);
            Assert.Single(firstHandler.Requests);
            Assert.Empty(secondHandler.Requests);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task GetByImdbIdAsync_SkipsHttpDuringPersistedRateLimitCooldown()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateCacheStore(root);
            await store.MarkRateLimitedAsync(
                DateTimeOffset.UtcNow.AddHours(1),
                "Request limit reached!",
                CancellationToken.None);
            var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("OMDb should not be called during cooldown."));
            var client = CreateOmdbClient(handler, enabled: true, cacheStore: store);

            var result = await client.GetByImdbIdAsync("tt0000100", CancellationToken.None);

            Assert.Null(result);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task GetByImdbIdAsync_TracksRateLimitReturnedWithHttpOk()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateCacheStore(root);
            var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
                "{\"Response\":\"False\",\"Error\":\"Request limit reached!\"}"));
            var client = CreateOmdbClient(handler, enabled: true, cacheStore: store);

            var error = await Assert.ThrowsAsync<ExternalApiException>(() =>
                client.GetByImdbIdAsync("tt0000100", CancellationToken.None));
            var state = await store.GetProviderStateAsync(CancellationToken.None);

            Assert.Contains("Request limit reached", error.Message);
            Assert.NotNull(state.RateLimitedUntilUtc);

            var secondHandler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Cooldown should skip HTTP."));
            var secondClient = CreateOmdbClient(secondHandler, enabled: true, cacheStore: CreateCacheStore(root));
            var result = await secondClient.GetByImdbIdAsync("tt0000200", CancellationToken.None);

            Assert.Null(result);
            Assert.Empty(secondHandler.Requests);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static OmdbClient CreateOmdbClient(
        HttpMessageHandler handler,
        bool enabled,
        IOmdbCacheStore? cacheStore = null)
    {
        return new OmdbClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://www.omdbapi.com/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new OmdbOptions
            {
                Enabled = enabled,
                ApiKey = enabled ? "test-key" : null,
                CacheDays = 90,
                RateLimitBackoffHours = 12
            }),
            cacheStore: cacheStore);
    }

    private static SqliteOmdbCacheStore CreateCacheStore(string root)
    {
        return new SqliteOmdbCacheStore(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/app.db" }),
            new FakeWebHostEnvironment(root),
            TimeProvider.System);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"premiere-calendar-omdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class AsyncStubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responder(request);
        }
    }

    private sealed class FakeWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = contentRootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
