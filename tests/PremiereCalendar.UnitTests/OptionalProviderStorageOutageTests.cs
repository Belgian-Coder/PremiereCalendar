using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class OptionalProviderStorageOutageTests
{
    [Theory]
    [InlineData("fanart")]
    [InlineData("tvmaze")]
    [InlineData("watchmode")]
    public async Task DistinctKeysAcrossClientsDeferWithoutHttpUntilTheStorageDeadlineRecovers(string provider)
    {
        var clock = new Clock();
        var store = new StateStore();
        var durable = Durable(store, clock);
        await durable.SaveAsync(provider, "prepare", "healthy", default);
        Assert.Equal(ProviderResponseCacheReadStatus.Hit, (await durable.ReadAsync<string>(provider, "prepare", TimeSpan.FromDays(1), default)).Status);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var handler = new ProviderHandler();
        var readsBefore = store.Reads;
        store.Unavailable = true;
        var first = await CallAsync(provider, 701, memory, handler, durable, clock);
        Assert.False(first.Successful);
        if (first.Value is IReadOnlyList<PremiereSource> sources) Assert.Empty(sources);
        else Assert.Null(first.Value);
        Assert.Equal("storage_unavailable", first.Failure?.Reason);
        Assert.True(first.Failure?.IsLocal);
        Assert.Equal(clock.GetUtcNow().AddSeconds(5), first.Failure?.RetryAtUtc);
        Assert.Equal(readsBefore + 1, store.Reads);
        clock.Advance(TimeSpan.FromSeconds(2));
        for (var id = 702; id < 707; id++)
        {
            // Each call creates another transient typed client with a distinct key.
            var deferred = await CallAsync(provider, id, memory, handler, durable, clock);
            Assert.False(deferred.Successful);
            Assert.Equal(first.Failure?.RetryAtUtc, deferred.Failure?.RetryAtUtc);
        }
        Assert.Equal(0, handler.Requests);
        Assert.Equal(readsBefore + 1, store.Reads);
        store.Unavailable = false;
        clock.Advance(TimeSpan.FromSeconds(4));
        var recovered = await CallAsync(provider, 708, memory, handler, durable, clock);
        Assert.True(recovered.Successful);
        Assert.Null(recovered.Failure);
        Assert.True(handler.Requests > 0);
    }

    [Fact]
    public async Task FailedStorageRetainsForcedMemoryValueAndPreservesEmptyListVersusNullableContracts()
    {
        var clock = new Clock();
        var store = new StateStore();
        var durable = Durable(store, clock);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var handler = new ProviderHandler();
        var fanart = Fanart(memory, handler, durable, clock);
        var goodArt = await fanart.GetMovieArtworkWithStatusAsync(42, default);
        Assert.NotNull(goodArt.Value);
        var tvmaze = Tvmaze(memory, handler, durable, clock);
        var goodImages = await tvmaze.GetShowImagesWithStatusAsync(42, default);
        Assert.Single(goodImages.Value);
        var watchmode = Watchmode(memory, handler, durable, clock);
        var goodSources = await watchmode.GetTitleSourcesWithStatusAsync(PremiereMediaType.Movie, 42, null, ["BE"], default);
        Assert.Single(goodSources.Value);
        var before = handler.Requests;
        store.Unavailable = true;
        var retainedArt = await fanart.GetMovieArtworkWithStatusAsync(42, default, true);
        Assert.False(retainedArt.Successful);
        Assert.Same(goodArt.Value, retainedArt.Value);
        var retainedImages = await tvmaze.GetShowImagesWithStatusAsync(42, default, true);
        Assert.False(retainedImages.Successful);
        Assert.Same(goodImages.Value, retainedImages.Value);
        var coldImages = await tvmaze.GetShowImagesWithStatusAsync(99, default);
        Assert.False(coldImages.Successful);
        Assert.NotNull(coldImages.Value);
        Assert.Empty(coldImages.Value);
        var coldLookup = await tvmaze.LookupShowWithStatusAsync(99, null, default);
        var coldSearch = await tvmaze.SearchShowByNameWithStatusAsync("Unseen title", default);
        Assert.False(coldLookup.Successful);
        Assert.False(coldSearch.Successful);
        Assert.Null(coldLookup.Value);
        Assert.Null(coldSearch.Value);
        // The identity is retained, but this new region has no availability value.
        var coldSources = await watchmode.GetTitleSourcesWithStatusAsync(PremiereMediaType.Movie, 42, null, ["NL"], default);
        Assert.False(coldSources.Successful);
        Assert.NotNull(coldSources.Value);
        Assert.Empty(coldSources.Value);
        var retainedSources = await watchmode.GetTitleSourcesWithRefreshPolicyAsync(PremiereMediaType.Movie, 42, null, ["BE"], default,
            forceRefresh: true, forceIdentityRefresh: false);
        Assert.False(retainedSources.Successful);
        Assert.Same(goodSources.Value, retainedSources.Value);
        Assert.Equal(before, handler.Requests);
        Assert.True((await fanart.GetMovieArtworkWithStatusAsync(42, default)).Successful);
        Assert.Equal(before, handler.Requests);
    }

    [Fact]
    public async Task RetainedStaleMemoryIsNotDiscardedWhenStorageIsUnavailable()
    {
        var clock = new Clock();
        var store = new StateStore();
        var durable = Durable(store, clock);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var responses = new OptionalProviderResponseCache(memory, durable, clock);
        var calls = 0;
        Task<ProviderRefreshResult<string?>> Fetch(CancellationToken _) { calls++; return Task.FromResult(new ProviderRefreshResult<string?>("retained", true)); }
        Assert.True((await responses.GetAsync("fanart", "stale", false, TimeSpan.FromDays(1), Fetch, default)).Successful);
        // Advancing the provider clock leaves this otherwise retained memory object
        // outside its provider freshness window without waiting for real wall time.
        clock.Advance(TimeSpan.FromDays(2));
        store.Unavailable = true;
        var result = await responses.GetAsync("fanart", "stale", false, TimeSpan.FromDays(1), Fetch, default);
        Assert.False(result.Successful);
        Assert.Equal("retained", result.Value);
        Assert.Equal("storage_unavailable", result.Failure?.Reason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReadBudgetExhaustionIsUnavailableAndDoesNotStartAProviderFetch()
    {
        var clock = new Clock();
        var store = new StateStore { BlockReads = true };
        var durable = Durable(store, clock);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var handler = new ProviderHandler();
        var response = await Fanart(memory, handler, durable, clock).GetMovieArtworkWithStatusAsync(42, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(response.Successful);
        Assert.Equal("storage_unavailable", response.Failure?.Reason);
        Assert.Equal(clock.GetUtcNow().AddSeconds(5), response.Failure?.RetryAtUtc);
        Assert.Equal(0, handler.Requests);
        var read = await durable.ReadAsync<string>("fanart", "another-key", TimeSpan.FromDays(1), default);
        Assert.Equal(ProviderResponseCacheReadStatus.StorageUnavailable, read.Status);
        Assert.Equal(response.Failure?.RetryAtUtc, read.RetryAtUtc);
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task CallerCancellationDoesNotCreateStorageBackoff()
    {
        var clock = new Clock();
        var store = new StateStore { BlockReads = true };
        var durable = Durable(store, clock);
        using var canceled = new CancellationTokenSource();
        var read = durable.ReadAsync<string>("fanart", "cancel", TimeSpan.FromDays(1), canceled.Token);
        await store.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        store.BlockReads = false;
        var next = await durable.ReadAsync<string>("fanart", "after-cancel", TimeSpan.FromDays(1), default);
        Assert.Equal(ProviderResponseCacheReadStatus.Miss, next.Status);
        Assert.Null(next.RetryAtUtc);
        Assert.Equal(2, store.Reads);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("type")]
    [InlineData("version")]
    [InlineData("expiry")]
    [InlineData("missing")]
    public async Task InvalidOrAbsentEntryRemainsARepairableMiss(string defect)
    {
        var clock = new Clock();
        var store = new StateStore();
        var durable = Durable(store, clock);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var helper = new OptionalProviderResponseCache(memory, durable, clock);
        if (defect != "missing")
        {
            await durable.SaveAsync("fanart", "repair", new OptionalProviderValue<string?>("old"), default);
            var pair = Assert.Single(store.Values);
            var wrongType = JsonNode.Parse(pair.Value.MetadataJson!)!;
            wrongType["type"] = "Wrong.Payload.Type";
            var wrongVersion = JsonNode.Parse(pair.Value.MetadataJson!)!;
            wrongVersion["version"] = 99;
            store.Values[pair.Key] = defect switch
            {
                "json" => pair.Value with { MetadataJson = "{" },
                "type" => pair.Value with { MetadataJson = wrongType.ToJsonString() },
                "version" => pair.Value with { MetadataJson = wrongVersion.ToJsonString() },
                _ => pair.Value with { LastCheckedUtc = clock.GetUtcNow().AddDays(-3) }
            };
        }
        var calls = 0;
        var result = await helper.GetAsync<string?>("fanart", "repair", false, TimeSpan.FromDays(1), _ =>
        { calls++; return Task.FromResult(new ProviderRefreshResult<string?>("repaired", true)); }, default);
        Assert.True(result.Successful);
        Assert.Equal("repaired", result.Value);
        Assert.Equal(1, calls);
        var read = await durable.ReadAsync<OptionalProviderValue<string?>>("fanart", "repair", TimeSpan.FromDays(1), default);
        Assert.Equal(ProviderResponseCacheReadStatus.Hit, read.Status);
        Assert.Equal("repaired", read.Response?.Value.Value);
        Assert.Null(read.RetryAtUtc);
    }

    [Fact]
    public async Task SuccessfulFetchSurvivesSaveFailureWhileDistinctMissesDefer()
    {
        var clock = new Clock();
        var store = new StateStore { FailSaves = true };
        var durable = Durable(store, clock);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var handler = new ProviderHandler();
        var firstClient = Fanart(memory, handler, durable, clock);
        var first = await firstClient.GetMovieArtworkWithStatusAsync(42, default);
        Assert.True(first.Successful);
        Assert.NotNull(first.Value);
        var cached = await Fanart(memory, handler, durable, clock).GetMovieArtworkWithStatusAsync(42, default);
        Assert.True(cached.Successful);
        Assert.Same(first.Value, cached.Value);
        var deferred = await firstClient.GetMovieArtworkWithStatusAsync(43, default);
        Assert.False(deferred.Successful);
        Assert.Equal("storage_unavailable", deferred.Failure?.Reason);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(1, store.Reads);
    }

    private static ProviderResponseCache Durable(StateStore store, Clock clock) => new(store, NullLogger<ProviderResponseCache>.Instance, clock);
    private static FanartClient Fanart(IMemoryCache memory, HttpMessageHandler handler, ProviderResponseCache durable, Clock clock) => new(
        new HttpClient(handler, disposeHandler: false) { BaseAddress = new("https://webservice.fanart.tv/v3/") }, memory,
        Microsoft.Extensions.Options.Options.Create(new FanartOptions { Enabled = true, ApiKey = "fixture" }), responseCache: durable, timeProvider: clock);
    private static TvmazeClient Tvmaze(IMemoryCache memory, HttpMessageHandler handler, ProviderResponseCache durable, Clock clock) => new(
        new HttpClient(handler, disposeHandler: false) { BaseAddress = new("https://api.tvmaze.com/") }, memory,
        Microsoft.Extensions.Options.Options.Create(new TvmazeOptions()), responseCache: durable, timeProvider: clock);
    private static WatchmodeClient Watchmode(IMemoryCache memory, HttpMessageHandler handler, ProviderResponseCache durable, Clock clock) => new(
        new HttpClient(handler, disposeHandler: false) { BaseAddress = new("https://api.watchmode.com/v1/") }, memory,
        Microsoft.Extensions.Options.Options.Create(new WatchmodeOptions { ApiKey = "fixture" }), responseCache: durable, timeProvider: clock);
    private static async Task<Outcome> CallAsync(string provider, int id, IMemoryCache memory, ProviderHandler handler, ProviderResponseCache durable, Clock clock)
    {
        if (provider == "fanart") { var result = await Fanart(memory, handler, durable, clock).GetMovieArtworkWithStatusAsync(id, default); return new(result.Successful, result.Value, result.Failure); }
        if (provider == "tvmaze") { var result = await Tvmaze(memory, handler, durable, clock).LookupShowWithStatusAsync(id, null, default); return new(result.Successful, result.Value, result.Failure); }
        var sources = await Watchmode(memory, handler, durable, clock).GetTitleSourcesWithStatusAsync(PremiereMediaType.Movie, id, null, ["BE"], default);
        Assert.NotNull(sources.Value);
        return new(sources.Successful, sources.Value, sources.Failure);
    }
    private sealed record Outcome(bool Successful, object? Value, ProviderFailure? Failure);
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
    private sealed class ProviderHandler : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath;
            var json = host.Contains("fanart", StringComparison.Ordinal) ? "{\"movieposter\":[{\"url\":\"https://fixture.test/poster.jpg\"}]}"
                : host.Contains("watchmode", StringComparison.Ordinal) ? path.Contains("search", StringComparison.Ordinal)
                    ? "{\"title_results\":[{\"id\":910}]}" : "[{\"source_id\":1,\"name\":\"Fixture\",\"type\":\"sub\"}]"
                : path.EndsWith("/images", StringComparison.Ordinal) ? "[{\"type\":\"poster\",\"resolutions\":{\"original\":{\"url\":\"https://fixture.test/poster.jpg\"}}}]"
                : "{\"id\":42,\"name\":\"Fixture\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class StateStore : IProviderCacheStateStore
    {
        public ConcurrentDictionary<(string Provider, ProviderCacheScope Scope, string Key), ProviderCacheState> Values { get; } = new();
        public bool Unavailable;
        public bool FailSaves;
        public bool BlockReads;
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProviderCacheState?> GetAsync(string provider, ProviderCacheScope scope, string key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            ReadStarted.TrySetResult();
            if (BlockReads) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (Unavailable) throw new IOException("Simulated unavailable response storage.");
            return Values.GetValueOrDefault((provider, scope, key));
        }
        public Task SaveAsync(ProviderCacheState state, CancellationToken cancellationToken)
        {
            if (Unavailable || FailSaves) throw new IOException("Simulated unavailable response storage.");
            Values[(state.Provider, state.Scope, state.Key)] = state;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ProviderCacheState>> GetRecentAsync(int take, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderCacheState>>(Values.Values.Take(take).ToArray());
        public Task<IReadOnlyList<ProviderCacheState>> GetByProviderAsync(string provider, int take, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderCacheState>>(Values.Values.Where(value => value.Provider == provider).Take(take).ToArray());
    }
}
