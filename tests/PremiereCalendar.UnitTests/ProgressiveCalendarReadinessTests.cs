using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task ProgressiveCache_BatchInvalidationForcesFreshDiscoveryEvenAfterRebaseConsumesItsEvidence()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var inner = new MemoryItemEnrichmentStore(clock);
        var store = new ProgressiveInvalidationStore(inner);
        var coordinator = new ItemEnrichmentCacheCoordinator(store,
            Microsoft.Extensions.Options.Options.Create(new PremiereCalendar.Options.ItemEnrichmentCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new PremiereCalendar.Options.TmdbOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ItemEnrichmentCacheCoordinator>.Instance, timeProvider: clock);
        var cache = new FakeCalendarCache();
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), calendarCache: cache,
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, default, filters: SourceListFilters());
        var tmdb = new FakeTmdbClient();
        var service = CreateService(tmdb, calendarCache: cache, itemEnrichmentCache: coordinator);
        await using var stream = service.StreamPremieresAsync(date, date, filters: SourceListFilters()).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.True(stream.Current.IsCalendarReady);
        store.BeforeCurrentRead = () =>
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            inner.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), default).GetAwaiter().GetResult();
        };
        PremiereLoadProgress? final = null;
        while (await stream.MoveNextAsync()) if (stream.Current.IsFinal) final = stream.Current;
        Assert.NotNull(final);
        Assert.Empty(final.Items);
        Assert.True(Assert.Single(tmdb.MovieCalls).ForceRefresh);
        Assert.Empty(cache.SetCalls.Last().Items);
    }

    [Fact]
    public async Task ProgressiveCache_ReadyBeforeProvidersAndPublishesRepairsBeforeTerminalCompletion()
    {
        var date = new DateOnly(2026, 11, 2);
        var rows = Enumerable.Range(1, 5).Select(id => new PremiereItem
        {
            CanonicalId = $"movie:{id}", TmdbId = id, Title = $"Cached {id}",
            MediaType = PremiereMediaType.Movie, Type = PremiereItemType.MovieFirstRelease,
            PremiereDate = date, RuntimeMinutes = 100, OriginalLanguage = "en"
        }).ToArray();
        var tmdb = new FakeTmdbClient();
        var tomatoes = new ProgressiveReadyTomatoes();
        var service = CreateService(tmdb, calendarCache: new FakeCalendarCache { Items = rows },
            rottenTomatoes: tomatoes, maxEnrichmentConcurrency: 2);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var filters = SourceListFilters();
        filters.ScoreSource = ScoreSource.RottenTomatoes;
        filters.IncludeUnknownScores = true;
        await using var stream = service.StreamPremieresAsync(date, date, filters: filters,
            cancellationToken: budget.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.True(stream.Current.IsCalendarReady);
        Assert.True(stream.Current.IsEnriching);
        Assert.False(stream.Current.IsFinal);
        Assert.Equal(5, stream.Current.Items.Count);
        Assert.Equal(0, tomatoes.Calls);

        Assert.True(await stream.MoveNextAsync());
        var patch = stream.Current;
        Assert.Equal("enrichment", patch.Phase);
        Assert.Null(patch.CheckpointKey);
        Assert.True(patch.IsCalendarReady);
        Assert.False(patch.IsFinal);
        Assert.Equal(2, patch.Items.Count(item => item.RottenTomatoesScore == 81));
        var next = stream.MoveNextAsync().AsTask();
        await tomatoes.Held.Task.WaitAsync(budget.Token);
        Assert.False(next.IsCompleted);
        // Published snapshots are immutable while later batches are in flight.
        Assert.Equal(2, patch.Items.Count(item => item.RottenTomatoesScore == 81));
        tomatoes.Release.TrySetResult();
        Assert.True(await next);
        PremiereLoadProgress? final = null;
        do { if (stream.Current.IsFinal) final = stream.Current; }
        while (await stream.MoveNextAsync());
        Assert.NotNull(final);
        Assert.True(final.IsCalendarReady);
        Assert.False(final.IsEnriching);
        Assert.Equal(rows.Select(row => row.TmdbId), final.Items.Select(row => row.TmdbId));
        Assert.All(final.Items, item => Assert.Equal(81, item.RottenTomatoesScore));
        Assert.Equal(5, tomatoes.Calls);
        Assert.Empty(tmdb.MovieCalls);
    }

    [Fact]
    public async Task ProgressiveCache_UnprocessedCardsKeepTheirLocallyHydratedScoresAndEligibility()
    {
        var date = new DateOnly(2026, 11, 2);
        var rows = Enumerable.Range(1, 5).Select(id => new PremiereItem
        {
            CanonicalId = $"movie:{id}", TmdbId = id, Title = $"Cached {id}",
            MediaType = PremiereMediaType.Movie, Type = PremiereItemType.MovieFirstRelease,
            PremiereDate = date, RuntimeMinutes = 100, OriginalLanguage = "en"
        }).ToArray();
        var provider = new ProgressiveReadyTomatoes { ReuseLaterCached = true };
        var service = CreateService(new FakeTmdbClient(), calendarCache: new FakeCalendarCache { Items = rows },
            rottenTomatoes: provider, maxEnrichmentConcurrency: 2);
        var filters = SourceListFilters();
        filters.ScoreSource = ScoreSource.RottenTomatoes;
        filters.MinScore = 8;
        filters.IncludeUnknownScores = false;
        await using var stream = service.StreamPremieresAsync(date, date, filters: filters).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(new[] { 3, 4, 5 }, stream.Current.Items.Select(item => item.TmdbId));
        Assert.True(await stream.MoveNextAsync());
        Assert.False(stream.Current.IsFinal);
        Assert.Equal(5, stream.Current.Items.Count);
        Assert.All(stream.Current.Items.Where(item => item.TmdbId > 2), item => Assert.Equal(91, item.RottenTomatoesScore));
        while (await stream.MoveNextAsync())
            Assert.Equal(5, stream.Current.Items.Count);
        Assert.Equal(2, provider.Calls);
    }

    private sealed class ProgressiveReadyTomatoes : IRottenTomatoesClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReuseLaterCached { get; init; }
        public bool TryGetCachedScores(PremiereMediaType type, string title, int? year, string? wikidataId,
            out RottenTomatoesScores scores)
        {
            scores = ReuseLaterCached && int.Parse(title.Split(' ')[1]) > 2 ? new(91, 95) : RottenTomatoesScores.Empty;
            return scores.HasAnyScore;
        }
        public async Task<RottenTomatoesScores> GetScoresAsync(PremiereMediaType type, string title, int? year,
            string? wikidataId, CancellationToken token, bool forceRefresh = false)
        {
            if (Interlocked.Increment(ref _calls) > 2)
            {
                Held.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            return new(81, 87);
        }
    }

    private sealed class ProgressiveInvalidationStore(IItemEnrichmentStore inner) : IItemEnrichmentStore
    {
        public Action? BeforeCurrentRead;
        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadManyAsync(string partition,
            IReadOnlyCollection<ItemEnrichmentIdentity> identities, bool reserveMissing, CancellationToken token)
            => inner.ReadManyAsync(partition, identities, reserveMissing, token);
        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadCurrentManyAsync(string partition,
            IReadOnlyCollection<ItemEnrichmentIdentity> identities, CancellationToken token)
        {
            Interlocked.Exchange(ref BeforeCurrentRead, null)?.Invoke();
            return inner.ReadCurrentManyAsync(partition, identities, token);
        }
        public Task<int> WriteManyAsync(string partition, IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken token)
            => inner.WriteManyAsync(partition, writes, token);
        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> WriteAcceptedAsync(string partition,
            IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken token) => inner.WriteAcceptedAsync(partition, writes, token);
        public Task<int> InvalidateTmdbAsync(PremiereMediaType type, IReadOnlyCollection<int> ids, DateTimeOffset changed, CancellationToken token)
            => inner.InvalidateTmdbAsync(type, ids, changed, token);
        public Task<int> InvalidateTvmazeAsync(IReadOnlyCollection<int> ids, DateTimeOffset changed, CancellationToken token)
            => inner.InvalidateTvmazeAsync(ids, changed, token);
        public Task<int> CleanupAsync(DateTimeOffset cutoff, CancellationToken token) => inner.CleanupAsync(cutoff, token);
    }
}
