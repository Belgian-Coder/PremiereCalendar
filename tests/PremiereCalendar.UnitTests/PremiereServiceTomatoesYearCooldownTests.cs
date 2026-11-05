using System.Text.Json;
using System.Collections.Concurrent;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task ConcurrentReleaseYearsRunSeparateTomatoesFlightsWithoutOmittingOrMisattributingScores()
    {
        var firstDate = new DateOnly(2025, 11, 2);
        var secondDate = firstDate.AddYears(1);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock, cacheOptions: new ItemEnrichmentCacheOptions { RatingsRefreshHours = 1 });
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(firstDate), omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator,
            rottenTomatoes: new YearCooldownTomatoesClient { Result = new(11, 12) })
            .GetPremieresAsync(firstDate, firstDate, default, filters: filters);
        clock.Advance(TimeSpan.FromHours(2));
        var pair = Assert.Single(store.Records);
        var seeded = JsonSerializer.Deserialize<ItemEnrichmentRatings>(pair.Value.RatingsJson!)!;
        // Keep IMDb current so only the year-bound RT provider needs work.
        store.Records[pair.Key] = pair.Value with
            { RatingsJson = JsonSerializer.Serialize(seeded with { ExternalFetchedUtc = clock.GetUtcNow() }) };
        var firstTmdb = ItemCacheMovie(firstDate);
        var secondTmdb = ItemCacheMovie(secondDate);
        var omdb = new FakeOmdbClient { ThrowOnGet = true };
        var tomatoes = new HeldYearTomatoesClient();
        var first = CreateService(firstTmdb, omdb: omdb, rottenTomatoes: tomatoes, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(firstDate, firstDate, default, filters: filters);
        Task<IReadOnlyList<PremiereItem>>? second = null;
        try
        {
            await tomatoes.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = CreateService(secondTmdb, omdb: omdb, rottenTomatoes: tomatoes, itemEnrichmentCache: coordinator)
                .GetPremieresAsync(secondDate, secondDate, default, filters: filters);
            await tomatoes.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal([2025, 2026], tomatoes.Years.Order());
            tomatoes.ReleaseFirst.SetResult();
            var firstRow = Assert.Single(await first.WaitAsync(TimeSpan.FromSeconds(5)));
            tomatoes.ReleaseSecond.SetResult();
            var secondRow = Assert.Single(await second.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(firstDate, firstRow.PremiereDate);
            Assert.Equal(83, firstRow.RottenTomatoesScore);
            Assert.Equal(91, firstRow.RottenTomatoesAudienceScore);
            Assert.Equal(secondDate, secondRow.PremiereDate);
            Assert.Equal(94, secondRow.RottenTomatoesScore);
            Assert.Equal(96, secondRow.RottenTomatoesAudienceScore);
            Assert.Equal(firstRow.CanonicalId, secondRow.CanonicalId);
            Assert.Equal(firstRow.Title, secondRow.Title);
            Assert.Equal(firstRow.WikidataId, secondRow.WikidataId);
            Assert.Equal(7.1, firstRow.ImdbScore);
            Assert.Equal(7.1, secondRow.ImdbScore);
            Assert.Empty(omdb.Calls);
            Assert.Empty(firstTmdb.MovieDetailCalls);
            Assert.Empty(secondTmdb.MovieDetailCalls);
            Assert.Equal(2, tomatoes.Years.Count);
        }
        finally
        {
            tomatoes.ReleaseFirst.TrySetResult();
            tomatoes.ReleaseSecond.TrySetResult();
            if (second is null) await first.WaitAsync(TimeSpan.FromSeconds(10));
            else await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CorrectedReleaseYearRetriesTomatoesDuringOldYearCooldownAndPreservesMatchingLastGoodValues()
    {
        var originalDate = new DateOnly(2025, 11, 2);
        var correctedDate = originalDate.AddYears(1);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock, cacheOptions: new ItemEnrichmentCacheOptions
            { RatingsRefreshHours = 1, RatingsFailureRetryMinutes = 180 });
        var filters = new CalendarFilters { ShowSeries = false };
        var tomatoes = new YearCooldownTomatoesClient { Result = new(83, 91) };
        var initialOmdb = ItemCacheOmdb("7.1");
        initialOmdb.ItemsByImdbId["tt0000901"] = initialOmdb.ItemsByImdbId["tt0000901"] with { Metascore = "74" };
        await CreateService(ItemCacheMovie(originalDate), omdb: initialOmdb, rottenTomatoes: tomatoes, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(originalDate, originalDate, default, filters: filters);
        var originalRecord = Assert.Single(store.Records.Values);
        var originalRatings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(originalRecord.RatingsJson!)!;

        clock.Advance(TimeSpan.FromHours(2));
        tomatoes.Successful = false;
        var currentOmdb = ItemCacheOmdb("7.8");
        currentOmdb.ItemsByImdbId["tt0000901"] = currentOmdb.ItemsByImdbId["tt0000901"] with { Metascore = "74" };
        var retained = Assert.Single(await CreateService(ItemCacheMovie(originalDate), omdb: currentOmdb,
            rottenTomatoes: tomatoes, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(originalDate, originalDate, default, filters: filters));
        var failedRecord = Assert.Single(store.Records.Values);
        var failedRatings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(failedRecord.RatingsJson!)!;
        Assert.Equal(83, retained.RottenTomatoesScore);
        Assert.Equal(91, retained.RottenTomatoesAudienceScore);
        Assert.Equal(originalRatings.TomatoesValues, failedRatings.TomatoesValues);
        Assert.Equal(originalRatings.TomatoesFetchedUtc, failedRatings.TomatoesFetchedUtc);
        Assert.Equal(originalRecord.MetadataFetchedUtc, failedRecord.MetadataFetchedUtc);

        // Block the other rating gates too, so the incoming year must participate
        // in the coordinator's decision to start a refresh, not only the provider call.
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 901);
        var session = await coordinator.CreateSessionAsync(default);
        await session.PrepareAsync([identity], null, default);
        var deadline = clock.GetUtcNow().AddHours(3);
        session.RecordComponentOutcome(identity, "omdb", false, deadline);
        session.RecordComponentOutcome(identity, "imdb_dataset", false, deadline);
        Assert.False(session.ComponentRetryAllowed(identity, "rottentomatoes"));
        clock.Advance(TimeSpan.FromMinutes(1));
        var sameYear = Assert.Single(await CreateService(ItemCacheMovie(originalDate), omdb: currentOmdb,
            rottenTomatoes: tomatoes, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(originalDate, originalDate, default, filters: filters));
        Assert.Equal(83, sameYear.RottenTomatoesScore);
        Assert.Equal([2025, 2025], tomatoes.Years);

        tomatoes.Successful = true;
        tomatoes.Result = new(94, 96);
        var corrected = Assert.Single(await CreateService(ItemCacheMovie(correctedDate), omdb: currentOmdb,
            rottenTomatoes: tomatoes, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(correctedDate, correctedDate, default, filters: filters));

        Assert.Equal([2025, 2025, 2026], tomatoes.Years);
        Assert.Equal(94, corrected.RottenTomatoesScore);
        Assert.Equal(96, corrected.RottenTomatoesAudienceScore);
        Assert.Equal(7.8, corrected.ImdbScore);
        Assert.Equal(74, corrected.MetacriticScore);
        Assert.Equal(correctedDate, corrected.PremiereDate);
        Assert.Single(initialOmdb.Calls);
        Assert.Single(currentOmdb.Calls);
        var recoveredRecord = Assert.Single(store.Records.Values);
        var recoveredRatings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(recoveredRecord.RatingsJson!)!;
        Assert.Equal(originalRecord.MetadataFetchedUtc, recoveredRecord.MetadataFetchedUtc);
        Assert.Equal(failedRatings.ExternalValues, recoveredRatings.ExternalValues);
        Assert.Equal(failedRatings.ExternalFetchedUtc, recoveredRatings.ExternalFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), recoveredRatings.TomatoesFetchedUtc);
        Assert.Equal(2026, recoveredRatings.Year);

        var recoveredSession = await coordinator.CreateSessionAsync(default);
        await recoveredSession.PrepareAsync([identity], null, default);
        Assert.True(recoveredSession.ComponentRetryAllowed(identity, "rottentomatoes"));
        Assert.False(recoveredSession.ComponentRetryAllowed(identity, "rottentomatoes",
            ItemEnrichmentCacheCoordinator.ItemEnrichmentSession.ComponentIdentity("rottentomatoes",
                wikidataId: recoveredRatings.WikidataId, title: recoveredRatings.Title, year: 2025)));
    }

    private sealed class YearCooldownTomatoesClient : IRottenTomatoesClient
    {
        public List<int?> Years { get; } = [];
        public RottenTomatoesScores Result = RottenTomatoesScores.Empty;
        public bool Successful = true;
        public Task<RottenTomatoesScores> GetScoresAsync(PremiereMediaType type, string title, int? year,
            string? wikidataId, CancellationToken token, bool forceRefresh = false) => Task.FromResult(Result);
        public Task<ProviderRefreshResult<RottenTomatoesScores>> GetScoresWithStatusAsync(PremiereMediaType type,
            string title, int? year, string? wikidataId, CancellationToken token, bool forceRefresh = false)
        {
            Years.Add(year);
            return Task.FromResult(new ProviderRefreshResult<RottenTomatoesScores>(Result, Successful));
        }
    }

    private sealed class HeldYearTomatoesClient : IRottenTomatoesClient
    {
        public ConcurrentQueue<int?> Years { get; } = new();
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RottenTomatoesScores> GetScoresAsync(PremiereMediaType type, string title, int? year,
            string? wikidataId, CancellationToken token, bool forceRefresh = false) => throw new NotSupportedException();
        public async Task<ProviderRefreshResult<RottenTomatoesScores>> GetScoresWithStatusAsync(PremiereMediaType type,
            string title, int? year, string? wikidataId, CancellationToken token, bool forceRefresh = false)
        {
            Years.Enqueue(year);
            if (year == 2025)
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(token);
                return new(new RottenTomatoesScores(83, 91), true);
            }
            Assert.Equal(2026, year);
            SecondEntered.TrySetResult();
            await ReleaseSecond.Task.WaitAsync(token);
            return new(new RottenTomatoesScores(94, 96), true);
        }
    }
}
