using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class ProviderResponseCacheIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-response-cache-" + Guid.NewGuid().ToString("N"));

    public ProviderResponseCacheIntegrationTests()
    {
        Directory.CreateDirectory(_root);
        TestSqliteDatabase.Initialize(_root, DatabasePath);
    }

    private string DatabasePath => Path.Combine(_root, "test.db");
    private IProviderCacheStateStore CreateStore() => new SqliteProviderCacheStateStore(
        Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = DatabasePath }), new TestEnvironment(_root));
    private static ProviderResponseCache Cache(IProviderCacheStateStore store, TimeProvider? time = null) =>
        new(store, NullLogger<ProviderResponseCache>.Instance, time);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TmdbDetails_SurviveFreshStoreClientAndMemoryCache_ForceRefreshReplacesPayload(bool television)
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            "{\"id\":42,\"title\":\"Result " + Guid.NewGuid().ToString("N") + "\",\"name\":\"Series\"}"));
        using var firstMemory = new MemoryCache(new MemoryCacheOptions());
        var first = Tmdb(handler, firstMemory, Cache(CreateStore()));
        var initial = await Details(first, television);

        using var secondMemory = new MemoryCache(new MemoryCacheOptions());
        var restarted = Tmdb(handler, secondMemory, Cache(CreateStore()));
        Assert.Equal(JsonSerializer.Serialize(initial), JsonSerializer.Serialize(await Details(restarted, television)));
        Assert.Single(handler.Requests);

        var replacement = await Details(restarted, television, forceRefresh: true);
        Assert.NotEqual(initial!.Title, replacement!.Title);
        using var thirdMemory = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal(JsonSerializer.Serialize(replacement), JsonSerializer.Serialize(await Details(Tmdb(handler, thirdMemory, Cache(CreateStore())), television)));
        Assert.Equal(2, handler.Requests.Count);

        var row = Assert.Single(await CreateStore().GetByProviderAsync("responsecache-tmdb", 100, CancellationToken.None));
        Assert.Equal(ProviderCacheScope.Item, row.Scope);
        Assert.StartsWith("v1:", row.Key);
        Assert.DoesNotContain("test-token", row.Key);
        Assert.DoesNotContain("Result", row.Key);
        Assert.Equal("/3/" + (television ? "tv" : "movie") + "/42", handler.Requests[0].Uri.AbsolutePath);
        Assert.Contains("append_to_response=", handler.Requests[0].Uri.Query);
    }

    [Fact]
    public async Task TmdbDetails_DoNotPersistNotFoundOrFailures()
    {
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest })
        {
            var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{}", status));
            using var memory = new MemoryCache(new MemoryCacheOptions());
            var client = Tmdb(handler, memory, Cache(CreateStore()));
            if (status == HttpStatusCode.NotFound)
                Assert.Null(await Details(client, false));
            else
                await Assert.ThrowsAsync<ExternalApiException>(() => Details(client, false));
            Assert.Empty(await CreateStore().GetByProviderAsync("responsecache-tmdb", 100, CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RottenTomatoes_ScoresAndSuccessfulEmptySurviveRestart_ForceRefreshBypasses(bool empty)
    {
        var score = 72;
        var handler = new StubHttpMessageHandler(request => Html(empty ? "<html>No matches</html>" :
            request.RequestUri!.AbsolutePath == "/search" ?
            $"<search-page-media-row release-year=\"2026\" tomatometer-score=\"{score}\"><a href=\"/m/example\" data-qa=\"info-name\">Example</a></search-page-media-row>" :
            $"<script id=\"media-scorecard-json\" type=\"application/json\">{{\"criticsScore\":{{\"score\":\"{score}\"}}}}</script>"));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var initial = await Scores(RottenTomatoes(handler, memory, Cache(CreateStore())));
        Assert.Equal(empty ? null : 72, initial.CriticScore);
        var requestCount = handler.Requests.Count;
        using var restartedMemory = new MemoryCache(new MemoryCacheOptions());
        var restarted = RottenTomatoes(handler, restartedMemory, Cache(CreateStore()));
        Assert.Equal(initial, await Scores(restarted));
        Assert.Equal(requestCount, handler.Requests.Count);
        Assert.True(restarted.TryGetCachedScores(PremiereMediaType.Movie, "Example", 2026, null, out _));

        score = 91;
        var refreshed = await Scores(restarted, forceRefresh: true);
        Assert.Equal(empty ? null : 91, refreshed.CriticScore);
        Assert.Equal(requestCount * 2, handler.Requests.Count);
        using var finalMemory = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal(refreshed, await Scores(RottenTomatoes(handler, finalMemory, Cache(CreateStore()))));
        Assert.Equal(requestCount * 2, handler.Requests.Count);
        var row = Assert.Single(await CreateStore().GetByProviderAsync("responsecache-rottentomatoes", 100, CancellationToken.None));
        Assert.DoesNotContain("example", row.Key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RottenTomatoes_TransientFailureDoesNotPersistEmpty()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal(RottenTomatoesScores.Empty, await Scores(RottenTomatoes(handler, memory, Cache(CreateStore()))));
        using var restartedMemory = new MemoryCache(new MemoryCacheOptions());
        await Scores(RottenTomatoes(handler, restartedMemory, Cache(CreateStore())));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Empty(await CreateStore().GetByProviderAsync("responsecache-rottentomatoes", 100, CancellationToken.None));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    public async Task AbsoluteExpiry_IsPreservedAcrossRestart_AndExpiredRowsAreMisses(int lifetimeHours)
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new TestTimeProvider(now);
        await Cache(CreateStore(), clock).SaveAsync("tmdb", "request", new RottenTomatoesScores(82, 90), CancellationToken.None);
        clock.Now = now.AddHours(lifetimeHours - 1);
        var result = await Cache(CreateStore(), clock).GetAsync<RottenTomatoesScores>("tmdb", "request", TimeSpan.FromHours(lifetimeHours), CancellationToken.None);
        Assert.Equal(now.AddHours(lifetimeHours), result!.ExpiresAtUtc);
        clock.Now = now.AddHours(lifetimeHours);
        Assert.Null(await Cache(CreateStore(), clock).GetAsync<RottenTomatoesScores>("tmdb", "request", TimeSpan.FromHours(lifetimeHours), CancellationToken.None));
    }

    [Fact]
    public async Task RestartedMemory_UsesRemainingLifetime_NotFreshTwelveHours()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{\"id\":42}"));
        using var initialMemory = new MemoryCache(new MemoryCacheOptions());
        await Details(Tmdb(handler, initialMemory, Cache(CreateStore())), false);
        var store = CreateStore();
        var row = Assert.Single(await store.GetByProviderAsync("responsecache-tmdb", 100, CancellationToken.None));
        await store.SaveAsync(row with { LastCheckedUtc = DateTimeOffset.UtcNow.AddHours(-12).AddSeconds(1) }, CancellationToken.None);
        using var restartedMemory = new MemoryCache(new MemoryCacheOptions());
        var restarted = Tmdb(handler, restartedMemory, Cache(CreateStore()));
        await Details(restarted, false);
        Assert.Single(handler.Requests);
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        await Details(restarted, false);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CorruptWrongVersionWrongTypeAndOversizedPayloads_AreMisses_AndDoNotAffectOtherNamespaces()
    {
        var store = CreateStore();
        var cache = Cache(store);
        await cache.SaveAsync("tmdb", "request", new RottenTomatoesScores(80, 90), CancellationToken.None);
        var row = Assert.Single(await store.GetByProviderAsync("responsecache-tmdb", 100, CancellationToken.None));
        await store.SaveAsync(row with { Provider = "tmdb", Key = "existing-metadata", MetadataJson = "retained" }, CancellationToken.None);
        foreach (var corrupt in new[] { "invalid-json", row.MetadataJson!.Replace("\"version\":1", "\"version\":99"), new string('x', 262145) })
        {
            await store.SaveAsync(row with { MetadataJson = corrupt }, CancellationToken.None);
            Assert.Null(await cache.GetAsync<RottenTomatoesScores>("tmdb", "request", TimeSpan.FromHours(12), CancellationToken.None));
        }
        await store.SaveAsync(row, CancellationToken.None);
        Assert.Null(await cache.GetAsync<TmdbDetailsWithExtras>("tmdb", "request", TimeSpan.FromHours(12), CancellationToken.None));
        await cache.SaveAsync("tmdb", "too-large", new string('x', 262145), CancellationToken.None);
        Assert.Single(await store.GetByProviderAsync("responsecache-tmdb", 100, CancellationToken.None));
        Assert.Equal("retained", (await store.GetAsync("tmdb", ProviderCacheScope.Item, "existing-metadata", CancellationToken.None))!.MetadataJson);
        var recent = Assert.Single(await store.GetRecentAsync(100, CancellationToken.None));
        Assert.Equal("tmdb", recent.Provider);
        Assert.Equal("existing-metadata", recent.Key);
    }

    [Fact]
    public async Task StoreFailures_FailOpen_AndCallerCancellationPropagates()
    {
        var failing = new InterceptingStore(CreateStore()) { Fail = true };
        var cache = Cache(failing);
        await cache.SaveAsync("tmdb", "request", RottenTomatoesScores.Empty, CancellationToken.None);
        Assert.Null(await cache.GetAsync<RottenTomatoesScores>("tmdb", "request", TimeSpan.FromHours(12), CancellationToken.None));
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{\"id\":42}"));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        Assert.NotNull(await Details(Tmdb(handler, memory, cache), false));
        Assert.Single(handler.Requests);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SaveAsync("tmdb", "request", RottenTomatoesScores.Empty, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync<RottenTomatoesScores>("tmdb", "request", TimeSpan.FromHours(12), canceled.Token));

        var blocked = new InterceptingStore(CreateStore()) { BlockReads = true };
        using var cancellation = new CancellationTokenSource();
        using var coldMemory = new MemoryCache(new MemoryCacheOptions());
        var pending = Tmdb(handler, coldMemory, Cache(blocked)).GetMovieDetailsAsync(42, cancellation.Token);
        await blocked.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConcurrentColdMisses_PerformOnePersistentReadAndOneHttpRequest()
    {
        var store = new InterceptingStore(CreateStore()) { BlockReads = true };
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{\"id\":42}"));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var client = Tmdb(handler, memory, Cache(store));
        var requests = Enumerable.Range(0, 20).Select(_ => Details(client, false)).ToArray();
        await store.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.ReleaseRead.TrySetResult();
        Assert.All(await Task.WhenAll(requests), value => Assert.Equal(42, value!.Id));
        Assert.Single(handler.Requests);
        Assert.Equal(1, store.ReadCount);
        Assert.Equal(1, store.WriteCount);
    }

    [Fact]
    public async Task CacheIoBudget_CancelsAndObservesStoreWork_FallsBackToHttp_AndPreservesCallerCancellation()
    {
        var blockedWrite = new InterceptingStore(CreateStore()) { BlockWrites = true };
        var save = Cache(blockedWrite).SaveAsync("tmdb", "request", RottenTomatoesScores.Empty, CancellationToken.None);
        await blockedWrite.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await save.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(blockedWrite.WriteCanceled);
        Assert.Empty(await CreateStore().GetByProviderAsync("responsecache-tmdb", 100, CancellationToken.None));

        var blockedRead = new InterceptingStore(CreateStore()) { BlockReads = true };
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{\"id\":42}"));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var fetch = Details(Tmdb(handler, memory, Cache(blockedRead)), false);
        await blockedRead.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42, (await fetch.WaitAsync(TimeSpan.FromSeconds(5)))!.Id);
        Assert.True(blockedRead.ReadCanceled);
        Assert.Single(handler.Requests);

        var callerBlockedWrite = new InterceptingStore(CreateStore()) { BlockWrites = true };
        using var callerCancellation = new CancellationTokenSource();
        var canceledSave = Cache(callerBlockedWrite).SaveAsync("tmdb", "caller", RottenTomatoesScores.Empty, callerCancellation.Token);
        await callerBlockedWrite.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledSave);
        Assert.True(callerBlockedWrite.WriteCanceled);
    }

    [Fact]
    public async Task RottenTomatoesConcurrentColdMisses_PerformOneReadAndOneSearch()
    {
        var store = new InterceptingStore(CreateStore()) { BlockReads = true };
        var handler = new StubHttpMessageHandler(_ => Html("<html>No matching results</html>"));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var client = RottenTomatoes(handler, memory, Cache(store));
        var requests = Enumerable.Range(0, 20).Select(_ => Scores(client)).ToArray();
        await store.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.ReleaseRead.TrySetResult();
        Assert.All(await Task.WhenAll(requests), value => Assert.Equal(RottenTomatoesScores.Empty, value));
        Assert.Equal("/search", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal(1, store.ReadCount);
        Assert.Equal(1, store.WriteCount);
    }

    [Fact]
    public async Task ResponseRetention_RemovesOnlyOldItemRowsInTheTwoExactNamespaces()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        ProviderCacheState Row(string provider, ProviderCacheScope scope, string key, int daysOld) =>
            new(provider, scope, key, now.AddDays(-daysOld), null, null, null, "preserve metadata");
        var rows = new[]
        {
            Row("responsecache-tmdb", ProviderCacheScope.Item, "expired-tmdb", 9),
            Row("responsecache-rottentomatoes", ProviderCacheScope.Item, "expired-rt", 9),
            Row("responsecache-tmdb", ProviderCacheScope.Item, "fresh", 7),
            Row("responsecache-rottentomatoes", ProviderCacheScope.Item, "boundary", 8),
            Row("tmdb", ProviderCacheScope.Item, "v1:legacy", 90),
            Row("rottentomatoes", ProviderCacheScope.Item, "legacy", 90),
            Row("responsecache-tmdb", ProviderCacheScope.Week, "other-scope", 90),
            Row("responsecache-rottentomatoes", ProviderCacheScope.Global, "other-global", 90),
            Row("responsecache-tmdb-other", ProviderCacheScope.Item, "other-namespace", 90)
        };
        await store.SaveManyAsync(rows, CancellationToken.None);
        Assert.Equal(2, await Cache(CreateStore(), new TestTimeProvider(now)).CleanupAsync(CancellationToken.None));
        foreach (var row in rows.Skip(2))
            Assert.Equal(row, await store.GetAsync(row.Provider, row.Scope, row.Key, CancellationToken.None));
        foreach (var row in rows.Take(2))
            Assert.Null(await store.GetAsync(row.Provider, row.Scope, row.Key, CancellationToken.None));
        Assert.Equal(0, await Cache(CreateStore(), new TestTimeProvider(now)).CleanupAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Maintenance_RunsResponseRetentionOnlyWhenEnabled(bool enabled)
    {
        var now = DateTimeOffset.UtcNow;
        var row = new ProviderCacheState("responsecache-tmdb", ProviderCacheScope.Item, "expired", now.AddDays(-9), null, null, null, "{}");
        await CreateStore().SaveAsync(row, CancellationToken.None);
        var clock = new TestTimeProvider(now);
        var noOp = new NoOpMaintenance();
        var runner = new CacheMaintenanceRunner(noOp, noOp, noOp,
            Microsoft.Extensions.Options.Options.Create(new CalendarWarmupOptions()),
            Microsoft.Extensions.Options.Options.Create(new CacheMaintenanceOptions { Enabled = enabled }),
            clock, NullLogger<CacheMaintenanceRunner>.Instance, Cache(CreateStore(), clock));
        await runner.RunOnceAsync(CancellationToken.None);
        Assert.Equal(enabled, await CreateStore().GetAsync(row.Provider, row.Scope, row.Key, CancellationToken.None) is null);
        Assert.Equal(enabled ? 2 : 0, noOp.CleanupCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TmdbHeldOldPersistentRead_CannotOverwriteACompletedForceRefreshAcrossTransientClients(bool television)
    {
        var title = "Old";
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json($"{{\"id\":42,\"title\":\"{title}\",\"name\":\"{title}\"}}"));
        using var seedMemory = new MemoryCache(new MemoryCacheOptions());
        await Details(Tmdb(handler, seedMemory, Cache(CreateStore())), television);

        var held = new InterceptingStore(CreateStore()) { BlockReads = true, SnapshotBeforeBlock = true };
        var sharedResponseCache = Cache(held);
        using var sharedMemory = new MemoryCache(new MemoryCacheOptions());
        var normal = Details(Tmdb(handler, sharedMemory, sharedResponseCache), television);
        await held.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        title = "New";
        Assert.Equal("New", (await Details(Tmdb(handler, sharedMemory, sharedResponseCache), television, forceRefresh: true))!.Title);
        held.ReleaseRead.TrySetResult();
        Assert.Equal("New", (await normal)!.Title);
        Assert.Equal("New", (await Details(Tmdb(handler, sharedMemory, sharedResponseCache), television))!.Title);
        using var restartedMemory = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal("New", (await Details(Tmdb(handler, restartedMemory, Cache(CreateStore())), television))!.Title);
        Assert.Equal(2, handler.Requests.Count);
        AssertNoPublicationEntries(sharedResponseCache);
    }

    [Fact]
    public async Task RottenTomatoesHeldOldPersistentRead_CannotOverwriteACompletedForceRefreshAcrossTransientClients()
    {
        var score = 72;
        var handler = new StubHttpMessageHandler(request => Html(request.RequestUri!.AbsolutePath == "/search"
            ? $"<search-page-media-row release-year=\"2026\" tomatometer-score=\"{score}\"><a href=\"/m/example\" data-qa=\"info-name\">Example</a></search-page-media-row>"
            : $"<script id=\"media-scorecard-json\">{{\"criticsScore\":{{\"score\":\"{score}\"}}}}</script>"));
        using var seedMemory = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal(72, (await Scores(RottenTomatoes(handler, seedMemory, Cache(CreateStore())))).CriticScore);

        var held = new InterceptingStore(CreateStore()) { BlockReads = true, SnapshotBeforeBlock = true };
        var sharedResponseCache = Cache(held);
        using var sharedMemory = new MemoryCache(new MemoryCacheOptions());
        var normal = Scores(RottenTomatoes(handler, sharedMemory, sharedResponseCache));
        await held.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        score = 91;
        Assert.Equal(91, (await Scores(RottenTomatoes(handler, sharedMemory, sharedResponseCache), forceRefresh: true)).CriticScore);
        held.ReleaseRead.TrySetResult();
        Assert.Equal(91, (await normal).CriticScore);
        Assert.Equal(91, (await Scores(RottenTomatoes(handler, sharedMemory, sharedResponseCache))).CriticScore);
        using var restartedMemory = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal(91, (await Scores(RottenTomatoes(handler, restartedMemory, Cache(CreateStore())))).CriticScore);
        Assert.Equal(4, handler.Requests.Count);
        AssertNoPublicationEntries(sharedResponseCache);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task RealWikimedia_OnlyVerifiedMissingIdentifiersPermitPersistentEmptyScores(HttpStatusCode wikimediaStatus, bool persistEmpty)
    {
        var wikiHandler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{\"entities\":{\"Q42\":{\"claims\":{}}}}", wikimediaStatus));
        var rtHandler = new StubHttpMessageHandler(_ => Html("<html>No matching results</html>"));
        for (var restart = 0; restart < 2; restart++)
        {
            using var memory = new MemoryCache(new MemoryCacheOptions());
            var wiki = new WikimediaClient(new HttpClient(wikiHandler) { BaseAddress = new Uri("https://www.wikidata.org/") }, memory,
                Microsoft.Extensions.Options.Options.Create(new WikimediaOptions()));
            var rt = RottenTomatoes(rtHandler, memory, Cache(CreateStore()), wiki);
            Assert.Equal(RottenTomatoesScores.Empty, await rt.GetScoresAsync(PremiereMediaType.Movie, "Example", 2026, "Q42", CancellationToken.None));
        }
        Assert.Equal(persistEmpty ? 1 : 2, wikiHandler.Requests.Count);
        Assert.Equal(persistEmpty ? 1 : 2, rtHandler.Requests.Count);
        Assert.Equal(persistEmpty ? 1 : 0, (await CreateStore().GetByProviderAsync("responsecache-rottentomatoes", 100, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task SqliteHeldWriter_ResponseCacheUsesOneSecondBusyBound_BackoffAndRecovery_CallerCancellationStillThrows()
    {
        await using var writer = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await writer.OpenAsync();
        await using var transaction = writer.BeginTransaction(deferred: false);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        var responseCache = Cache(CreateStore(), clock);
        var started = Stopwatch.GetTimestamp();
        await responseCache.SaveAsync("tmdb", "locked", RottenTomatoesScores.Empty, CancellationToken.None);
        Assert.InRange(Stopwatch.GetElapsedTime(started).TotalSeconds, 0, 3);

        started = Stopwatch.GetTimestamp();
        await responseCache.SaveAsync("tmdb", "backoff", RottenTomatoesScores.Empty, CancellationToken.None);
        Assert.InRange(Stopwatch.GetElapsedTime(started).TotalSeconds, 0, 1);
        Assert.Empty(await CreateStore().GetByProviderAsync("responsecache-tmdb", 100, CancellationToken.None));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        started = Stopwatch.GetTimestamp();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cache(CreateStore()).SaveAsync("tmdb", "canceled", RottenTomatoesScores.Empty, cancellation.Token));
        Assert.InRange(Stopwatch.GetElapsedTime(started).TotalSeconds, 0, 3);
        await transaction.RollbackAsync();
        clock.Now = clock.Now.AddSeconds(6);
        await responseCache.SaveAsync("tmdb", "recovered", RottenTomatoesScores.Empty, CancellationToken.None);
        Assert.NotNull(await responseCache.GetAsync<RottenTomatoesScores>("tmdb", "recovered", TimeSpan.FromHours(12), CancellationToken.None));
        Assert.Single(await CreateStore().GetByProviderAsync("responsecache-tmdb", 100, CancellationToken.None));
    }

    [Fact]
    public async Task ForcedOversizedResponse_InvalidatesOlderPersistentPayload_AndRestartFetchesAgain()
    {
        var large = false;
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
        {
            id = 42, title = large ? "New oversized" : "Old", overview = large ? new string('x', 300_000) : "Small"
        })));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var client = Tmdb(handler, memory, Cache(CreateStore()));
        Assert.Equal("Old", (await Details(client, false))!.Title);
        large = true;
        Assert.Equal("New oversized", (await Details(client, false, forceRefresh: true))!.Title);
        var row = Assert.Single(await CreateStore().GetByProviderAsync("responsecache-tmdb", 100, CancellationToken.None));
        Assert.Equal("{\"version\":0}", row.MetadataJson);
        using var restartedMemory = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal("New oversized", (await Details(Tmdb(handler, restartedMemory, Cache(CreateStore())), false))!.Title);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalHttpStartedDuringForce_CannotOverwriteSuccessfulForce_ButCanPublishAfterForceFailure(bool forceSucceeds)
    {
        var forceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var normalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseForce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNormal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var handler = new AsyncHttpMessageHandler(async (_, token) =>
        {
            var request = Interlocked.Increment(ref requestCount);
            if (request == 1)
            {
                forceStarted.TrySetResult();
                await releaseForce.Task.WaitAsync(token);
                return forceSucceeds ? StubHttpMessageHandler.Json("{\"id\":42,\"title\":\"New\"}")
                    : StubHttpMessageHandler.Json("{}", HttpStatusCode.BadRequest);
            }
            normalStarted.TrySetResult();
            await releaseNormal.Task.WaitAsync(token);
            return StubHttpMessageHandler.Json("{\"id\":42,\"title\":\"Old\"}");
        });
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var sharedResponseCache = Cache(CreateStore());
        var forced = Details(Tmdb(handler, memory, sharedResponseCache), false, forceRefresh: true);
        await forceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var normal = Details(Tmdb(handler, memory, sharedResponseCache), false);
        await normalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseForce.TrySetResult();
        if (forceSucceeds) Assert.Equal("New", (await forced)!.Title);
        else await Assert.ThrowsAsync<ExternalApiException>(() => forced);
        releaseNormal.TrySetResult();
        var expected = forceSucceeds ? "New" : "Old";
        Assert.Equal(expected, (await normal)!.Title);
        using var restartedMemory = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal(expected, (await Details(Tmdb(handler, restartedMemory, Cache(CreateStore())), false))!.Title);
        Assert.Equal(2, requestCount);
        AssertNoPublicationEntries(sharedResponseCache);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RottenTomatoesFailedForcedFetch_PreservesValidNormalScoresInEitherCompletionOrder(bool normalCompletesFirst, bool persistence)
    {
        var forceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var normalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseForce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNormal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var handler = new AsyncHttpMessageHandler(async (request, token) =>
        {
            var number = Interlocked.Increment(ref requestCount);
            if (number == 1)
            {
                forceStarted.TrySetResult();
                await releaseForce.Task.WaitAsync(token);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            if (request.RequestUri!.AbsolutePath == "/search")
            {
                normalStarted.TrySetResult();
                await releaseNormal.Task.WaitAsync(token);
                return Html("<search-page-media-row release-year=\"2026\" tomatometer-score=\"88\"><a href=\"/m/example\" data-qa=\"info-name\">Example</a></search-page-media-row>");
            }
            return Html("<script id=\"media-scorecard-json\">{\"criticsScore\":{\"score\":\"88\"}}</script>");
        });
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var sharedResponseCache = Cache(CreateStore());
        var forced = Scores(RottenTomatoes(handler, memory, persistence ? sharedResponseCache : null), forceRefresh: true);
        await forceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var normal = Scores(RottenTomatoes(handler, memory, persistence ? sharedResponseCache : null));
        await normalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (normalCompletesFirst)
        {
            releaseNormal.TrySetResult();
            Assert.Equal(88, (await normal).CriticScore);
        }
        releaseForce.TrySetResult();
        Assert.Equal(RottenTomatoesScores.Empty, await forced);
        releaseNormal.TrySetResult();
        Assert.Equal(88, (await normal).CriticScore);
        Assert.Equal(88, (await Scores(RottenTomatoes(handler, memory, persistence ? sharedResponseCache : null))).CriticScore);
        using var restartedMemory = new MemoryCache(new MemoryCacheOptions());
        if (persistence) Assert.Equal(88, (await Scores(RottenTomatoes(handler, restartedMemory, Cache(CreateStore())))).CriticScore);
        Assert.Equal(3, requestCount);
        AssertNoPublicationEntries(sharedResponseCache);
    }

    [Fact]
    public async Task RottenTomatoesNormalFetchStartedBeforeFailedForce_ReturnsItsValidScoresWhenForceFailsFirst()
    {
        var normalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNormal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var handler = new AsyncHttpMessageHandler(async (request, token) =>
        {
            var number = Interlocked.Increment(ref requestCount);
            if (number == 1)
            {
                normalStarted.TrySetResult();
                await releaseNormal.Task.WaitAsync(token);
                return Html("<search-page-media-row release-year=\"2026\" tomatometer-score=\"88\"><a href=\"/m/example\" data-qa=\"info-name\">Example</a></search-page-media-row>");
            }
            return request.RequestUri!.AbsolutePath == "/search"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Html("<script id=\"media-scorecard-json\">{\"criticsScore\":{\"score\":\"88\"}}</script>");
        });
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var sharedResponseCache = Cache(CreateStore());
        var normal = Scores(RottenTomatoes(handler, memory, sharedResponseCache));
        await normalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RottenTomatoesScores.Empty, await Scores(RottenTomatoes(handler, memory, sharedResponseCache), forceRefresh: true));
        releaseNormal.TrySetResult();
        Assert.Equal(88, (await normal).CriticScore);
        Assert.Equal(3, requestCount);
        AssertNoPublicationEntries(sharedResponseCache);
    }

    [Theory]
    [InlineData("tmdb", "rottentomatoes")]
    [InlineData("rottentomatoes", "tmdb")]
    public async Task ProviderIoBackoff_DoesNotSuppressOtherProvidersPersistence(string failedProvider, string healthyProvider)
    {
        var store = new InterceptingStore(CreateStore()) { BlockReads = true, BlockProvider = $"responsecache-{failedProvider}" };
        var cache = Cache(store, new TestTimeProvider(DateTimeOffset.UtcNow));
        Assert.Null(await cache.GetAsync<RottenTomatoesScores>(failedProvider, "blocked", TimeSpan.FromHours(12), CancellationToken.None));
        Assert.True(store.ReadCanceled);
        var scores = new RottenTomatoesScores(88, 91);
        await cache.SaveAsync(healthyProvider, "healthy", scores, CancellationToken.None);
        Assert.Equal(scores, (await cache.GetAsync<RottenTomatoesScores>(healthyProvider, "healthy", TimeSpan.FromHours(12), CancellationToken.None))!.Value);
        await cache.SaveAsync(failedProvider, "backoff", scores, CancellationToken.None);
        Assert.Equal(2, store.ReadCount);
        Assert.Equal(1, store.WriteCount);
        Assert.Single(await CreateStore().GetByProviderAsync($"responsecache-{healthyProvider}", 100, CancellationToken.None));
        Assert.Empty(await CreateStore().GetByProviderAsync($"responsecache-{failedProvider}", 100, CancellationToken.None));
    }

    private static void AssertNoPublicationEntries(ProviderResponseCache cache)
    {
        var entries = (System.Collections.IDictionary)typeof(ProviderResponseCache)
            .GetField("_publications", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(cache)!;
        Assert.Empty(entries);
    }

    private static TmdbClient Tmdb(HttpMessageHandler handler, IMemoryCache memory, ProviderResponseCache cache) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") }, memory,
        Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "test-token" }),
        NullLogger<TmdbClient>.Instance, responseCache: cache);
    private static RottenTomatoesClient RottenTomatoes(HttpMessageHandler handler, IMemoryCache memory, ProviderResponseCache? cache, IWikimediaClient? wikimedia = null) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://www.rottentomatoes.com/") }, memory,
        Microsoft.Extensions.Options.Options.Create(new RottenTomatoesOptions()), NullLogger<RottenTomatoesClient>.Instance,
        wikimediaClient: wikimedia, responseCache: cache);
    private static Task<TmdbDetailsWithExtras?> Details(TmdbClient client, bool television, bool forceRefresh = false) => television
        ? client.GetTvDetailsAsync(42, CancellationToken.None, forceRefresh)
        : client.GetMovieDetailsAsync(42, CancellationToken.None, forceRefresh);
    private static Task<RottenTomatoesScores> Scores(RottenTomatoesClient client, bool forceRefresh = false) =>
        client.GetScoresAsync(PremiereMediaType.Movie, "Example", 2026, null, CancellationToken.None, forceRefresh);
    private static HttpResponseMessage Html(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "text/html") };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AsyncHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class NoOpMaintenance : ICalendarCacheMaintenance, IImageCacheMaintenance, ICalendarFilterUsageStore
    {
        public int CleanupCalls;
        public Task<int> CleanupAsync(DateTimeOffset nowUtc, TimeSpan retention, CancellationToken token)
        {
            CleanupCalls++;
            return Task.FromResult(0);
        }
        public Task<int> CleanupAsync(DateTimeOffset cutoffUtc, IReadOnlySet<string> retainedProfileKeys, CancellationToken token) => Task.FromResult(0);
        public Task<CalendarCacheMetadata?> GetWeekMetadataAsync(DateOnly start, DateOnly end, string key, CancellationToken token) => Task.FromResult<CalendarCacheMetadata?>(null);
        public Task<IReadOnlyList<CalendarFilterUsageProfile>> GetTopProfilesAsync(int count, DateTimeOffset now, TimeSpan retention, CancellationToken token) => Task.FromResult<IReadOnlyList<CalendarFilterUsageProfile>>([]);
        public Task<CalendarFilterUsageProfile?> GetProfileAsync(string key, CancellationToken token) => Task.FromResult<CalendarFilterUsageProfile?>(null);
        public Task RecordUseAsync(CalendarPageMode mode, CalendarFilters filters, int count, DateTimeOffset now, CancellationToken token) => Task.CompletedTask;
        public Task MarkWarmedAsync(string key, CalendarPageMode mode, CalendarFilters filters, bool isDefault, int count, DateTimeOffset now, CancellationToken token) => Task.CompletedTask;
        public Task MarkWarmFailedAsync(string key, CalendarPageMode mode, CalendarFilters filters, bool isDefault, string failure, DateTimeOffset now, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PremiereCalendar.IntegrationTests";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class InterceptingStore(IProviderCacheStateStore inner) : IProviderCacheStateStore
    {
        public bool Fail { get; set; }
        public bool BlockReads { get; set; }
        public bool SnapshotBeforeBlock { get; set; }
        public bool BlockWrites { get; set; }
        public string? BlockProvider { get; set; }
        public bool ReadCanceled { get; private set; }
        public bool WriteCanceled { get; private set; }
        public int ReadCount;
        public int WriteCount;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProviderCacheState?> GetAsync(string provider, ProviderCacheScope scope, string key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ReadCount);
            var snapshot = SnapshotBeforeBlock ? await inner.GetAsync(provider, scope, key, cancellationToken) : null;
            ReadStarted.TrySetResult();
            if (BlockReads && (BlockProvider is null || BlockProvider == provider))
            {
                try { await ReleaseRead.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { ReadCanceled = true; throw; }
            }
            if (Fail) throw new IOException("test failure with sensitive payload");
            return SnapshotBeforeBlock ? snapshot : await inner.GetAsync(provider, scope, key, cancellationToken);
        }
        public async Task SaveAsync(ProviderCacheState state, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref WriteCount);
            WriteStarted.TrySetResult();
            if (BlockWrites && (BlockProvider is null || BlockProvider == state.Provider))
            {
                try { await ReleaseWrite.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { WriteCanceled = true; throw; }
            }
            if (Fail) throw new IOException("test failure with sensitive payload");
            await inner.SaveAsync(state, cancellationToken);
        }
        public Task<IReadOnlyList<ProviderCacheState>> GetRecentAsync(int take, CancellationToken token) => inner.GetRecentAsync(take, token);
        public Task<IReadOnlyList<ProviderCacheState>> GetByProviderAsync(string provider, int take, CancellationToken token) => inner.GetByProviderAsync(provider, take, token);
    }
}
