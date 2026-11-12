using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class ProviderOptionalCacheIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-optional-cache-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(DateTimeOffset.UtcNow);
    private string DatabasePath => Path.Combine(_root, "test.db");

    public ProviderOptionalCacheIntegrationTests() => TestSqliteDatabase.Initialize(_root, DatabasePath);
    private IProviderCacheStateStore Store() => new SqliteProviderCacheStateStore(
        Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = DatabasePath }), new Environment(_root));
    private ProviderResponseCache Durable() => new(Store(), NullLogger<ProviderResponseCache>.Instance, _clock, ioBudget: TimeSpan.FromSeconds(30));
    private static MemoryCache Memory() => new(new MemoryCacheOptions());
    private FanartClient Fanart(HttpMessageHandler handler, IMemoryCache memory, string key = "private-key", string version = "v3") => new(
        new HttpClient(handler) { BaseAddress = new Uri($"https://webservice.fanart.tv/{version}/") }, memory,
        Microsoft.Extensions.Options.Options.Create(new FanartOptions { Enabled = true, ApiKey = key }), responseCache: Durable(), timeProvider: _clock);
    private WatchmodeClient Watchmode(HttpMessageHandler handler, IMemoryCache memory, string key = "private-key") => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.watchmode.com/v1/") }, memory,
        Microsoft.Extensions.Options.Options.Create(new WatchmodeOptions { Enabled = true, ApiKey = key, CacheHours = 12 }),
        responseCache: Durable(), timeProvider: _clock);
    private static HttpResponseMessage Art(string label) => StubHttpMessageHandler.Json(
        "{\"movieposter\":[{\"url\":\"https://art.test/" + label + ".jpg\"}]}");
    private static string? Poster(ProviderRefreshResult<FanartMovieArtwork?> value) => value.Value?.MoviePosters.FirstOrDefault()?.Url;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("{\"Response\":\"True\",\"imdbRating\":\"8.2\",\"Ratings\":[null]}")]
    [InlineData("{\"Response\":\"True\",\"Ratings\":[null]}")]
    [InlineData("{\"Response\":\"False\",\"Error\":\"Unexpected provider failure\"}")]
    public async Task OmdbRepeatedInvalidForcedRefreshPreservesGoodMemoryResponse(string invalid)
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(++calls == 1
            ? "{\"Response\":\"True\",\"imdbRating\":\"8.2\"}" : invalid));
        using var memory = Memory();
        var client = new OmdbClient(new HttpClient(handler) { BaseAddress = new Uri("https://www.omdbapi.com/") }, memory,
            Microsoft.Extensions.Options.Options.Create(new OmdbOptions { Enabled = true, ApiKey = "private-key" }));
        var good = await client.GetByImdbIdWithStatusAsync("tt42", default);
        var failed = await client.GetByImdbIdWithStatusAsync("tt42", default, true);
        Assert.False(failed.Successful);
        Assert.Same(good.Value, failed.Value);
        var cached = await client.GetByImdbIdWithStatusAsync("tt42", default);
        Assert.True(cached.Successful);
        Assert.Same(good.Value, cached.Value);
        var retried = await client.GetByImdbIdWithStatusAsync("tt42", default, true);
        Assert.False(retried.Successful);
        Assert.Equal("invalid_response", retried.Failure?.Reason);
        Assert.Same(good.Value, retried.Value);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("{\"Response\":\"False\",\"Error\":\"Incorrect IMDb ID.\"}", "invalid_imdb_id")]
    [InlineData("{", "invalid_response")]
    public async Task OmdbItemPayloadFailureDoesNotBlockAnotherTitleOrPersistProviderQuota(string invalid, string reason)
    {
        using var memory = Memory();
        var store = new SqliteOmdbCacheStore(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = DatabasePath }), new Environment(_root), _clock);
        var handler = new StubHttpMessageHandler(request => StubHttpMessageHandler.Json(QueryString.Parse(request.RequestUri!)["i"] == "ttbad"
            ? invalid : "{\"Response\":\"True\",\"imdbRating\":\"7.3\"}"));
        var client = new OmdbClient(new HttpClient(handler) { BaseAddress = new Uri("https://www.omdbapi.com/") }, memory,
            Microsoft.Extensions.Options.Options.Create(new OmdbOptions { Enabled = true, ApiKey = "private-key" }), cacheStore: store, timeProvider: _clock);
        var bad = await client.GetByImdbIdWithStatusAsync("ttbad", default);
        Assert.False(bad.Successful);
        Assert.Equal(reason, bad.Failure?.Reason);
        Assert.Null((await store.GetProviderStateAsync(default)).RateLimitedUntilUtc);
        var good = await client.GetByImdbIdWithStatusAsync("ttgood", default);
        Assert.True(good.Successful);
        Assert.Equal("7.3", good.Value?.ImdbRating);
        var deferred = await client.GetByImdbIdWithStatusAsync("ttbad", default);
        Assert.False(deferred.Successful);
        Assert.Equal("local_backoff", deferred.Failure?.Reason);
        Assert.Null(await store.GetAsync("ttbad", default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("Request limit reached!", "rate_limited")]
    [InlineData("Invalid API key!", "authentication_denied")]
    public async Task OmdbExplicitForceRetriesSoftPayloadFailureButCannotBypassGlobalDenial(string denial, string reason)
    {
        using var memory = Memory();
        var store = new SqliteOmdbCacheStore(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = DatabasePath }), new Environment(_root), _clock);
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(++calls switch
        {
            1 => "{}",
            2 => "{\"Response\":\"True\",\"imdbRating\":\"7.3\"}",
            _ => System.Text.Json.JsonSerializer.Serialize(new { Response = "False", Error = denial })
        }));
        var client = new OmdbClient(new HttpClient(handler) { BaseAddress = new Uri("https://www.omdbapi.com/") }, memory,
            Microsoft.Extensions.Options.Options.Create(new OmdbOptions { Enabled = true, ApiKey = "private-key" }), cacheStore: store, timeProvider: _clock);
        using var scope = ProviderRefreshScope.Use(ProviderRefreshScope.Create());
        Assert.False((await client.GetByImdbIdWithStatusAsync("ttsoft", default)).Successful);
        Assert.Equal("local_backoff", (await client.GetByImdbIdWithStatusAsync("ttsoft", default)).Failure?.Reason);
        var recovered = await client.GetByImdbIdWithStatusAsync("ttsoft", default, true);
        Assert.True(recovered.Successful);
        Assert.Equal("7.3", recovered.Value?.ImdbRating);
        Assert.Equal(2, calls);
        var denied = await client.GetByImdbIdWithStatusAsync("ttquota", default, true);
        Assert.False(denied.Successful);
        Assert.Equal(reason, denied.Failure?.Reason);
        Assert.NotNull((await store.GetProviderStateAsync(default)).RateLimitedUntilUtc);
        var deferred = await client.GetByImdbIdWithStatusAsync("ttdifferent", default, true);
        Assert.False(deferred.Successful);
        Assert.Equal("local_backoff", deferred.Failure?.Reason);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("{\"error\":\"Item lookup failed\"}")]
    public async Task FanartItemPayloadFailureDoesNotBlockAnotherTitle(string invalid)
    {
        using var memory = Memory();
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/42", StringComparison.Ordinal)
            ? StubHttpMessageHandler.Json(invalid) : Art("other-title"));
        var client = Fanart(handler, memory);
        var bad = await client.GetMovieArtworkWithStatusAsync(42, default);
        Assert.False(bad.Successful);
        var good = await client.GetMovieArtworkWithStatusAsync(43, default);
        Assert.True(good.Successful);
        Assert.Equal("https://art.test/other-title.jpg", Poster(good));
        var deferred = await client.GetMovieArtworkWithStatusAsync(42, default, true);
        Assert.False(deferred.Successful);
        Assert.Equal("local_backoff", deferred.Failure?.Reason);
        Assert.Single(await Store().GetByProviderAsync("responsecache-fanart", 10, default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OmdbPreviouslyCachedNullRatingEntriesAreRefetched(bool persisted)
    {
        using var memory = Memory();
        var store = new SqliteOmdbCacheStore(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = DatabasePath }), new Environment(_root), _clock);
        var invalid = new OmdbItem { Response = "True", Ratings = [null!] };
        if (persisted) await store.SetAsync("tt42", invalid, _clock.UtcNow, default);
        else memory.Set("omdb:imdb:tt42", invalid);
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{\"Response\":\"True\",\"imdbRating\":\"7.3\"}"));
        var client = new OmdbClient(new HttpClient(handler) { BaseAddress = new Uri("https://www.omdbapi.com/") }, memory,
            Microsoft.Extensions.Options.Options.Create(new OmdbOptions { Enabled = true, ApiKey = "private-key" }), cacheStore: store, timeProvider: _clock);
        var result = await client.GetByImdbIdWithStatusAsync("tt42", default);
        Assert.True(result.Successful);
        Assert.Equal("7.3", result.Value?.ImdbRating);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FanartSuccessfulPayloadAndLegacyAbsenceSurviveRestartWithoutExtendingExpiry(bool absent)
    {
        var handler = new StubHttpMessageHandler(_ => absent ? StubHttpMessageHandler.Json("{}") : Art("initial"));
        using var memory = Memory();
        var initial = await Fanart(handler, memory).GetMovieArtworkWithStatusAsync(42, default);
        Assert.True(initial.Successful);
        Assert.Equal(absent, initial.Value is null);
        var savedAt = _clock.UtcNow;
        _clock.UtcNow = savedAt.AddDays(6);
        using var restartedMemory = Memory();
        var restarted = Fanart(handler, restartedMemory);
        Assert.Equal(Poster(initial), Poster(await restarted.GetMovieArtworkWithStatusAsync(42, default)));
        Assert.Single(handler.Requests);
        Assert.Equal(savedAt, Assert.Single(await Store().GetByProviderAsync("responsecache-fanart", 10, default)).LastCheckedUtc);
        _clock.UtcNow = savedAt.AddDays(7);
        await restarted.GetMovieArtworkWithStatusAsync(42, default);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task LegacyEmptyObjectIsNotAbsenceOnNewerFanartEndpoint()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{}"));
        using var memory = Memory();
        var client = Fanart(handler, memory, version: "v3.2");
        Assert.False((await client.GetMovieArtworkWithStatusAsync(42, default)).Successful);
        var deferred = await client.GetMovieArtworkWithStatusAsync(42, default, true);
        Assert.Equal("local_backoff", deferred.Failure?.Reason);
        Assert.Empty(await Store().GetByProviderAsync("responsecache-fanart", 10, default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FanartForcedAbsenceSupersedesDurableGoodResponseAndFailuresRetainGoodData()
    {
        var mode = "good";
        var handler = new StubHttpMessageHandler(_ => mode switch
        {
            "good" => Art("initial"),
            "failed" => StubHttpMessageHandler.Json("{\"unexpected\":true}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var memory = Memory();
        var client = Fanart(handler, memory);
        var good = await client.GetMovieArtworkWithStatusAsync(42, default);
        mode = "failed";
        var failed = await client.GetMovieArtworkWithStatusAsync(42, default, true);
        Assert.False(failed.Successful);
        Assert.Equal(Poster(good), Poster(failed));
        using var restartMemory = Memory();
        Assert.Equal(Poster(good), Poster(await Fanart(handler, restartMemory).GetMovieArtworkWithStatusAsync(42, default)));
        // Failure backoff is local; a restarted client can perform the explicit absence refresh.
        mode = "absent";
        Assert.Null((await Fanart(handler, restartMemory).GetMovieArtworkWithStatusAsync(42, default, true)).Value);
        using var finalMemory = Memory();
        var final = await Fanart(handler, finalMemory).GetMovieArtworkWithStatusAsync(42, default);
        Assert.True(final.Successful);
        Assert.Null(final.Value);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FanartForcedPublicationWinsNormalRaceInMemoryAndAfterRestart(bool forceFirst)
    {
        var firstStarted = Signal();
        var secondStarted = Signal();
        var releaseFirst = Signal();
        var releaseSecond = Signal();
        var calls = 0;
        var handler = new AsyncHandler(async (_, token) =>
        {
            var number = Interlocked.Increment(ref calls);
            (number == 1 ? firstStarted : secondStarted).TrySetResult();
            await (number == 1 ? releaseFirst : releaseSecond).Task.WaitAsync(token);
            return Art(number == (forceFirst ? 1 : 2) ? "forced" : "old");
        });
        using var memory = Memory();
        var client = Fanart(handler, memory);
        var first = client.GetMovieArtworkWithStatusAsync(42, default, forceFirst);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.GetMovieArtworkWithStatusAsync(42, default, !forceFirst);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (forceFirst) { releaseFirst.TrySetResult(); await first; releaseSecond.TrySetResult(); }
        else { releaseSecond.TrySetResult(); await second; releaseFirst.TrySetResult(); }
        await Task.WhenAll(first, second);
        Assert.Equal("https://art.test/forced.jpg", Poster(await client.GetMovieArtworkWithStatusAsync(42, default)));
        using var restartedMemory = Memory();
        Assert.Equal("https://art.test/forced.jpg", Poster(await Fanart(handler, restartedMemory).GetMovieArtworkWithStatusAsync(42, default)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FanartCoalescesAcrossClientsAndCanceledWaiterDoesNotCancelSurvivor()
    {
        var started = Signal();
        var release = Signal();
        var calls = 0;
        var handler = new AsyncHandler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Art("shared");
        });
        using var memory = Memory();
        using var cancellation = new CancellationTokenSource();
        var canceled = Fanart(handler, memory).GetMovieArtworkWithStatusAsync(42, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var survivor = Fanart(handler, memory).GetMovieArtworkWithStatusAsync(42, default);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        release.TrySetResult();
        Assert.True((await survivor.WaitAsync(TimeSpan.FromSeconds(5))).Successful);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CredentialPartitionPreventsCrossAccountMemoryAndDurableReuse()
    {
        var handler = new StubHttpMessageHandler(_ => Art("account"));
        using var memory = Memory();
        await Fanart(handler, memory, "first-secret").GetMovieArtworkWithStatusAsync(42, default);
        await Fanart(handler, memory, "second-secret").GetMovieArtworkWithStatusAsync(42, default);
        Assert.Equal(2, handler.Requests.Count);
        var rows = await Store().GetByProviderAsync("responsecache-fanart", 10, default);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.DoesNotContain("secret", row.Key);
            Assert.DoesNotContain("secret", row.MetadataJson!);
        });
    }

    [Fact]
    public async Task WatchmodeIdentityOutlivesAvailabilityAcrossRestartAndForceAbsenceInvalidatesMapping()
    {
        var searches = 0;
        var sources = 0;
        var absence = false;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/search/", StringComparison.Ordinal))
            {
                searches++;
                return StubHttpMessageHandler.Json(absence ? "{\"title_results\":[]}" : "{\"title_results\":[{\"id\":123}]}");
            }
            sources++;
            return StubHttpMessageHandler.Json("[{\"source_id\":1,\"name\":\"Fixture\",\"type\":\"sub\"}]");
        });
        using var memory = Memory();
        await Watchmode(handler, memory).GetTitleSourcesWithStatusAsync(PremiereMediaType.Movie, 42, null, ["BE"], default);
        _clock.UtcNow = _clock.UtcNow.AddHours(13);
        using var restartedMemory = Memory();
        var restarted = Watchmode(handler, restartedMemory);
        Assert.Single((await restarted.GetTitleSourcesWithStatusAsync(PremiereMediaType.Movie, 42, null, ["BE"], default)).Value);
        Assert.Equal(1, searches);
        Assert.Equal(2, sources);
        absence = true;
        Assert.Empty((await restarted.GetTitleSourcesWithStatusAsync(PremiereMediaType.Movie, 42, null, ["BE"], default, true)).Value);
        using var finalMemory = Memory();
        Assert.Empty((await Watchmode(handler, finalMemory).GetTitleSourcesWithStatusAsync(PremiereMediaType.Movie, 42, null, ["BE"], default)).Value);
        Assert.Equal(2, searches);
        Assert.Equal(2, sources);
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("search")]
    [InlineData("images")]
    public async Task TvmazeSelectedResponsesSurviveClientAndStoreRestart(string kind)
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(kind switch
        {
            "lookup" => "{\"id\":82,\"name\":\"Fixture\"}",
            "search" => "[{\"show\":{\"id\":82,\"name\":\"Fixture\"}}]",
            _ => "[{\"type\":\"poster\",\"resolutions\":{\"original\":{\"url\":\"https://art.test/poster.jpg\"}}}]"
        }));
        TvmazeClient Client(IMemoryCache cache) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.tvmaze.com/") }, cache,
            Microsoft.Extensions.Options.Options.Create(new TvmazeOptions { Enabled = true }), responseCache: Durable(), timeProvider: _clock);
        async Task Lookup(TvmazeClient client)
        {
            if (kind == "lookup") Assert.Equal(82, (await client.LookupShowWithStatusAsync(123, null, default)).Value?.Id);
            else if (kind == "search") Assert.Equal(82, (await client.SearchShowByNameWithStatusAsync("Fixture", default)).Value?.Id);
            else Assert.Single((await client.GetShowImagesWithStatusAsync(82, default)).Value);
        }
        using var memory = Memory();
        await Lookup(Client(memory));
        using var restartedMemory = Memory();
        await Lookup(Client(restartedMemory));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WikimediaSequentialDifferentClaimLookupsReuseEntityResponse(bool imageFirst)
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.Host == "www.wikidata.org"
            ? StubHttpMessageHandler.Json("{\"entities\":{\"Q1\":{\"claims\":{\"P18\":[{\"mainsnak\":{\"datavalue\":{\"value\":\"Fixture.jpg\"}}}],\"P1258\":[{\"mainsnak\":{\"datavalue\":{\"value\":\"m/fixture\"}}}]}}}}")
            : Commons());
        using var memory = Memory();
        var client = Wikimedia(handler, memory);
        if (imageFirst)
        {
            Assert.NotNull(await client.GetReusableImageUrlAsync("Q1", default));
            Assert.Equal("m/fixture", await client.GetRottenTomatoesIdAsync("Q1", default));
        }
        else
        {
            Assert.Equal("m/fixture", await client.GetRottenTomatoesIdAsync("Q1", default));
            Assert.NotNull(await client.GetReusableImageUrlAsync("Q1", default));
        }
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Uri.Host == "www.wikidata.org");
    }

    [Fact]
    public async Task WikimediaCommonsFetchIsSharedForTwoEntitiesReferencingTheSameFile()
    {
        var commonsStarted = Signal();
        var release = Signal();
        var commonsCalls = 0;
        var handler = new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "www.wikidata.org")
                return StubHttpMessageHandler.Json("{\"entities\":{\"Q1\":{\"claims\":{\"P18\":[{\"mainsnak\":{\"datavalue\":{\"value\":\"Shared.jpg\"}}}]}},\"Q2\":{\"claims\":{\"P18\":[{\"mainsnak\":{\"datavalue\":{\"value\":\"Shared.jpg\"}}}]}}}}");
            Interlocked.Increment(ref commonsCalls);
            commonsStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return Commons();
        });
        using var memory = Memory();
        var client = Wikimedia(handler, memory);
        var first = client.GetReusableImageUrlAsync("Q1", default);
        await commonsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.GetReusableImageUrlAsync("Q2", default);
        // Wait for the second entity to reach its shared Commons operation.
        await client.GetRottenTomatoesIdAsync("Q2", default);
        release.TrySetResult();
        Assert.All(await Task.WhenAll(first, second), value => Assert.Equal("https://art.test/commons.jpg", value));
        Assert.Equal(1, commonsCalls);
    }

    private static WikimediaClient Wikimedia(HttpMessageHandler handler, IMemoryCache memory) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://www.wikidata.org/") }, memory,
        Microsoft.Extensions.Options.Options.Create(new WikimediaOptions { Enabled = true }));
    private static HttpResponseMessage Commons() => StubHttpMessageHandler.Json(
        "{\"query\":{\"pages\":{\"1\":{\"imageinfo\":[{\"url\":\"https://art.test/commons.jpg\",\"extmetadata\":{\"LicenseShortName\":{\"value\":\"CC BY-SA 4.0\"}}}]}}}}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar.IntegrationTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
