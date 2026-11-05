using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class SelectiveProviderDeltaTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-05-16T12:00:00Z");

    [Fact]
    public async Task DeltaInspectsOnlyKnownChangedTitlesAndReplayUsesActualChangeTimestamp()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/3/movie/changes" => StubHttpMessageHandler.Json("""{"page":1,"total_pages":1,"results":[{"id":42},{"id":99999}]}"""),
            "/3/tv/changes" => StubHttpMessageHandler.Json("""{"page":1,"total_pages":1,"results":[]}"""),
            "/3/movie/42/changes" => StubHttpMessageHandler.Json("""{"changes":[{"key":"overview","items":[{"time":"2026-05-16 10:00:00 UTC"}]}]}"""),
            _ => throw new InvalidOperationException("Unexpected non-targeted request")
        });
        var state = new StateStore();
        var items = new ItemStore { Known = [42] };
        var clock = new Clock();
        var service = Service(Tmdb(handler), Tvmaze(new(_ => StubHttpMessageHandler.Json("{}")), false), state, items, clock: clock);
        await service.RunOnceAsync(default);
        Assert.Equal(Now, (await state.GetAsync("tmdb", ProviderCacheScope.Global, "movie-changes", default))!.LastCheckedUtc);
        await service.RunOnceAsync(default);
        Assert.Single(handler.Requests, request => request.Uri.AbsolutePath == "/3/movie/42/changes");
        // Successful feeds keep their own cadence; replay the overlapping window
        // once that cadence is due, rather than requesting an immediate duplicate.
        clock.UtcNow = Now.AddHours(1);
        await service.RunOnceAsync(default);
        Assert.Equal(1, items.InvalidationChanges);
        Assert.Equal(DateTimeOffset.Parse("2026-05-16T10:00:00Z"), items.Invalidated[42]);
        Assert.Equal(2, handler.Requests.Count(request => request.Uri.AbsolutePath == "/3/movie/42/changes"));
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath.Contains("99999"));
        var marker = await state.GetAsync("tmdb", ProviderCacheScope.Global, "movie-changes", default);
        Assert.Equal(Now.AddHours(1), marker!.LastCheckedUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedHistoryOrInvalidationDoesNotAdvanceSuccessfulWatermark(bool failInvalidation)
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/3/movie/changes" => StubHttpMessageHandler.Json("""{"page":1,"total_pages":1,"results":[{"id":42}]}"""),
            "/3/tv/changes" => StubHttpMessageHandler.Json("""{"page":1,"total_pages":1,"results":[]}"""),
            "/3/movie/42/changes" => StubHttpMessageHandler.Json(failInvalidation
                ? """{"changes":[{"key":"overview","items":[{"time":"2026-05-16 10:00:00 UTC"}]}]}"""
                : """{"changes":[{"key":"overview","items":[{"time":"invalid"}]}]}"""),
            _ => throw new InvalidOperationException("Unexpected request outside the targeted media feeds")
        });
        var state = new StateStore();
        var previous = new ProviderCacheState("tmdb", ProviderCacheScope.Global, "movie-changes", Now.AddDays(-1), null, "old", 0, null);
        await state.SaveAsync(previous, default);
        var items = new ItemStore { Known = [42], FailInvalidation = failInvalidation };
        var service = Service(Tmdb(handler), Tvmaze(new(_ => StubHttpMessageHandler.Json("{}")), false), state, items);
        await Assert.ThrowsAsync<ExternalApiException>(() => service.RunOnceAsync(default));
        Assert.Equal(previous, await state.GetAsync("tmdb", ProviderCacheScope.Global, "movie-changes", default));
        Assert.Equal(Now, (await state.GetAsync("tmdb", ProviderCacheScope.Global, "tv-changes", default))!.LastCheckedUtc);
    }

    [Fact]
    public async Task OverCapChangeFeedFailsInsteadOfAcceptingPartialCoverage()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"page":1,"total_pages":2,"results":[{"id":42}]}"""));
        await Assert.ThrowsAsync<ExternalApiException>(() => Tmdb(handler, maxPages: 1)
            .GetChangedMovieIdsAsync(new(2026, 5, 15), new(2026, 5, 16), default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task IrrelevantCastHistoryDoesNotInvalidateCachedMetadata()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"changes":[{"key":"cast","items":[{"time":"2026-05-16 10:00:00 UTC"}]}]}"""));
        Assert.Equal(DateTimeOffset.MinValue, await Tmdb(handler).GetLastMetadataChangeUtcAsync(
            PremiereMediaType.Movie, 42, new(2026, 5, 15), new(2026, 5, 16), default));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"page\":2,\"total_pages\":1,\"results\":[]}")]
    [InlineData("{\"page\":1,\"results\":[]}")]
    [InlineData("{\"page\":1,\"total_pages\":1}")]
    [InlineData("{\"page\":1,\"total_pages\":1,\"results\":null}")]
    public async Task MalformedChangePageCannotBecomeSuccessfulEmptyCoverage(string body)
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(body));
        await Assert.ThrowsAsync<ExternalApiException>(() => Tmdb(handler)
            .GetChangedMovieIdsAsync(new(2026, 5, 15), new(2026, 5, 16), default));
    }

    [Fact]
    public async Task TvmazeFailedOrNullFeedIsNotRecordedAsSuccessfullyEmpty()
    {
        foreach (var response in new[] { StubHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable), StubHttpMessageHandler.Json("null") })
        {
            var state = new StateStore();
            var service = Service(Tmdb(new(_ => throw new InvalidOperationException())),
                Tvmaze(new(_ => response), true), state, new(), useTmdb: false, useTvmaze: true);
            await Assert.ThrowsAsync<ExternalApiException>(() => service.RunOnceAsync(default));
            Assert.Null(await state.GetAsync("tvmaze", ProviderCacheScope.Global, "show-updates", default));
        }
    }

    [Fact]
    public async Task TvmazeRecoveryUsesWiderWindowAndFencesUnknownReservationsBeforeKnownIds()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"123":1778925600}"""));
        var state = new StateStore();
        await state.SaveAsync(new("tvmaze", ProviderCacheScope.Global, "show-updates", Now.AddDays(-5), null, "day", 0, null), default);
        var items = new ItemStore { KnownTvmaze = [123] };
        await Service(Tmdb(new(_ => throw new InvalidOperationException())), Tvmaze(handler, true), state, items,
            useTmdb: false, useTvmaze: true).RunOnceAsync(default);
        Assert.Contains("since=week", Assert.Single(handler.Requests).Uri.Query);
        Assert.Equal(["unmapped", "known"], items.TvmazeOrder);
    }

    [Fact]
    public async Task CheckedTvmazeCoverageBypassesOptionalCachedFeed()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            ++calls == 1 ? "null" : "{\"123\":1778925600}"));
        var client = Tvmaze(handler, true);
        Assert.Empty(await client.GetShowUpdatesAsync(TvmazeUpdateWindow.Day, default));
        Assert.Single(await client.GetShowUpdatesCheckedAsync(TvmazeUpdateWindow.Day, default));
        Assert.Single(await client.GetShowUpdatesCheckedAsync(TvmazeUpdateWindow.Day, default));
        Assert.Equal(3, calls);
    }

    private static ProviderDeltaSyncService Service(ITmdbClient tmdb, ITvmazeClient tvmaze, StateStore state, ItemStore items,
        bool useTmdb = true, bool useTvmaze = false, Clock? clock = null) => new(tmdb, tvmaze, state,
        new Monitor<ProviderDeltaSyncOptions>(new() { UseTmdbChanges = useTmdb, UseTvmazeUpdates = useTvmaze }),
        clock ?? new Clock(), NullLogger<ProviderDeltaSyncService>.Instance, itemEnrichmentStore: items);

    private static TmdbClient Tmdb(StubHttpMessageHandler handler, int maxPages = 500) => new(
        new HttpClient(handler) { BaseAddress = new("https://api.themoviedb.org/3/") }, new MemoryCache(new MemoryCacheOptions()),
        Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "test", MaxPagesPerQuery = maxPages, MaxUnfilteredPagesPerQuery = maxPages }),
        NullLogger<TmdbClient>.Instance);

    private static TvmazeClient Tvmaze(StubHttpMessageHandler handler, bool enabled) => new(
        new HttpClient(handler) { BaseAddress = new("https://api.tvmaze.com/") }, new MemoryCache(new MemoryCacheOptions()),
        Microsoft.Extensions.Options.Options.Create(new TvmazeOptions { Enabled = enabled }));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
    private sealed class StateStore : IProviderCacheStateStore
    {
        private readonly Dictionary<(string, ProviderCacheScope, string), ProviderCacheState> _rows = [];
        public Task<ProviderCacheState?> GetAsync(string provider, ProviderCacheScope scope, string key, CancellationToken token)
            => Task.FromResult(_rows.GetValueOrDefault((provider, scope, key)));
        public Task SaveAsync(ProviderCacheState state, CancellationToken token) { _rows[(state.Provider, state.Scope, state.Key)] = state; return Task.CompletedTask; }
        public Task<IReadOnlyList<ProviderCacheState>> GetRecentAsync(int take, CancellationToken token) => Task.FromResult<IReadOnlyList<ProviderCacheState>>(_rows.Values.Take(take).ToArray());
        public Task<IReadOnlyList<ProviderCacheState>> GetByProviderAsync(string provider, int take, CancellationToken token) => Task.FromResult<IReadOnlyList<ProviderCacheState>>(_rows.Values.Where(row => row.Provider == provider).Take(take).ToArray());
    }
    private sealed class ItemStore : IItemEnrichmentStore
    {
        public HashSet<int> Known { get; init; } = [];
        public HashSet<int> KnownTvmaze { get; init; } = [];
        public Dictionary<int, DateTimeOffset> Invalidated { get; } = [];
        public List<string> TvmazeOrder { get; } = [];
        public bool FailInvalidation { get; init; }
        public int InvalidationChanges { get; private set; }
        public Task<IReadOnlySet<int>> FindKnownTmdbIdsAsync(PremiereMediaType media, IReadOnlyCollection<int> ids, CancellationToken token) => Task.FromResult<IReadOnlySet<int>>(ids.Where(Known.Contains).ToHashSet());
        public Task<IReadOnlySet<int>> FindKnownTvmazeIdsAsync(IReadOnlyCollection<int> ids, CancellationToken token)
            => Task.FromResult<IReadOnlySet<int>>(ids.Where(KnownTvmaze.Contains).ToHashSet());
        public Task<int> InvalidateTmdbAsync(PremiereMediaType media, IReadOnlyCollection<int> ids, DateTimeOffset changedAt, CancellationToken token)
        {
            if (FailInvalidation) throw new IOException("Simulated interrupted invalidation");
            var count = 0;
            foreach (var id in ids) if (!Invalidated.TryGetValue(id, out var old) || changedAt > old) { Invalidated[id] = changedAt; count++; }
            InvalidationChanges += count;
            return Task.FromResult(count);
        }
        public Task<int> FenceUnmappedTvmazeReservationsAsync(DateTimeOffset epoch, CancellationToken token) { TvmazeOrder.Add("unmapped"); return Task.FromResult(0); }
        public Task<int> InvalidateTvmazeAsync(IReadOnlyCollection<int> ids, DateTimeOffset changed, CancellationToken token) { TvmazeOrder.Add("known"); return Task.FromResult(0); }
        public Task<int> CleanupAsync(DateTimeOffset cutoff, CancellationToken token) => Task.FromResult(0);
        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadManyAsync(string p, IReadOnlyCollection<ItemEnrichmentIdentity> ids, bool reserve, CancellationToken token) => throw new NotSupportedException();
        public Task<int> WriteManyAsync(string p, IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken token) => throw new NotSupportedException();
    }
}
