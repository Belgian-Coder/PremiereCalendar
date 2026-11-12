using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task GetByImdbIdAsync_RefreshesStaleEmptyPersistentCacheSoonerThanSuccessfulMetadata()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateCacheStore(root);
            await store.SetAsync(
                "tt-empty",
                new PremiereCalendar.Models.OmdbItem
                {
                    Response = "False",
                    Error = "Error getting data."
                },
                DateTimeOffset.UtcNow.AddHours(-2),
                CancellationToken.None);
            var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("omdb/by-imdb-id-success.json")));
            var client = CreateOmdbClient(handler, enabled: true, cacheStore: store);

            var refreshed = await client.GetByImdbIdAsync("tt-empty", CancellationToken.None);

            Assert.NotNull(refreshed);
            Assert.Equal("8.2", refreshed.ImdbRating);
            Assert.Single(handler.Requests);
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

    [Theory]
    [InlineData("Invalid API key!", "authentication_denied")]
    [InlineData("Request limit reached!", "rate_limited")]
    public async Task PayloadDenialIsSharedAcrossDistinctTitlesAndNeverVerifiedAsAbsence(string error, string reason)
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            System.Text.Json.JsonSerializer.Serialize(new { Response = "False", Error = error })));
        var client = CreateOmdbClient(handler, enabled: true);
        var first = await client.GetByImdbIdWithStatusAsync("tt1", CancellationToken.None);
        var second = await client.GetByImdbIdWithStatusAsync("tt2", CancellationToken.None, forceRefresh: true);
        Assert.False(first.Successful);
        Assert.Null(first.Value);
        Assert.Equal(reason, first.Failure?.Reason);
        Assert.False(second.Successful);
        Assert.Equal("local_backoff", second.Failure?.Reason);
        Assert.Equal(first.Failure?.RetryAtUtc, second.Failure?.RetryAtUtc);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UnknownFalsePayloadBacksOffWithoutCachingVerifiedAbsence()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(++calls == 1
            ? "{\"Response\":\"False\",\"Error\":\"Error getting data.\"}"
            : Fixture.Read("omdb/by-imdb-id-success.json")));
        var client = CreateOmdbClient(handler, enabled: true);
        var failed = await client.GetByImdbIdWithStatusAsync("tt1", CancellationToken.None);
        var deferred = await client.GetByImdbIdWithStatusAsync("tt1", CancellationToken.None);
        Assert.False(failed.Successful);
        Assert.Equal("provider_data_unavailable", failed.Failure?.Reason);
        Assert.False(deferred.Successful);
        Assert.Equal("local_backoff", deferred.Failure?.Reason);
        Assert.Null(deferred.Value);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("Error getting data.", "provider_data_unavailable", 1)]
    [InlineData("Incorrect IMDb ID.", "invalid_imdb_id", 24)]
    public async Task KnownProviderFailureDefersSameIdUntilDeadlineAndPreservesStaleMetadata(
        string error, string reason, int retryHours)
    {
        var root = CreateRoot();
        try
        {
            var clock = new OmdbClock(DateTimeOffset.UtcNow);
            var store = CreateCacheStore(root);
            var originalClock = clock.GetUtcNow().AddDays(-91);
            await store.SetAsync("tt49639968", new() { Response = "True", ImdbRating = "7.4" }, originalClock, default);
            var calls = 0;
            var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(++calls == 1
                ? System.Text.Json.JsonSerializer.Serialize(new { Response = "False", Error = error })
                : """{"Response":"True","imdbRating":"8.1"}"""));
            var client = CreateOmdbClient(handler, true, store, timeProvider: clock);

            var failure = await client.GetByImdbIdWithStatusAsync("tt49639968", default);
            Assert.False(failure.Successful);
            Assert.Equal(reason, failure.Failure?.Reason);
            Assert.Equal(clock.GetUtcNow().AddHours(retryHours), failure.Failure?.RetryAtUtc);
            Assert.Equal("7.4", failure.Value?.ImdbRating);
            Assert.Equal(originalClock, (await store.GetAsync("tt49639968", default))!.CachedAtUtc);
            Assert.Null((await store.GetProviderStateAsync(default)).RateLimitedUntilUtc);

            clock.Advance(TimeSpan.FromHours(retryHours) - TimeSpan.FromSeconds(1));
            var deferred = await client.GetByImdbIdWithStatusAsync("tt49639968", default);
            Assert.False(deferred.Successful);
            Assert.Equal("local_backoff", deferred.Failure?.Reason);
            Assert.Equal(failure.Failure?.RetryAtUtc, deferred.Failure?.RetryAtUtc);
            Assert.Equal("7.4", deferred.Value?.ImdbRating);
            Assert.Single(handler.Requests);

            clock.Advance(TimeSpan.FromSeconds(1));
            var recovered = await client.GetByImdbIdWithStatusAsync("tt49639968", default);
            Assert.True(recovered.Successful);
            Assert.Equal("8.1", recovered.Value?.ImdbRating);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(clock.GetUtcNow(), (await store.GetAsync("tt49639968", default))!.CachedAtUtc);
        }
        finally { TryDelete(root); }
    }

    [Theory]
    [InlineData("Error getting data.")]
    [InlineData("Incorrect IMDb ID.")]
    public async Task KnownFailureIsIsolatedFromAnotherImdbIdAndExplicitForceCanRecover(string error)
    {
        var recover = false;
        var handler = new StubHttpMessageHandler(request => StubHttpMessageHandler.Json(
            QueryString.Parse(request.RequestUri!)["i"] == "tt49639968" && !recover
                ? System.Text.Json.JsonSerializer.Serialize(new { Response = "False", Error = error })
                : """{"Response":"True","imdbRating":"7.9"}"""));
        var client = CreateOmdbClient(handler, true);
        Assert.False((await client.GetByImdbIdWithStatusAsync("tt49639968", default)).Successful);

        var other = await client.GetByImdbIdWithStatusAsync("tt31091563", default);
        Assert.True(other.Successful);
        Assert.Equal("7.9", other.Value?.ImdbRating);
        Assert.False((await client.GetByImdbIdWithStatusAsync("tt49639968", default)).Successful);
        Assert.Equal(2, handler.Requests.Count);

        recover = true;
        var forced = await client.GetByImdbIdWithStatusAsync("tt49639968", default, forceRefresh: true);
        Assert.True(forced.Successful);
        Assert.Equal("7.9", forced.Value?.ImdbRating);
        Assert.True((await client.GetByImdbIdWithStatusAsync("tt49639968", default)).Successful);
        Assert.True((await client.GetByImdbIdWithStatusAsync("tt31091563", default)).Successful);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("Error getting data.", 1)]
    [InlineData("Incorrect IMDb ID.", 24)]
    public async Task KnownFailureDeadlineSurvivesRestartWithoutReplacingGoodItem(string error, int hours)
    {
        var root = CreateRoot();
        try
        {
            var clock = new OmdbClock(DateTimeOffset.UtcNow);
            var store = CreateCacheStore(root);
            var originalClock = clock.GetUtcNow().AddDays(-91);
            await store.SetAsync("tt49639968", new() { Response = "True", ImdbRating = "7.4" }, originalClock, default);
            var handler = new StubHttpMessageHandler(request => StubHttpMessageHandler.Json(
                QueryString.Parse(request.RequestUri!)["i"] == "tt49639968"
                    ? System.Text.Json.JsonSerializer.Serialize(new { Response = "False", Error = error })
                    : """{"Response":"True","imdbRating":"8.1"}"""));
            var first = CreateOmdbClient(handler, true, store, timeProvider: clock, responseCache: CreateResponseCache(root, clock));
            var failure = await first.GetByImdbIdWithStatusAsync("tt49639968", default);

            var restarted = CreateOmdbClient(handler, true, CreateCacheStore(root), timeProvider: clock, responseCache: CreateResponseCache(root, clock));
            var deferred = await restarted.GetByImdbIdWithStatusAsync("tt49639968", default);
            Assert.False(deferred.Successful);
            Assert.Equal("local_backoff", deferred.Failure?.Reason);
            Assert.True(deferred.Failure?.IsLocal);
            Assert.Equal(clock.GetUtcNow().AddHours(hours), deferred.Failure?.RetryAtUtc);
            Assert.Equal(failure.Failure?.RetryAtUtc, deferred.Failure?.RetryAtUtc);
            Assert.Equal("7.4", deferred.Value?.ImdbRating);
            Assert.Equal(originalClock, (await store.GetAsync("tt49639968", default))!.CachedAtUtc);
            Assert.Single(handler.Requests);
            Assert.True((await restarted.GetByImdbIdWithStatusAsync("tt31091563", default)).Successful);
            Assert.Equal(2, handler.Requests.Count);

            clock.Advance(TimeSpan.FromHours(hours));
            Assert.False((await restarted.GetByImdbIdWithStatusAsync("tt49639968", default)).Successful);
            Assert.Equal(3, handler.Requests.Count);
        }
        finally { TryDelete(root); }
    }

    [Theory]
    [InlineData("Error getting data.")]
    [InlineData("Incorrect IMDb ID.")]
    public async Task ForcedSuccessClearsPersistedFailureForRestartedClient(string error)
    {
        var root = CreateRoot();
        try
        {
            CreateCacheStore(root);
            var clock = new OmdbClock(DateTimeOffset.UtcNow);
            var calls = 0;
            var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(++calls == 1
                ? System.Text.Json.JsonSerializer.Serialize(new { Response = "False", Error = error })
                : """{"Response":"True","imdbRating":"8.1"}"""));
            var client = CreateOmdbClient(handler, true, timeProvider: clock, responseCache: CreateResponseCache(root, clock));
            Assert.False((await client.GetByImdbIdWithStatusAsync("tt49639968", default)).Successful);
            Assert.True((await client.GetByImdbIdWithStatusAsync("tt49639968", default, forceRefresh: true)).Successful);

            var restarted = CreateOmdbClient(handler, true, timeProvider: clock, responseCache: CreateResponseCache(root, clock));
            Assert.True((await restarted.GetByImdbIdWithStatusAsync("tt49639968", default)).Successful);
            Assert.Equal(3, handler.Requests.Count);
        }
        finally { TryDelete(root); }
    }

    [Theory]
    [InlineData("Error getting data.")]
    [InlineData("Incorrect IMDb ID.")]
    public async Task LateNormalFailureCannotRestoreCooldownAfterForcedSuccess(string error)
    {
        var root = CreateRoot();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            CreateCacheStore(root);
            var clock = new OmdbClock(DateTimeOffset.UtcNow);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var handler = new AsyncStubHttpMessageHandler(async _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.SetResult();
                    await release.Task;
                    return StubHttpMessageHandler.Json(System.Text.Json.JsonSerializer.Serialize(new { Response = "False", Error = error }));
                }
                return StubHttpMessageHandler.Json("""{"Response":"True","imdbRating":"8.1"}""");
            });
            var client = CreateOmdbClient(handler, true, timeProvider: clock, responseCache: CreateResponseCache(root, clock));
            var old = client.GetByImdbIdWithStatusAsync("tt49639968", default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((await client.GetByImdbIdWithStatusAsync("tt49639968", default, forceRefresh: true)).Successful);
            release.SetResult();
            Assert.False((await old).Successful);

            var restarted = CreateOmdbClient(handler, true, timeProvider: clock, responseCache: CreateResponseCache(root, clock));
            var result = await restarted.GetByImdbIdWithStatusAsync("tt49639968", default);
            Assert.True(result.Successful);
            Assert.Equal("8.1", result.Value?.ImdbRating);
            Assert.Equal(3, calls);
        }
        finally { release.TrySetResult(); TryDelete(root); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistentFailureDoesNotCrossCredentialsOrEndpoints(bool changeCredential)
    {
        var root = CreateRoot();
        try
        {
            CreateCacheStore(root);
            var clock = new OmdbClock(DateTimeOffset.UtcNow);
            var bad = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"Response":"False","Error":"Incorrect IMDb ID."}"""));
            var first = CreateOmdbClient(bad, true, timeProvider: clock, responseCache: CreateResponseCache(root, clock));
            Assert.False((await first.GetByImdbIdWithStatusAsync("tt49639968", default)).Successful);

            var good = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"Response":"True","imdbRating":"8.1"}"""));
            var restarted = new OmdbClient(new HttpClient(good)
                { BaseAddress = new Uri(changeCredential ? "https://www.omdbapi.com/" : "https://replacement.example/omdb/") },
                new MemoryCache(new MemoryCacheOptions()),
                Microsoft.Extensions.Options.Options.Create(new OmdbOptions
                    { Enabled = true, ApiKey = changeCredential ? "replacement-key" : "test-key" }),
                timeProvider: clock, responseCache: CreateResponseCache(root, clock));
            Assert.True((await restarted.GetByImdbIdWithStatusAsync("tt49639968", default)).Successful);
            Assert.Single(good.Requests);

            var stateStore = new SqliteProviderCacheStateStore(
                Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/app.db" }), new FakeWebHostEnvironment(root));
            var rows = await stateStore.GetByProviderAsync("responsecache-omdb", 10, default);
            Assert.Equal(2, rows.Count);
            foreach (var row in rows)
            {
                var saved = row.Key + row.MetadataJson;
                Assert.DoesNotContain("test-key", saved);
                Assert.DoesNotContain("replacement-key", saved);
                Assert.DoesNotContain("tt49639968", saved);
                Assert.DoesNotContain("https://", saved);
            }
        }
        finally { TryDelete(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedEmptySuccessClearsMemoryFailureBeyondItsSixHourLifetime(bool absent)
    {
        var clock = new OmdbClock(DateTimeOffset.UtcNow);
        using var memory = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(++calls switch
        {
            1 => """{"Response":"False","Error":"Incorrect IMDb ID."}""",
            2 => absent ? """{"Response":"False","Error":"Movie not found!"}""" : """{"Response":"True"}""",
            _ => """{"Response":"True","imdbRating":"8.1"}"""
        }));
        var client = new OmdbClient(new HttpClient(handler) { BaseAddress = new Uri("https://www.omdbapi.com/") }, memory,
            Microsoft.Extensions.Options.Options.Create(new OmdbOptions { Enabled = true, ApiKey = "test-key", EmptyResponseCacheHours = 6 }),
            timeProvider: clock);
        Assert.False((await client.GetByImdbIdWithStatusAsync("tt49639968", default)).Successful);
        Assert.True((await client.GetByImdbIdWithStatusAsync("tt49639968", default, forceRefresh: true)).Successful);
        clock.Advance(TimeSpan.FromHours(6).Add(TimeSpan.FromSeconds(1)));
        var refreshed = await client.GetByImdbIdWithStatusAsync("tt49639968", default);
        Assert.True(refreshed.Successful);
        Assert.Equal("8.1", refreshed.Value?.ImdbRating);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedOutcomeClearCannotReviveOlderFailureAfterRestartAndEmptyExpiry(bool seedGood, bool absent)
    {
        var root = CreateRoot();
        try
        {
            var clock = new OmdbClock(DateTimeOffset.UtcNow);
            var tiedClock = clock.GetUtcNow();
            var goodStore = CreateCacheStore(root);
            if (seedGood)
                await goodStore.SetAsync("tt49639968", new() { Response = "True", ImdbRating = "7.4" }, tiedClock.AddDays(-91), default);
            var baseline = await goodStore.GetAsync("tt49639968", default);
            var outcomeStore = new FailingOmdbClearStore(CreateOutcomeStore(root));
            var calls = 0;
            var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(++calls switch
            {
                1 => """{"Response":"False","Error":"Incorrect IMDb ID."}""",
                2 => absent ? """{"Response":"False","Error":"Movie not found!"}""" : """{"Response":"True"}""",
                _ => """{"Response":"True","imdbRating":"8.1"}"""
            }));
            var client = CreateOmdbClient(handler, true, goodStore, timeProvider: clock,
                responseCache: CreateResponseCache(root, clock, outcomeStore), emptyResponseCacheHours: 6);
            var failed = await client.GetByImdbIdWithStatusAsync("tt49639968", default);
            Assert.False(failed.Successful);
            Assert.Equal(tiedClock.AddHours(24), failed.Failure?.RetryAtUtc);
            Assert.True((await client.GetByImdbIdWithStatusAsync("tt49639968", default, forceRefresh: true)).Successful);
            Assert.Equal(1, outcomeStore.FailedClears);
            var newerGood = await goodStore.GetAsync("tt49639968", default);
            Assert.Equal(tiedClock, newerGood!.CachedAtUtc);
            Assert.NotNull(newerGood.SuccessObservationId);
            Assert.NotEqual(baseline?.SuccessObservationId, newerGood.SuccessObservationId);

            clock.Advance(TimeSpan.FromHours(6).Add(TimeSpan.FromSeconds(1)));
            var restarted = CreateOmdbClient(handler, true, CreateCacheStore(root), timeProvider: clock,
                responseCache: CreateResponseCache(root, clock), emptyResponseCacheHours: 6);
            var refreshed = await restarted.GetByImdbIdWithStatusAsync("tt49639968", default);
            Assert.True(refreshed.Successful);
            Assert.Equal("8.1", refreshed.Value?.ImdbRating);
            Assert.Equal(3, calls);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task NewFailureAfterSameClockSuccessStillDefersAfterRestartAndEmptyExpiry()
    {
        var root = CreateRoot();
        try
        {
            var clock = new OmdbClock(DateTimeOffset.UtcNow);
            var tiedClock = clock.GetUtcNow();
            var goodStore = CreateCacheStore(root);
            var outcomeStore = new FailingOmdbClearStore(CreateOutcomeStore(root));
            var calls = 0;
            var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(++calls == 2
                ? """{"Response":"True"}"""
                : """{"Response":"False","Error":"Incorrect IMDb ID."}"""));
            var client = CreateOmdbClient(handler, true, goodStore, timeProvider: clock,
                responseCache: CreateResponseCache(root, clock, outcomeStore), emptyResponseCacheHours: 6);
            Assert.False((await client.GetByImdbIdWithStatusAsync("tt49639968", default)).Successful);
            Assert.True((await client.GetByImdbIdWithStatusAsync("tt49639968", default, forceRefresh: true)).Successful);
            Assert.Equal(1, outcomeStore.FailedClears);
            var good = await goodStore.GetAsync("tt49639968", default);

            // New response-cache instance removes only the storage-I/O backoff;
            // all provider observations retain the exact same clock value.
            var later = CreateOmdbClient(handler, true, CreateCacheStore(root), timeProvider: clock,
                responseCache: CreateResponseCache(root, clock), emptyResponseCacheHours: 6);
            var failed = await later.GetByImdbIdWithStatusAsync("tt49639968", default, forceRefresh: true);
            Assert.False(failed.Successful);
            Assert.Equal(tiedClock.AddHours(24), failed.Failure?.RetryAtUtc);
            Assert.Equal(good!.SuccessObservationId, (await goodStore.GetAsync("tt49639968", default))!.SuccessObservationId);

            clock.Advance(TimeSpan.FromHours(6).Add(TimeSpan.FromSeconds(1)));
            var restarted = CreateOmdbClient(handler, true, CreateCacheStore(root), timeProvider: clock,
                responseCache: CreateResponseCache(root, clock), emptyResponseCacheHours: 6);
            var deferred = await restarted.GetByImdbIdWithStatusAsync("tt49639968", default);
            Assert.False(deferred.Successful);
            Assert.Equal("local_backoff", deferred.Failure?.Reason);
            Assert.Equal(tiedClock.AddHours(24), deferred.Failure?.RetryAtUtc);
            Assert.Equal(3, calls);
        }
        finally { TryDelete(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherScopeSuccessCannotSupersedeOwnFailureAfterRestartAndEmptyExpiry(bool changeCredential)
    {
        var root = CreateRoot();
        try
        {
            var clock = new OmdbClock(DateTimeOffset.UtcNow);
            var goodStore = CreateCacheStore(root);
            var failedHandler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
                """{"Response":"False","Error":"Incorrect IMDb ID."}"""));
            var first = CreateOmdbClient(failedHandler, true, goodStore, timeProvider: clock,
                responseCache: CreateResponseCache(root, clock), emptyResponseCacheHours: 6);
            var failure = await first.GetByImdbIdWithStatusAsync("tt49639968", default);
            Assert.False(failure.Successful);

            var goodHandler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"Response":"True"}"""));
            var other = new OmdbClient(new HttpClient(goodHandler)
                { BaseAddress = new Uri(changeCredential ? "https://www.omdbapi.com/" : "https://replacement.example/omdb/") },
                new MemoryCache(new MemoryCacheOptions()),
                Microsoft.Extensions.Options.Options.Create(new OmdbOptions
                    { Enabled = true, ApiKey = changeCredential ? "replacement-key" : "test-key", EmptyResponseCacheHours = 6 }),
                cacheStore: goodStore, timeProvider: clock, responseCache: CreateResponseCache(root, clock));
            Assert.True((await other.GetByImdbIdWithStatusAsync("tt49639968", default, forceRefresh: true)).Successful);
            Assert.NotNull((await goodStore.GetAsync("tt49639968", default))!.SuccessObservationId);

            clock.Advance(TimeSpan.FromHours(6).Add(TimeSpan.FromSeconds(1)));
            var liveDeferred = await first.GetByImdbIdWithStatusAsync("tt49639968", default);
            Assert.False(liveDeferred.Successful);
            Assert.Equal(failure.Failure?.RetryAtUtc, liveDeferred.Failure?.RetryAtUtc);
            var restarted = CreateOmdbClient(failedHandler, true, CreateCacheStore(root), timeProvider: clock,
                responseCache: CreateResponseCache(root, clock), emptyResponseCacheHours: 6);
            var deferred = await restarted.GetByImdbIdWithStatusAsync("tt49639968", default);
            Assert.False(deferred.Successful);
            Assert.Equal("local_backoff", deferred.Failure?.Reason);
            Assert.Equal(failure.Failure?.RetryAtUtc, deferred.Failure?.RetryAtUtc);
            Assert.Single(failedHandler.Requests);
            Assert.Single(goodHandler.Requests);
        }
        finally { TryDelete(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherClientSameScopeSuccessSupersedesOldLiveMemoryFailureAfterEmptyExpiry(bool failClear)
    {
        var root = CreateRoot();
        try
        {
            var clock = new OmdbClock(DateTimeOffset.UtcNow);
            var goodStore = CreateCacheStore(root);
            var calls = 0;
            var liveHandler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(++calls == 1
                ? """{"Response":"False","Error":"Incorrect IMDb ID."}"""
                : """{"Response":"True","imdbRating":"8.1"}"""));
            var live = CreateOmdbClient(liveHandler, true, goodStore, timeProvider: clock,
                responseCache: CreateResponseCache(root, clock), emptyResponseCacheHours: 6);
            Assert.False((await live.GetByImdbIdWithStatusAsync("tt49639968", default)).Successful);

            var clearStore = new FailingOmdbClearStore(CreateOutcomeStore(root));
            var goodHandler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"Response":"True"}"""));
            var other = CreateOmdbClient(goodHandler, true, CreateCacheStore(root), timeProvider: clock,
                responseCache: CreateResponseCache(root, clock, failClear ? clearStore : null), emptyResponseCacheHours: 6);
            Assert.True((await other.GetByImdbIdWithStatusAsync("tt49639968", default, forceRefresh: true)).Successful);
            Assert.Equal(failClear ? 1 : 0, clearStore.FailedClears);

            // Keep the original client and its independent memory cache alive.
            clock.Advance(TimeSpan.FromHours(6).Add(TimeSpan.FromSeconds(1)));
            var refreshed = await live.GetByImdbIdWithStatusAsync("tt49639968", default);
            Assert.True(refreshed.Successful);
            Assert.Equal("8.1", refreshed.Value?.ImdbRating);
            Assert.Equal(2, calls);
            Assert.Single(goodHandler.Requests);
        }
        finally { TryDelete(root); }
    }

    [Theory]
    [InlineData("Incorrect IMDb ID.", "payload_invalid_imdb_id", "invalid_imdb_id")]
    [InlineData(" incorrect imdb id. ", "payload_invalid_imdb_id", "invalid_imdb_id")]
    [InlineData("Error getting data.", "payload_data_error", "provider_data_unavailable")]
    [InlineData(null, "payload_missing_error", "invalid_response")]
    [InlineData(" ", "payload_missing_error", "invalid_response")]
    [InlineData("private-title https://private.example/?token=private-api-value", "payload_unrecognized_error", "invalid_response")]
    public async Task FalsePayloadDiagnosticsClassifySafelyAndPreserveVerifiedMetadata(string? error, string category, string reason)
    {
        var root = CreateRoot();
        try
        {
            using var diagnostics = new PerformanceDiagnosticStore(new(), root);
            var telemetry = new PremiereTelemetry(diagnostics);
            var store = CreateCacheStore(root);
            var calls = 0;
            var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(++calls == 1
                ? Fixture.Read("omdb/by-imdb-id-success.json")
                : System.Text.Json.JsonSerializer.Serialize(new { Response = "False", Error = error })));
            var client = CreateOmdbClient(handler, true, store, telemetry);
            var good = await client.GetByImdbIdWithStatusAsync("tt-private-id", default);
            var saved = await store.GetAsync("tt-private-id", default);

            var failed = await client.GetByImdbIdWithStatusAsync("tt-private-id", default, forceRefresh: true);

            Assert.False(failed.Successful);
            Assert.Equal(reason, failed.Failure?.Reason);
            Assert.NotNull(failed.Failure?.RetryAtUtc);
            Assert.Equal(good.Value!.ImdbRating, failed.Value!.ImdbRating);
            Assert.Equal(saved!.CachedAtUtc, (await store.GetAsync("tt-private-id", default))!.CachedAtUtc);
            Assert.Null((await store.GetProviderStateAsync(default)).RateLimitedUntilUtc);
            Assert.True((await client.GetByImdbIdWithStatusAsync("tt-private-id", default)).Successful);
            Assert.Equal(2, handler.Requests.Count);

            telemetry.FlushDiagnostics();
            var lines = Directory.GetFiles(Path.Combine(root, "App_Data", "logs", "performance"), "performance-*.ndjson")
                .SelectMany(path =>
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                }).ToArray();
            var events = lines.Select(line => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(line)).ToArray();
            var outcome = Assert.Single(events, item => item.GetProperty("event").GetString() == "provider.outcome"
                && item.GetProperty("details").GetProperty("outcome").GetString() == "failed");
            Assert.Equal(category, outcome.GetProperty("details").GetProperty("stage").GetString());
            var log = string.Join('\n', lines);
            Assert.DoesNotContain("tt-private-id", log);
            Assert.DoesNotContain("test-key", log);
            Assert.DoesNotContain("private-title", log);
            Assert.DoesNotContain("private.example", log);
            Assert.DoesNotContain("private-api-value", log);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task PersistedQuotaDeadlineSurvivesAnUnrelatedRequestFailure()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateCacheStore(root);
            var until = DateTimeOffset.UtcNow.AddHours(2);
            await store.MarkRateLimitedAsync(until, "rate_limited", CancellationToken.None);
            await store.MarkFailureAsync("http_failure", CancellationToken.None);
            await store.MarkRateLimitedAsync(until.AddMinutes(-30), "authentication_denied", CancellationToken.None);
            var restarted = CreateCacheStore(root);
            var state = await restarted.GetProviderStateAsync(CancellationToken.None);
            Assert.Equal(until, state.RateLimitedUntilUtc);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task EnabledBlankKeyIsIntentionalSkipWithoutHttpOrFailureCooldown()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Unconfigured OMDb must not send requests."));
        var client = new OmdbClient(new HttpClient(handler) { BaseAddress = new Uri("https://www.omdbapi.com/") },
            new MemoryCache(new MemoryCacheOptions()), Microsoft.Extensions.Options.Options.Create(new OmdbOptions { Enabled = true, ApiKey = " " }));
        var result = await client.GetByImdbIdWithStatusAsync("tt0000042", CancellationToken.None, true);
        Assert.True(result.Successful);
        Assert.Null(result.Value);
        Assert.Equal("not_configured", result.Failure?.Reason);
        Assert.Null(result.Failure?.RetryAtUtc);
        Assert.Empty(handler.Requests);
    }

    private static OmdbClient CreateOmdbClient(
        HttpMessageHandler handler,
        bool enabled,
        IOmdbCacheStore? cacheStore = null,
        PremiereTelemetry? telemetry = null,
        TimeProvider? timeProvider = null,
        ProviderResponseCache? responseCache = null,
        int emptyResponseCacheHours = 1)
    {
        return new OmdbClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://www.omdbapi.com/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new OmdbOptions
            {
                Enabled = enabled,
                ApiKey = enabled ? "test-key" : null,
                CacheDays = 90,
                EmptyResponseCacheHours = emptyResponseCacheHours,
                RateLimitBackoffHours = 12
            }),
            cacheStore: cacheStore,
            timeProvider: timeProvider,
            telemetry: telemetry,
            responseCache: responseCache);
    }

    [Fact]
    public async Task ForcedFailureReturnsGoodFallbackButDoesNotReportFreshSuccess()
    {
        var root = CreateRoot();
        try
        {
            var fail = false;
            var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(fail
                ? """{"Response":"False","Error":"Request limit reached!"}"""
                : """{"Response":"True","imdbRating":"7.5","imdbVotes":"1500"}"""));
            var store = CreateCacheStore(root);
            var client = CreateOmdbClient(handler, true, store);
            using var refresh = ProviderRefreshScope.Use(ProviderRefreshScope.Create());
            var first = await client.GetByImdbIdWithStatusAsync("tt0000042", default);
            Assert.True(first.Successful);
            var original = await store.GetAsync("tt0000042", default);
            fail = true;
            var result = await client.GetByImdbIdWithStatusAsync("tt0000042", default, forceRefresh: true);
            Assert.False(result.Successful);
            Assert.Equal("7.5", result.Value!.ImdbRating);
            Assert.Equal(original!.CachedAtUtc, (await store.GetAsync("tt0000042", default))!.CachedAtUtc);
        }
        finally { TryDelete(root); }
    }

    private static SqliteOmdbCacheStore CreateCacheStore(string root)
    {
        TestSqliteDatabase.Initialize(root, "data/app.db");
        return new SqliteOmdbCacheStore(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/app.db" }),
            new FakeWebHostEnvironment(root),
            TimeProvider.System);
    }

    private static IProviderCacheStateStore CreateOutcomeStore(string root)
        => new SqliteProviderCacheStateStore(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/app.db" }),
            new FakeWebHostEnvironment(root));

    private static ProviderResponseCache CreateResponseCache(string root, TimeProvider clock, IProviderCacheStateStore? store = null)
        => new(store ?? CreateOutcomeStore(root), NullLogger<ProviderResponseCache>.Instance, clock);

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

    private sealed class OmdbClock(DateTimeOffset now) : TimeProvider, Microsoft.Extensions.Internal.ISystemClock
    {
        public override DateTimeOffset GetUtcNow() => now;
        public DateTimeOffset UtcNow => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }

    private sealed class FailingOmdbClearStore(IProviderCacheStateStore inner) : IProviderCacheStateStore
    {
        public int FailedClears { get; private set; }
        public Task<ProviderCacheState?> GetAsync(string provider, ProviderCacheScope scope, string key, CancellationToken token)
            => inner.GetAsync(provider, scope, key, token);
        public Task SaveAsync(ProviderCacheState state, CancellationToken token)
        {
            using var document = System.Text.Json.JsonDocument.Parse(state.MetadataJson!);
            if (state.Provider == "responsecache-omdb"
                && document.RootElement.GetProperty("value").GetProperty("failure").ValueKind == System.Text.Json.JsonValueKind.Null)
            {
                FailedClears++;
                throw new IOException("Only the outcome-clear write fails.");
            }
            return inner.SaveAsync(state, token);
        }
        public Task<IReadOnlyList<ProviderCacheState>> GetRecentAsync(int take, CancellationToken token) => inner.GetRecentAsync(take, token);
        public Task<IReadOnlyList<ProviderCacheState>> GetByProviderAsync(string provider, int take, CancellationToken token) => inner.GetByProviderAsync(provider, take, token);
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
