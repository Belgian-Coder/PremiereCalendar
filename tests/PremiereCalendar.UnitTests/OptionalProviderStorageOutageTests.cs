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
    public async Task StorageOutageStillCallsTheProviderAndOnlyRepeatedFailuresBackOffTheStorage(string provider)
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
        var requestsBefore = handler.Requests;
        for (var id = 701; id < 705; id++)
        {
            // Each call creates another transient typed client with a distinct key: storage is a miss, never a failure.
            var outcome = await CallAsync(provider, id, memory, handler, durable, clock);
            Assert.True(outcome.Successful, $"{provider} call {id} must reach the provider while storage is down.");
            Assert.Null(outcome.Failure);
            Assert.True(handler.Requests > requestsBefore);
            requestsBefore = handler.Requests;
        }
        // The first failures are tolerated; once the threshold is crossed storage is no longer touched.
        var readsDuringBackoff = store.Reads;
        Assert.True(readsDuringBackoff > readsBefore);
        for (var id = 705; id < 709; id++)
        {
            Assert.True((await CallAsync(provider, id, memory, handler, durable, clock)).Successful);
        }
        Assert.Equal(readsDuringBackoff, store.Reads);
        store.Unavailable = false;
        clock.Advance(TimeSpan.FromSeconds(6));
        var recovered = await CallAsync(provider, 710, memory, handler, durable, clock);
        Assert.True(recovered.Successful);
        Assert.True(store.Reads > readsDuringBackoff);
    }

    [Fact]
    public void DefaultIoBudgetIsGenerousEnoughForOneSlowOperation()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1500), ProviderResponseCache.DefaultIoBudget);
    }

    [Fact]
    public async Task OneSlowReadBeyondTheOldHalfSecondBudgetStillHitsAndDoesNotBackOff()
    {
        var clock = new Clock();
        var store = new StateStore { ReadDelay = TimeSpan.FromMilliseconds(700) };
        // A generous explicit budget keeps this independent of host load; the point is that a read
        // slower than the old 500 ms budget is a hit and does not start a backoff.
        var durable = Durable(store, clock, TimeSpan.FromSeconds(20));
        await durable.SaveAsync("tmdb", "slow", "value", default);

        var read = await durable.ReadAsync<string>("tmdb", "slow", TimeSpan.FromDays(1), default);

        Assert.Equal(ProviderResponseCacheReadStatus.Hit, read.Status);
        Assert.Null(read.RetryAtUtc);
        Assert.Equal(ProviderResponseCacheReadStatus.Hit, (await durable.ReadAsync<string>("tmdb", "slow", TimeSpan.FromDays(1), default)).Status);
        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public async Task StorageOutageDoesNotStopForcedOrColdProviderCalls()
    {
        var clock = new Clock();
        var store = new StateStore();
        var durable = Durable(store, clock);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var handler = new ProviderHandler();
        var fanart = Fanart(memory, handler, durable, clock);
        Assert.NotNull((await fanart.GetMovieArtworkWithStatusAsync(42, default)).Value);
        var tvmaze = Tvmaze(memory, handler, durable, clock);
        Assert.Single((await tvmaze.GetShowImagesWithStatusAsync(42, default)).Value);
        var watchmode = Watchmode(memory, handler, durable, clock);
        Assert.Single((await watchmode.GetTitleSourcesWithStatusAsync(PremiereMediaType.Movie, 42, null, ["BE"], default)).Value);
        var before = handler.Requests;
        store.Unavailable = true;
        var forced = await fanart.GetMovieArtworkWithStatusAsync(42, default, true);
        Assert.True(forced.Successful);
        Assert.NotNull(forced.Value);
        var coldImages = await tvmaze.GetShowImagesWithStatusAsync(99, default);
        Assert.True(coldImages.Successful);
        Assert.Single(coldImages.Value);
        var coldSources = await watchmode.GetTitleSourcesWithStatusAsync(PremiereMediaType.Movie, 42, null, ["NL"], default);
        Assert.True(coldSources.Successful);
        Assert.NotEmpty(coldSources.Value);
        Assert.True(handler.Requests > before);
    }

    [Fact]
    public async Task RetainedStaleMemoryIsRefetchedWhenStorageIsUnavailable()
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
        // An unavailable durable cache is only a cache miss: the provider is called again.
        Assert.True(result.Successful);
        Assert.Equal("retained", result.Value);
        Assert.Null(result.Failure);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ReadBudgetExhaustionStillCallsTheProviderAndBacksOffAfterRepeatedBudgetFailures()
    {
        var clock = new Clock();
        var store = new StateStore { BlockReads = true };
        var durable = Durable(store, clock, TimeSpan.FromMilliseconds(100));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var handler = new ProviderHandler();
        var response = await Fanart(memory, handler, durable, clock).GetMovieArtworkWithStatusAsync(42, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(response.Successful);
        Assert.Equal(1, handler.Requests);
        var second = await durable.ReadAsync<string>("fanart", "another-key", TimeSpan.FromDays(1), default);
        Assert.Equal(ProviderResponseCacheReadStatus.StorageUnavailable, second.Status);
        // Below the threshold there is no retry time at all (never a deadline in the past).
        Assert.Null(second.RetryAtUtc);
        // Third failure inside the 10 s window opens the 5 s backoff.
        var third = await durable.ReadAsync<string>("fanart", "third-key", TimeSpan.FromDays(1), default);
        Assert.Equal(clock.GetUtcNow().AddSeconds(5), third.RetryAtUtc);
        var reads = store.Reads;
        var skipped = await durable.ReadAsync<string>("fanart", "skipped-key", TimeSpan.FromDays(1), default);
        Assert.Equal(ProviderResponseCacheReadStatus.StorageUnavailable, skipped.Status);
        Assert.Equal(reads, store.Reads);
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
    public async Task SuccessfulFetchSurvivesSaveFailureAndDistinctMissesStillReachTheProvider()
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
        var second = await firstClient.GetMovieArtworkWithStatusAsync(43, default);
        Assert.True(second.Successful);
        Assert.Equal(2, handler.Requests);
        Assert.Equal(2, store.Reads);
    }

    private static ProviderResponseCache Durable(StateStore store, Clock clock, TimeSpan? ioBudget = null) => new(store, NullLogger<ProviderResponseCache>.Instance, clock, ioBudget: ioBudget);
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
        public TimeSpan ReadDelay;
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProviderCacheState?> GetAsync(string provider, ProviderCacheScope scope, string key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            ReadStarted.TrySetResult();
            if (BlockReads) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (ReadDelay > TimeSpan.Zero) await Task.Delay(ReadDelay, cancellationToken);
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
