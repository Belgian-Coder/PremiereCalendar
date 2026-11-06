using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(false, "null")]
    [InlineData(false, "payload")]
    [InlineData(false, "clock")]
    [InlineData(true, "null")]
    [InlineData(true, "payload")]
    [InlineData(true, "clock")]
    public async Task ContinuationRatingsFenceRejectsARepairInsideTheWrite(bool fresh, string baseline)
    {
        await using var fixture = await AtomicRatingsFixture.CreateAsync(fresh, baseline);
        await fixture.QueueOptionalAsync();
        ItemEnrichmentRatingsBaseline? repair = null;
        fixture.Store.BeforeWrite = (writes, _) =>
        {
            if (Assert.Single(writes).ExpectedMetadataRevision == 1) repair = fixture.RepairRatings();
            return Task.CompletedTask;
        };
        await fixture.Session.FlushAsync(default);
        fixture.AssertRepairAndMetadata(repair!);
        var reused = await fixture.Session.GetAsync(fixture.Identity, true, (_, _, _, _) =>
            throw new InvalidOperationException("A later duplicate must retain the guarded repair."), default);
        Assert.Equal(repair!.Json, JsonSerializer.Serialize(reused.Ratings));
        Assert.Equal(1, fixture.RefreshCalls);
    }

    [Fact]
    public async Task ContinuationRatingsFenceRetainsProducerBaselineBeforeCoreAcceptance()
    {
        await using var fixture = await AtomicRatingsFixture.CreateAsync(fresh: false, "null");
        ItemEnrichmentRatingsBaseline? repair = null;
        fixture.Store.BeforeWrite = (writes, _) =>
        {
            if (Assert.Single(writes).ExpectedMetadataRevision == 0) repair = fixture.RepairRatings();
            return Task.CompletedTask;
        };
        await fixture.QueueOptionalAsync();
        fixture.Store.BeforeWrite = null;
        await fixture.Session.FlushAsync(default);
        fixture.AssertRepairAndMetadata(repair!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuationRatingsFenceSurvivesAWriteFailureAndRequeue(bool fresh)
    {
        await using var fixture = await AtomicRatingsFixture.CreateAsync(fresh, "payload");
        await fixture.QueueOptionalAsync();
        fixture.Store.BeforeWrite = (writes, _) => Assert.Single(writes).ExpectedMetadataRevision == 1
            ? throw new IOException("Retry transaction unavailable.") : Task.CompletedTask;
        await fixture.Session.FlushAsync(default);
        Assert.True(fixture.Session.HasPendingWrites);
        fixture.Store.BeforeWrite = null;
        var repair = fixture.RepairRatings();
        await fixture.Session.FlushAsync(default);
        fixture.AssertRepairAndMetadata(repair);
        Assert.Equal(1, fixture.RefreshCalls);
    }

    [Theory]
    [InlineData("ratings")]
    [InlineData("identity")]
    [InlineData("generation")]
    public async Task ContinuationRatingsFenceUsesCurrentWinnersAndClearsOnInvalidation(string change)
    {
        await using var fixture = await AtomicRatingsFixture.CreateAsync(fresh: true, "null");
        await fixture.QueueOptionalAsync();
        fixture.Store.BeforeWrite = (writes, _) =>
        {
            if (Assert.Single(writes).ExpectedMetadataRevision == 1) fixture.RepairRatings();
            return Task.CompletedTask;
        };
        await fixture.Session.FlushAsync(default);
        fixture.Store.BeforeWrite = null;
        var current = fixture.Current;
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(current.MetadataJson!)!;
        var ratings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(current.RatingsJson!)!;
        fixture.Replace(change switch
        {
            "identity" => current with { MetadataJson = JsonSerializer.Serialize(metadata with { ImdbId = "tt999" }), MetadataRevision = 3 },
            "generation" => current with { Generation = 1, MetadataInvalidatedUtc = fixture.Clock.GetUtcNow().AddMinutes(1) },
            _ => current with { RatingsJson = JsonSerializer.Serialize(ratings with { ImdbScore = 9.8 }) }
        });
        await fixture.Session.ReconcileAsync([], includeConsumed: true, default);
        var accepted = fixture.Session.Find(fixture.Identity)!;
        Assert.Equal(change == "ratings", accepted.PreserveRatings);
        if (change == "ratings") Assert.Equal(9.8, accepted.Ratings!.ImdbScore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuationRatingsFencePreservesFirstRepairWhenProvidersReturnNoPayload(bool fresh)
    {
        await using var fixture = await AtomicRatingsFixture.CreateAsync(fresh, "payload");
        await fixture.QueueOptionalAsync(noRatingsPayload: true);
        ItemEnrichmentRatingsBaseline? repair = null;
        fixture.Store.BeforeWrite = (writes, _) =>
        {
            if (repair is null && Assert.Single(writes).ExpectedMetadataRevision == (fresh ? 0 : 1))
                repair = fixture.RepairRatings();
            return Task.CompletedTask;
        };
        await fixture.Session.FlushAsync(default);
        fixture.AssertRepairAndMetadata(repair!);
        var cachedSource = fixture.Complete.Apply(new PremiereItem { CanonicalId = "movie:901", TmdbId = 901,
            MediaType = PremiereMediaType.Movie, Type = PremiereItemType.MovieFirstRelease, Title = fixture.Core.Title!,
            PremiereDate = new DateOnly(2026, 11, 2), ImdbScore = 6.5 });
        Assert.Equal(9.9, Assert.Single(fixture.Session.ApplyPreservedRatings([cachedSource])).ImdbScore);
        fixture.Session.SynchronizeMaintainedProjection([cachedSource]);
        await fixture.Session.FlushAsync(default);
        fixture.AssertRepairAndMetadata(repair!);
        var hydrated = await fixture.Session.GetAsync(fixture.Identity, false, (_, _, _, _) =>
            throw new InvalidOperationException("Failed providers cannot replay stale cached scores after a repair."), default, ratingsOnly: true);
        Assert.Equal(9.9, hydrated.Ratings!.ImdbScore);
    }

    [Theory]
    [InlineData(false, "null", "none")]
    [InlineData(true, "null", "none")]
    [InlineData(false, "payload", "none")]
    [InlineData(true, "payload", "none")]
    [InlineData(false, "clock", "none")]
    [InlineData(true, "clock", "none")]
    [InlineData(true, "null", "winner-passes")]
    [InlineData(true, "null", "winner-fails")]
    public async Task ForcedMovieRatingsFenceSurvivesDuplicateAndFinalHydrationButAllowsNextVisit(bool beforeInitial, string baseline, string scoreFilter)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var memory = new MemoryItemEnrichmentStore(clock);
        var store = new CoreContinuationStore(memory);
        var coordinator = new ItemEnrichmentCacheCoordinator(store,
            Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions()), NullLogger<ItemEnrichmentCacheCoordinator>.Instance,
            timeProvider: clock);
        var template = ItemCacheMovie(date, duplicate: true);
        var rejectedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eligibleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEligible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejectedTmdb = new FakeTmdbClient { MovieItems = template.MovieItems, MovieDetailOverride = async (_, token) =>
        {
            rejectedEntered.TrySetResult();
            await releaseRejected.Task.WaitAsync(token);
            return template.MovieDetailsById[901] with { Runtime = 20 };
        } };
        var eligibleTmdb = new FakeTmdbClient { MovieItems = template.MovieItems, MovieStreamBatches = template.MovieStreamBatches,
            MovieDetailOverride = async (_, token) =>
            {
                eligibleEntered.TrySetResult();
                await releaseEligible.Task.WaitAsync(token);
                return template.MovieDetailsById[901] with { Runtime = 20 };
            } };
        var proposedScore = scoreFilter == "winner-fails" ? 9.9 : 7.1;
        var repairedScore = scoreFilter == "winner-fails" ? 7.1 : 9.9;
        var omdb = ItemCacheOmdb(proposedScore.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var tomatoes = new ComponentTomatoesClient { Result = new(94, 96) };
        var artwork = new OptionalOwnershipArtwork { Candidate = new("https://fixture.invalid/fanart.jpg", ArtworkSources.Fanart) };
        var watchmode = new FakeWatchmodeClient { Sources = [new() { Id = 19, Name = "Available", Kind = "flatrate" }] };
        var cache = new FakeCalendarCache();
        var reports = new List<PremiereLoadProgress>();
        PremiereService EligibleService(bool useCache = true) => CreateService(eligibleTmdb, omdb: omdb, rottenTomatoes: tomatoes,
            artworkProviders: [artwork], watchmode: watchmode, calendarCache: useCache ? cache : null, itemEnrichmentCache: coordinator);
        var eligibleFilters = new CalendarFilters { ShowSeries = false, ScoreSource = ScoreSource.Imdb,
            MinScore = scoreFilter == "none" ? 0 : 8, IncludeUnknownScores = scoreFilter == "none",
            MovieFilters = { RuntimeMaxMinutes = 60 } };
        if (baseline != "null")
        {
            await memory.ReadManyAsync(coordinator.Partition, [new(PremiereMediaType.Movie, 901)], true, default);
            var pair = Assert.Single(memory.Records);
            var seed = new ItemEnrichmentRatings(baseline == "clock" ? repairedScore : 6.5, 6500, 77, 78, 79,
                "tt0000901", "Q901", "Cached movie", 2026, FreshnessVerified: false);
            memory.Records[pair.Key] = pair.Value with { RatingsJson = JsonSerializer.Serialize(seed), RatingsFetchedUtc = clock.GetUtcNow().AddHours(-2) };
        }
        var rejected = CreateService(rejectedTmdb, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, default,
            forceRefresh: true, filters: new CalendarFilters { ShowSeries = false, MovieFilters = { RuntimeMinMinutes = 45 } });
        Task<IReadOnlyList<PremiereItem>>? eligible = null;
        string? repairedJson = null;
        var repairedClock = clock.GetUtcNow().AddHours(-1);
        try
        {
            await rejectedEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            eligible = EligibleService().GetPremieresAsync(date, date, default, forceRefresh: true,
                progress: new RecordingProgress<PremiereLoadProgress>(reports.Add), filters: eligibleFilters);
            await eligibleEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            releaseRejected.TrySetResult();
            Assert.Empty(await rejected.WaitAsync(TimeSpan.FromSeconds(3)));
            store.BeforeWrite = (writes, _) =>
            {
                if (repairedJson is null && Assert.Single(writes).ExpectedMetadataRevision == (beforeInitial ? 0 : 1))
                {
                    var pair = Assert.Single(memory.Records);
                    repairedJson = baseline == "clock" ? pair.Value.RatingsJson! : JsonSerializer.Serialize(new ItemEnrichmentRatings(repairedScore, 9900, 77, 78, 79,
                        "tt0000901", "Q901", "Cached movie", 2026, FreshnessVerified: false));
                    if (baseline == "clock") repairedClock = pair.Value.RatingsFetchedUtc!.Value.AddMinutes(1);
                    memory.Records[pair.Key] = pair.Value with { RatingsJson = repairedJson, RatingsFetchedUtc = repairedClock };
                }
                return Task.CompletedTask;
            };
            releaseEligible.TrySetResult();
            var items = await eligible.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(repairedJson);
            if (scoreFilter == "winner-fails") Assert.Empty(items);
            else
            {
                var result = Assert.Single(items);
                Assert.Equal(repairedScore, result.ImdbScore);
                Assert.Equal(77, result.RottenTomatoesScore);
                Assert.Equal(79, result.MetacriticScore);
            }
            var streamed = Assert.Single(reports, report => report.IsFinal).Items;
            Assert.Equal(items.Select(item => item.ImdbScore), streamed.Select(item => item.ImdbScore));
            var cached = cache.SetCalls.Last().Items;
            Assert.Equal(items.Select(item => item.ImdbScore), cached.Select(item => item.ImdbScore));
            var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(memory.Records.Values).MetadataJson!)!;
            Assert.False(metadata.CoreMetadataOnly);
            Assert.True(metadata.ArtworkComplete);
            Assert.True(metadata.AvailabilityOwnedByWatchmode);
            Assert.Equal(repairedJson, Assert.Single(memory.Records.Values).RatingsJson);
            Assert.Equal(repairedClock, Assert.Single(memory.Records.Values).RatingsFetchedUtc);
            Assert.Single(eligibleTmdb.MovieDetailCalls);
            Assert.Single(omdb.Calls);
            Assert.Equal(1, tomatoes.Calls);
            Assert.Equal(1, artwork.Calls);
            Assert.Single(watchmode.SourceCalls);

            var following = await EligibleService(useCache: false).GetPremieresAsync(date, date, default, filters: eligibleFilters);
            if (scoreFilter == "winner-passes") Assert.Empty(following);
            else Assert.Equal(proposedScore, Assert.Single(following).ImdbScore);
            Assert.Equal(2, omdb.Calls.Count);
            Assert.Equal(2, tomatoes.Calls);
            Assert.Equal(1, artwork.Calls);
            Assert.Single(watchmode.SourceCalls);
        }
        finally
        {
            releaseRejected.TrySetResult();
            releaseEligible.TrySetResult();
            await rejected.WaitAsync(TimeSpan.FromSeconds(5));
            if (eligible is not null) await eligible.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("winner-passes")]
    [InlineData("winner-fails")]
    public async Task OrdinaryMetadataWithoutRatingsPreservesConcurrentRepairThroughForceAllStages(string scoreFilter)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var memory = new MemoryItemEnrichmentStore(clock);
        var store = new CoreContinuationStore(memory);
        var coordinator = new ItemEnrichmentCacheCoordinator(store,
            Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions()), NullLogger<ItemEnrichmentCacheCoordinator>.Instance,
            timeProvider: clock);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 901);
        var cachedScore = scoreFilter == "winner-fails" ? 9.0 : 7.0;
        var repairedScore = scoreFilter == "winner-fails" ? 7.0 : 9.0;
        var fetched = clock.GetUtcNow().AddHours(-2);
        var cachedRatings = new ItemEnrichmentRatings(cachedScore, 7000, 70, 71, 72,
            "tt0000901", "Q901", "Cached movie", 2026);
        var cachedMetadata = new ItemEnrichmentMetadata { Title = "Cached movie", ImdbId = "tt0000901", WikidataId = "Q901",
            OriginalLanguage = "en", RuntimeMinutes = 112, CoreMetadataOnly = false,
            PosterUrl = "https://fixture.invalid/cached.jpg", ImageSource = ArtworkSources.Fanart,
            ArtworkComplete = true, OptionalMetadataComplete = true, AvailabilityComplete = true };
        await memory.ReadManyAsync(coordinator.Partition, [identity], true, default);
        await memory.WriteManyAsync(coordinator.Partition,
            [new(identity, 0, JsonSerializer.Serialize(cachedMetadata), fetched, JsonSerializer.Serialize(cachedRatings), fetched,
                ExpectedMetadataRevision: 0)], default);
        var initial = Assert.Single(memory.Records.Values);
        Assert.Equal(1, initial.MetadataRevision);
        Assert.False(cachedMetadata.CoreMetadataOnly);
        var repairedRatings = cachedRatings with { ImdbScore = repairedScore, FreshnessVerified = false };
        var repairedJson = JsonSerializer.Serialize(repairedRatings);
        var repairedClock = fetched.AddMinutes(1);
        var repairs = 0;
        store.BeforeWrite = (writes, _) =>
        {
            var write = Assert.Single(writes);
            if (repairs == 0 && write.MetadataJson is not null)
            {
                // No concurrent metadata writer or revision advancement. Both
                // failed providers produced no ratings payload for this refresh.
                Assert.Equal(initial.MetadataRevision, write.ExpectedMetadataRevision);
                Assert.Null(write.RatingsJson);
                Assert.Null(write.ExpectedRatingsBaseline);
                Assert.Null(write.CoreContinuation);
                var pair = Assert.Single(memory.Records);
                memory.Records[pair.Key] = pair.Value with { RatingsJson = repairedJson, RatingsFetchedUtc = repairedClock };
                Assert.Equal(initial.MetadataRevision, memory.Records[pair.Key].MetadataRevision);
                repairs++;
            }
            return Task.CompletedTask;
        };
        var omdb = new FakeOmdbClient { ThrowOnGet = true };
        var tomatoes = new ComponentTomatoesClient { Successful = false };
        var artwork = new OptionalOwnershipArtwork { Candidate = new("https://fixture.invalid/fresh.jpg", ArtworkSources.Fanart) };
        var watchmode = new FakeWatchmodeClient { Sources = [new() { Id = 19, Name = "Available", Kind = "flatrate" }] };
        var backfill = new ScoreBackfillService(null, omdb, new RatingMapper(), tomatoes,
            NullLogger<ScoreBackfillService>.Instance);
        var tmdb = ItemCacheMovie(date);
        var cache = new FakeCalendarCache();
        var reports = new List<PremiereLoadProgress>();
        var filters = new CalendarFilters { ShowSeries = false, ScoreSource = ScoreSource.Imdb,
            MinScore = scoreFilter == "none" ? 0 : 8, IncludeUnknownScores = false,
            MovieFilters = { OriginalLanguages = ["en"] } };
        var service = CreateService(tmdb, omdb: omdb, rottenTomatoes: tomatoes, artworkProviders: [artwork],
            watchmode: watchmode, scoreBackfill: backfill, calendarCache: cache, itemEnrichmentCache: coordinator);
        var items = await service.GetPremieresAsync(date, date, default, forceRefresh: true, filters: filters,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add));

        Assert.Equal(1, repairs);
        if (scoreFilter == "winner-fails") Assert.Empty(items);
        else Assert.Equal(repairedScore, Assert.Single(items).ImdbScore);
        Assert.Equal(items.Select(item => item.ImdbScore), Assert.Single(reports, report => report.IsFinal).Items.Select(item => item.ImdbScore));
        Assert.Equal(items.Select(item => item.ImdbScore), cache.SetCalls.Last().Items.Select(item => item.ImdbScore));
        var accepted = Assert.Single(memory.Records.Values);
        Assert.Equal(repairedJson, accepted.RatingsJson);
        Assert.Equal(repairedClock, accepted.RatingsFetchedUtc);
        Assert.Equal(initial.MetadataRevision + 1, accepted.MetadataRevision);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(accepted.MetadataJson!)!;
        Assert.False(metadata.CoreMetadataOnly);
        Assert.Equal("https://fixture.invalid/fresh.jpg", metadata.PosterUrl);
        Assert.True(metadata.AvailabilityOwnedByWatchmode);
        Assert.Single(store.Attempts); // Ordinary write only: no core preflight or continuation retry.
        Assert.Single(tmdb.MovieDetailCalls);
        Assert.Single(omdb.Calls);
        Assert.Equal(1, tomatoes.Calls);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("winner-passes")]
    [InlineData("winner-fails")]
    public async Task PersistedCoreOnlyFailedRefreshPreservesObservedRatingsWinner(string scoreFilter)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var memory = new MemoryItemEnrichmentStore(clock);
        var store = new CoreContinuationStore(memory);
        var coordinator = new ItemEnrichmentCacheCoordinator(store,
            Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions()), NullLogger<ItemEnrichmentCacheCoordinator>.Instance,
            timeProvider: clock);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 901);
        var fetched = clock.GetUtcNow().AddHours(-2);
        var cachedScore = scoreFilter == "winner-fails" ? 9.0 : 7.0;
        var repairedScore = scoreFilter == "winner-fails" ? 7.0 : 9.0;
        var cachedRatings = new ItemEnrichmentRatings(cachedScore, 7000, 70, 71, 72,
            "tt0000901", "Q901", "Cached movie", 2026);
        var core = new ItemEnrichmentMetadata { Title = "Cached movie", ImdbId = "tt0000901", WikidataId = "Q901",
            OriginalLanguage = "en", RuntimeMinutes = 20, Overview = "Stored core fallback", CoreMetadataOnly = true,
            ArtworkComplete = false, OptionalMetadataComplete = false, AvailabilityComplete = false,
            TmdbVotes = new(6.0, 6000, fetched) };
        await memory.ReadManyAsync(coordinator.Partition, [identity], true, default);
        await memory.WriteManyAsync(coordinator.Partition,
            [new(identity, 0, JsonSerializer.Serialize(core), fetched, JsonSerializer.Serialize(cachedRatings), fetched,
                TmdbObservedUtc: fetched, ExpectedMetadataRevision: 0)], default);
        var repairedJson = JsonSerializer.Serialize(cachedRatings with { ImdbScore = repairedScore, FreshnessVerified = false });
        var repairedClock = fetched.AddMinutes(1);
        var repairs = 0;
        store.BeforeWrite = (writes, _) =>
        {
            var write = Assert.Single(writes);
            if (repairs == 0)
            {
                Assert.Equal(1, write.ExpectedMetadataRevision);
                Assert.True(JsonSerializer.Deserialize<ItemEnrichmentMetadata>(write.MetadataJson!)!.CoreMetadataOnly);
                Assert.Null(write.RatingsJson);
                var pair = Assert.Single(memory.Records);
                memory.Records[pair.Key] = pair.Value with { RatingsJson = repairedJson, RatingsFetchedUtc = repairedClock };
                repairs++;
            }
            return Task.CompletedTask;
        };
        var template = ItemCacheMovie(date);
        var discovery = Assert.Single(template.MovieItems) with { VoteAverage = 8.2, VoteCount = 8200 };
        var tmdb = new FakeTmdbClient { MovieItems = [discovery],
            MovieDetailOverride = (_, _) => Task.FromResult<TmdbDetailsWithExtras?>(null) };
        var omdb = new FakeOmdbClient { ThrowOnGet = true };
        var tomatoes = new ComponentTomatoesClient { Successful = false };
        var artwork = new OptionalOwnershipArtwork();
        var watchmode = new FakeWatchmodeClient();
        var backfill = new ScoreBackfillService(null, omdb, new RatingMapper(), tomatoes, NullLogger<ScoreBackfillService>.Instance);
        var cache = new FakeCalendarCache();
        var reports = new List<PremiereLoadProgress>();
        var filters = new CalendarFilters { ShowSeries = false, ScoreSource = ScoreSource.Imdb,
            MinScore = scoreFilter == "none" ? 0 : 8, IncludeUnknownScores = false,
            MovieFilters = { OriginalLanguages = ["en"], RuntimeMaxMinutes = 60 } };
        var items = await CreateService(tmdb, omdb: omdb, rottenTomatoes: tomatoes, artworkProviders: [artwork], watchmode: watchmode,
            scoreBackfill: backfill, calendarCache: cache, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, default,
                forceRefresh: true, filters: filters, progress: new RecordingProgress<PremiereLoadProgress>(reports.Add));

        Assert.Equal(1, repairs);
        if (scoreFilter == "winner-fails") Assert.Empty(items);
        else Assert.Equal(repairedScore, Assert.Single(items).ImdbScore);
        Assert.Equal(items.Select(item => item.ImdbScore), Assert.Single(reports, report => report.IsFinal).Items.Select(item => item.ImdbScore));
        Assert.Equal(items.Select(item => item.ImdbScore), cache.SetCalls.Last().Items.Select(item => item.ImdbScore));
        var accepted = Assert.Single(memory.Records.Values);
        Assert.Equal(repairedJson, accepted.RatingsJson);
        Assert.Equal(repairedClock, accepted.RatingsFetchedUtc);
        Assert.Equal(fetched, accepted.MetadataFetchedUtc); // Failed details cannot renew the stored core proof.
        Assert.Equal(clock.GetUtcNow(), accepted.TmdbObservedUtc);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(accepted.MetadataJson!)!;
        Assert.Equal(20, metadata.RuntimeMinutes);
        Assert.Equal(8.2, metadata.TmdbVotes!.TmdbScore);
        Assert.False(metadata.ArtworkComplete);
        Assert.False(metadata.AvailabilityComplete);
        Assert.Single(tmdb.MovieDetailCalls);
        Assert.Single(omdb.Calls);
        Assert.Equal(1, tomatoes.Calls);
        Assert.Equal(0, artwork.Calls);
        Assert.Empty(watchmode.SourceCalls);
    }

    [Fact]
    public async Task OrdinaryProviderMergeProjectsNewerCompetingRatingsWinner()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var submittedClock = clock.GetUtcNow();
        var oldClock = submittedClock.AddHours(-2);
        var memory = new MemoryItemEnrichmentStore(clock);
        var store = new CoreContinuationStore(memory);
        var coordinator = new ItemEnrichmentCacheCoordinator(store,
            Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions()), NullLogger<ItemEnrichmentCacheCoordinator>.Instance,
            timeProvider: clock);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 901);
        var metadata = new ItemEnrichmentMetadata { Title = "Cached movie", ImdbId = "tt0000901", WikidataId = "Q901",
            OriginalLanguage = "en", RuntimeMinutes = 112, ArtworkComplete = true, OptionalMetadataComplete = true, AvailabilityComplete = true };
        var seed = new ItemEnrichmentRatings(5.0, 5000, 60, 61, 62, "tt0000901", "Q901", metadata.Title, 2026)
        {
            ProviderFreshnessTracked = true, ExternalFetchedUtc = oldClock, TomatoesFetchedUtc = oldClock,
            ExternalValues = new(ImdbScore: 5.0, ImdbVoteCount: 5000, MetacriticScore: 62),
            TomatoesValues = new(RottenTomatoesScore: 60, RottenTomatoesAudienceScore: 61)
        };
        await memory.ReadManyAsync(coordinator.Partition, [identity], true, default);
        await memory.WriteManyAsync(coordinator.Partition,
            [new(identity, 0, JsonSerializer.Serialize(metadata), oldClock, JsonSerializer.Serialize(seed), oldClock,
                ExpectedMetadataRevision: 0)], default);
        DateTimeOffset? competingClock = null;
        store.BeforeWrite = (writes, _) =>
        {
            if (competingClock is null)
            {
                var write = Assert.Single(writes);
                Assert.Equal(1, write.ExpectedMetadataRevision);
                Assert.Null(write.ExpectedRatingsBaseline); // Ordinary provider-clock merge stays independent.
                var submitted = JsonSerializer.Deserialize<ItemEnrichmentRatings>(write.RatingsJson!)!;
                Assert.Equal(7.0, submitted.ImdbScore);
                clock.Advance(TimeSpan.FromMinutes(1));
                competingClock = clock.GetUtcNow();
                var competing = seed with { ImdbScore = 9.0, ImdbVoteCount = 9000, ExternalFetchedUtc = competingClock,
                    ExternalValues = new(ImdbScore: 9.0, ImdbVoteCount: 9000, MetacriticScore: 79) };
                var pair = Assert.Single(memory.Records);
                memory.Records[pair.Key] = pair.Value with { RatingsJson = JsonSerializer.Serialize(competing), RatingsFetchedUtc = competingClock };
            }
            return Task.CompletedTask;
        };
        var omdb = ItemCacheOmdb("7.0");
        var tomatoes = new ComponentTomatoesClient { Result = new(94, 96) };
        var cache = new FakeCalendarCache();
        var reports = new List<PremiereLoadProgress>();
        var items = await CreateService(ItemCacheMovie(date), omdb: omdb, rottenTomatoes: tomatoes, calendarCache: cache,
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, default, forceRefresh: true,
                filters: new CalendarFilters { ShowSeries = false, ScoreSource = ScoreSource.Imdb, MinScore = 8,
                    IncludeUnknownScores = false, MovieFilters = { OriginalLanguages = ["en"] } },
                progress: new RecordingProgress<PremiereLoadProgress>(reports.Add));
        var item = Assert.Single(items);
        Assert.Equal(9.0, item.ImdbScore);
        Assert.Equal(94, item.RottenTomatoesScore);
        Assert.Equal(9.0, Assert.Single(Assert.Single(reports, report => report.IsFinal).Items).ImdbScore);
        Assert.Equal(9.0, Assert.Single(cache.SetCalls.Last().Items).ImdbScore);
        var accepted = Assert.Single(memory.Records.Values);
        var ratings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(accepted.RatingsJson!)!;
        Assert.Equal(9.0, ratings.ExternalValues!.ImdbScore);
        Assert.Equal(competingClock, ratings.ExternalFetchedUtc);
        Assert.Equal(94, ratings.TomatoesValues!.RottenTomatoesScore);
        Assert.Equal(submittedClock, ratings.TomatoesFetchedUtc);
        Assert.Equal(competingClock, accepted.RatingsFetchedUtc);
        Assert.Equal(2, accepted.MetadataRevision);
        Assert.Single(store.Attempts);
        Assert.Single(omdb.Calls);
        Assert.Equal(1, tomatoes.Calls);
    }

    [Fact]
    public async Task PreservedRatingsDropQueuedPayloadAndAllowLateMetadataCompletion()
    {
        await using var fixture = await AtomicRatingsFixture.CreateAsync(fresh: false, "payload");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateRatings = fixture.Session.GetAsync(fixture.Identity, false, async (_, _, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(null, fixture.Ratings with { ImdbScore = 7.0 }, RefreshMetadataClock: false);
        }, default, ratingsOnly: true, refreshVariant: "held-ratings");
        ItemEnrichmentRatingsBaseline? repair = null;
        fixture.Store.BeforeWrite = async (writes, _) =>
        {
            if (Assert.Single(writes).ExpectedMetadataRevision != 0) return;
            await fixture.Session.GetAsync(fixture.Identity, false, (_, _, _, _) =>
                Task.FromResult(new ItemEnrichmentRefresh(null, fixture.Ratings with { ImdbScore = 7.7 }, RefreshMetadataClock: false)),
                default, ratingsOnly: true, refreshVariant: "queued-ratings");
            Assert.True(fixture.Session.HasPendingWrites);
            repair = fixture.RepairRatings();
        };
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.Session.FlushAsync(default);
        }
        finally { release.TrySetResult(); }
        var protectedRatings = await lateRatings.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(9.9, protectedRatings.Ratings!.ImdbScore);
        Assert.False(fixture.Session.HasPendingWrites); // The older queued payload is gone, not merely hidden.
        Assert.Equal(repair!.Json, fixture.Current.RatingsJson);
        fixture.Store.BeforeWrite = null;
        var completed = await fixture.Session.GetAsync(fixture.Identity, false, (_, _, _, _) =>
            Task.FromResult(new ItemEnrichmentRefresh(fixture.Complete, null, RefreshMetadataClock: false, RefreshRatingsClock: false)), default);
        Assert.Equal(0, completed.Record.MetadataRevision); // Producer revision is not rebased to the accepted core's revision1.
        Assert.Equal(fixture.Complete.PosterUrl, completed.Metadata!.PosterUrl);
        await fixture.Session.FlushAsync(default);
        fixture.AssertRepairAndMetadata(repair);
        Assert.Null(Assert.Single(fixture.Store.Attempts.Last()).RatingsJson);
        Assert.Equal(0, Assert.Single(fixture.Store.Attempts.Last()).CoreContinuation!.MetadataRevision);
    }

    [Fact]
    public void RatingsOriginDoesNotConstrainOrdinaryIndependentProviderMerges()
    {
        var now = DateTimeOffset.UtcNow;
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 901);
        var metadata = new ItemEnrichmentMetadata { ImdbId = "tt901", Title = "Independent ratings" };
        var original = new ItemEnrichmentRatings(7.1, 7100, 90, 91, null, "tt901", null, metadata.Title, 2026)
        {
            ProviderFreshnessTracked = true, ExternalFetchedUtc = now, TomatoesFetchedUtc = now,
            ExternalValues = new(ImdbScore: 7.1, ImdbVoteCount: 7100),
            TomatoesValues = new(RottenTomatoesScore: 90, RottenTomatoesAudienceScore: 91)
        };
        var accepted = original with { TomatoesFetchedUtc = now.AddMinutes(1), RottenTomatoesScore = 95,
            TomatoesValues = new(RottenTomatoesScore: 95, RottenTomatoesAudienceScore: 96) };
        var proposed = original with { ExternalFetchedUtc = now.AddMinutes(2), ImdbScore = 8.4,
            ExternalValues = new(ImdbScore: 8.4, ImdbVoteCount: 8400) };
        var current = new ItemEnrichmentRecord(identity, JsonSerializer.Serialize(metadata), JsonSerializer.Serialize(accepted),
            now, now.AddMinutes(1), now, 0, null, null, MetadataRevision: 1);
        var incoming = new ItemEnrichmentWrite(identity, 0, RatingsJson: JsonSerializer.Serialize(proposed),
            RatingsFetchedUtc: now.AddMinutes(2), ExpectedMetadataRevision: 0)
        { RatingsOrigin = new(0, 0, new(JsonSerializer.Serialize(original), now)) };
        var result = ItemEnrichmentRatingsMerger.Merge(current, incoming, out var authorized);
        Assert.True(authorized);
        var merged = JsonSerializer.Deserialize<ItemEnrichmentRatings>(result.RatingsJson!)!;
        Assert.Equal(8.4, merged.ImdbScore);
        Assert.Equal(95, merged.RottenTomatoesScore);
    }

    private sealed class AtomicRatingsFixture : IAsyncDisposable
    {
        public ItemCacheClock Clock { get; } = new();
        public ItemEnrichmentIdentity Identity { get; } = new(PremiereMediaType.Movie, 901);
        public MemoryItemEnrichmentStore Memory { get; private set; } = null!;
        public CoreContinuationStore Store { get; private set; } = null!;
        public ItemEnrichmentCacheCoordinator.ItemEnrichmentSession Session { get; private set; } = null!;
        public ItemEnrichmentMetadata Core { get; private set; } = null!;
        public ItemEnrichmentMetadata Complete { get; private set; } = null!;
        public ItemEnrichmentRatings Ratings { get; private set; } = null!;
        private bool Fresh;
        private string Baseline = "null";
        public int RefreshCalls;
        public ItemEnrichmentRecord Current => Assert.Single(Memory.Records.Values);
        public void Replace(ItemEnrichmentRecord record) => Memory.Records[Assert.Single(Memory.Records.Keys)] = record;

        public static async Task<AtomicRatingsFixture> CreateAsync(bool fresh, string baseline)
        {
            var fixture = new AtomicRatingsFixture { Fresh = fresh, Baseline = baseline };
            fixture.Memory = new(fixture.Clock);
            fixture.Store = new(fixture.Memory);
            var coordinator = new ItemEnrichmentCacheCoordinator(fixture.Store,
                Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
                Microsoft.Extensions.Options.Options.Create(new TmdbOptions()), NullLogger<ItemEnrichmentCacheCoordinator>.Instance,
                timeProvider: fixture.Clock);
            fixture.Core = new() { Title = "Atomic ratings", ImdbId = "tt901", OriginalLanguage = "en", RuntimeMinutes = 90,
                CoreMetadataOnly = true, ArtworkComplete = false, OptionalMetadataComplete = false, AvailabilityComplete = false };
            fixture.Complete = fixture.Core with { CoreMetadataOnly = false, ArtworkComplete = true,
                OptionalMetadataComplete = true, AvailabilityComplete = true, PosterUrl = "https://fixture.invalid/complete.jpg" };
            fixture.Ratings = new(8.4, 8400, 94, 96, null, "tt901", null, fixture.Core.Title, 2026)
            {
                ProviderFreshnessTracked = true, ExternalFetchedUtc = fixture.Clock.GetUtcNow(), TomatoesFetchedUtc = fixture.Clock.GetUtcNow(),
                ExternalValues = new(ImdbScore: 8.4, ImdbVoteCount: 8400),
                TomatoesValues = new(RottenTomatoesScore: 94, RottenTomatoesAudienceScore: 96)
            };
            await fixture.Memory.ReadManyAsync(coordinator.Partition, [fixture.Identity], true, default);
            if (baseline != "null") fixture.Replace(fixture.Current with
            {
                RatingsJson = JsonSerializer.Serialize(fixture.Ratings with { ImdbScore = 6.5, ProviderFreshnessTracked = false, FreshnessVerified = false }),
                RatingsFetchedUtc = fixture.Clock.GetUtcNow().AddHours(-2)
            });
            fixture.Session = await coordinator.CreateSessionAsync(default);
            await fixture.Session.PrepareAsync([fixture.Identity], null, default);
            if (!fresh) await fixture.Session.GetAsync(fixture.Identity, false,
                (_, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(fixture.Core, null, RefreshRatingsClock: false)),
                default, metadataOnly: true);
            return fixture;
        }

        public async Task QueueOptionalAsync(bool noRatingsPayload = false)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var optional = Session.GetAsync(Identity, Fresh, async (_, _, _, token) =>
            {
                RefreshCalls++;
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new(Complete, noRatingsPayload ? null : Ratings, RefreshMetadataClock: Fresh,
                    RatingsFailed: noRatingsPayload, RefreshRatingsClock: !noRatingsPayload,
                    FreshMovieCoreJson: Fresh ? JsonSerializer.Serialize(Core) : null);
            }, default);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (Fresh) await Memory.WriteManyAsync(Assert.Single(Memory.Records.Keys).Partition,
                    [new(Identity, 0, JsonSerializer.Serialize(Core), Clock.GetUtcNow(), ExpectedMetadataRevision: 0)], default);
                else await Session.FlushAsync(default);
            }
            finally { release.TrySetResult(); }
            await optional.WaitAsync(TimeSpan.FromSeconds(3));
        }

        public ItemEnrichmentRatingsBaseline RepairRatings()
        {
            var current = Current;
            var json = Baseline == "clock" ? current.RatingsJson! : JsonSerializer.Serialize(Ratings with
            { ImdbScore = 9.9, ProviderFreshnessTracked = false, FreshnessVerified = false });
            var clock = Baseline == "clock" ? current.RatingsFetchedUtc!.Value.AddMinutes(1)
                : current.RatingsFetchedUtc ?? Clock.GetUtcNow().AddHours(-1);
            Replace(current with { RatingsJson = json, RatingsFetchedUtc = clock });
            return new(json, clock);
        }

        public void AssertRepairAndMetadata(ItemEnrichmentRatingsBaseline repair)
        {
            Assert.Equal(JsonSerializer.Serialize(Complete), Current.MetadataJson);
            Assert.Equal(2, Current.MetadataRevision);
            Assert.Equal(repair.Json, Current.RatingsJson);
            Assert.Equal(repair.FetchedUtc, Current.RatingsFetchedUtc);
            Assert.True(Session.Find(Identity)!.PreserveRatings);
        }

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }
}
