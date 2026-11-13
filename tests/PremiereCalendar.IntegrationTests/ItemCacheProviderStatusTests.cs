using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class ItemCacheProviderStatusTests : IDisposable
{
    private PostgresTestDatabase? _database;

    [Fact]
    public async Task TvmazeLookupWithStatus_TreatsNotFoundAndValidShowAsSuccessful()
    {
        var notFoundHandler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{}", HttpStatusCode.NotFound));
        var notFound = await Tvmaze(notFoundHandler).LookupShowWithStatusAsync(42, null, CancellationToken.None);

        Assert.True(notFound.Successful);
        Assert.Null(notFound.Value);

        var validHandler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"id":43,"name":"Fixture Show"}"""));
        var valid = await Tvmaze(validHandler).LookupShowWithStatusAsync(43, null, CancellationToken.None);

        Assert.True(valid.Successful);
        Assert.Equal(43, valid.Value?.Id);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "{}")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, "null")]
    [InlineData(HttpStatusCode.OK, "{")]
    public async Task TvmazeLookupWithStatus_TreatsRateLimitNullAndInvalidJsonAsFailure(HttpStatusCode status, string body)
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(body, status));

        var result = await Tvmaze(handler).LookupShowWithStatusAsync(44, null, CancellationToken.None);

        Assert.False(result.Successful);
        Assert.Null(result.Value);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TvmazeSearchWithStatus_TreatsValidEmptyListAsSuccessfulAbsence()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("[]"));

        var result = await Tvmaze(handler).SearchShowByNameWithStatusAsync("No Matching Show", CancellationToken.None);

        Assert.True(result.Successful);
        Assert.Null(result.Value);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "[]")]
    [InlineData(HttpStatusCode.OK, "null")]
    [InlineData(HttpStatusCode.OK, "not-json")]
    public async Task TvmazeSearchWithStatus_TreatsRateLimitNullAndInvalidJsonAsFailure(HttpStatusCode status, string body)
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(body, status));

        var result = await Tvmaze(handler).SearchShowByNameWithStatusAsync("No Matching Show", CancellationToken.None);

        Assert.False(result.Successful);
        Assert.Null(result.Value);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("title resolution")]
    [InlineData("source lookup")]
    public async Task WatchmodeSourcesWithStatus_TreatsFailedResolutionOrSourceRequestAsFailure(string failAt)
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/search/", StringComparison.Ordinal))
            {
                return failAt == "title resolution"
                    ? StubHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable)
                    : StubHttpMessageHandler.Json(
                        """{"title_results":[{"id":3173903,"name":"Fixture Show","type":"tv_series","tmdb_id":1396,"tmdb_type":"tv"}]}""");
            }

            Assert.Equal("source lookup", failAt);
            return StubHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
        });

        var result = await Watchmode(handler).GetTitleSourcesWithStatusAsync(
            PremiereMediaType.Series, 1396, null, ["BE"], CancellationToken.None);

        Assert.False(result.Successful);
        Assert.Empty(result.Value);
        Assert.Equal(failAt == "title resolution" ? 1 : 2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("title resolution")]
    [InlineData("source lookup")]
    public async Task WatchmodeSourcesWithStatus_TreatsValidEmptyResultsAsSuccessful(string emptyAt)
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/search/", StringComparison.Ordinal))
            {
                return emptyAt == "title resolution"
                    ? StubHttpMessageHandler.Json("""{"title_results":[]}""")
                    : StubHttpMessageHandler.Json(
                        """{"title_results":[{"id":3173903,"name":"Fixture Show","type":"tv_series","tmdb_id":1396,"tmdb_type":"tv"}]}""");
            }

            Assert.Equal("source lookup", emptyAt);
            return StubHttpMessageHandler.Json("[]");
        });

        var result = await Watchmode(handler).GetTitleSourcesWithStatusAsync(
            PremiereMediaType.Series, 1396, null, ["BE"], CancellationToken.None);

        Assert.True(result.Successful);
        Assert.Empty(result.Value);
        Assert.Equal(emptyAt == "title resolution" ? 1 : 2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"title_results":null}""")]
    public async Task WatchmodeSourcesWithStatus_TreatsMissingOrNullTitleResultsAsFailure(string body)
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(body));

        var result = await Watchmode(handler).GetTitleSourcesWithStatusAsync(
            PremiereMediaType.Series, 1396, null, ["BE"], CancellationToken.None);

        Assert.False(result.Successful);
        Assert.Empty(result.Value);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ProviderResponseCache_PreservesExplicitExpiryAcrossRestartWithoutExtendingItOnReads()
    {
        var now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        var clock = new TestTimeProvider(now);
        var lifetime = TimeSpan.FromHours(8);
        const string provider = "tmdb";
        const string requestKey = "movie/detail/42";
        const string value = "cached response fixture";

        var firstStore = CreateStore();
        var firstCache = Cache(firstStore, clock);
        await firstCache.SaveAsync(provider, requestKey, value, CancellationToken.None, lifetime: lifetime);

        var persisted = Assert.Single(await firstStore.GetByProviderAsync("responsecache-tmdb", 10, CancellationToken.None));
        Assert.Equal(now, persisted.LastCheckedUtc);

        var initialRead = await Cache(CreateStore(), clock).GetAsync<string>(
            provider, requestKey, TimeSpan.FromDays(30), CancellationToken.None);
        Assert.NotNull(initialRead);
        Assert.Equal(value, initialRead.Value);
        Assert.Equal(now.Add(lifetime), initialRead.ExpiresAtUtc);

        clock.UtcNow = now.AddHours(7);
        var restartedCache = Cache(CreateStore(), clock);
        var restartedRead = await restartedCache.GetAsync<string>(provider, requestKey, TimeSpan.FromDays(30), CancellationToken.None);

        Assert.NotNull(restartedRead);
        Assert.Equal(now.Add(lifetime), restartedRead.ExpiresAtUtc);
        Assert.Equal(now, (await CreateStore().GetByProviderAsync("responsecache-tmdb", 10, CancellationToken.None)).Single().LastCheckedUtc);

        clock.UtcNow = now.AddHours(8);
        var afterExpiry = await Cache(CreateStore(), clock).GetAsync<string>(
            provider, requestKey, TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Null(afterExpiry);
    }

    [Fact]
    public async Task WatchmodePartialRegionsRetainAvailableDataWithoutReportingCompleteRefresh()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/search/", StringComparison.Ordinal))
                return StubHttpMessageHandler.Json("""{"title_results":[{"id":3173903}]}""");
            return QueryString.Parse(request.RequestUri)["regions"] == "BE"
                ? StubHttpMessageHandler.Json("""[{"source_id":203,"name":"Netflix","type":"sub","region":"BE"}]""")
                : StubHttpMessageHandler.Json("{}", HttpStatusCode.BadRequest);
        });
        var client = Watchmode(handler);
        var result = await client.GetTitleSourcesWithStatusAsync(PremiereMediaType.Series, 1396, null, ["BE", "NL"], default);
        Assert.False(result.Successful);
        Assert.Equal("Netflix", Assert.Single(result.Value).Name);
        Assert.Equal(4, handler.Requests.Count);
        var retried = await client.GetTitleSourcesWithStatusAsync(PremiereMediaType.Series, 1396, null, ["BE", "NL"], default);
        Assert.False(retried.Successful);
        Assert.Equal(7, handler.Requests.Count); // Positive identity reused; incomplete sources were not cached as complete.
    }

    private static TvmazeClient Tvmaze(StubHttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.tvmaze.com/") },
        new MemoryCache(new MemoryCacheOptions()),
        Microsoft.Extensions.Options.Options.Create(new TvmazeOptions { Enabled = true }));

    private static WatchmodeClient Watchmode(StubHttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.watchmode.com/v1/") },
        new MemoryCache(new MemoryCacheOptions()),
        Microsoft.Extensions.Options.Options.Create(new WatchmodeOptions
        {
            Enabled = true,
            ApiKey = "test-watchmode-key",
            Regions = ["BE"],
            EnableAvailabilityEnrichment = true
        }));

    // Only the response-cache restart test needs a database, so it is created on first use.
    private IProviderCacheStateStore CreateStore() => new ProviderCacheStateStore((_database ??= PostgresTestDatabase.Create()).Options);

    private static ProviderResponseCache Cache(IProviderCacheStateStore store, TimeProvider clock) =>
        new(store, NullLogger<ProviderResponseCache>.Instance, clock);

    public void Dispose() => _database?.Dispose();

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
