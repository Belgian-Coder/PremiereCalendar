using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task TraktAccessDenialPersistsUsablePartialCoverageAndReusesItAcrossServiceRestart()
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var tmdb = ItemCacheMovie(date);
        var trakt = new DegradedTraktClient();
        var service = CreateService(tmdb, omdb: ItemCacheOmdb("7.1"), calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(trakt)]);

        var first = await DegradedFinalAsync(service, date);
        Assert.Single(first.Items);
        Assert.True(first.HasSourceErrors);
        Assert.True(first.IsReusableDegraded);
        Assert.False(first.FromCache);
        var notice = Assert.Single(first.UnavailableSources);
        Assert.Equal("trakt", notice.Provider);
        Assert.Equal("authentication_denied", notice.Reason);
        Assert.Equal(403, notice.StatusCode);
        Assert.Equal(trakt.RetryAtUtc, notice.RetryAtUtc);
        var discoveryCalls = tmdb.MovieCalls.Count;
        var traktCalls = trakt.Calls;

        var repeated = await DegradedFinalAsync(service, date);
        Assert.True(repeated.FromCache);
        Assert.True(repeated.HasSourceErrors);
        Assert.True(repeated.IsReusableDegraded);
        Assert.Equal(first.UnavailableSources, repeated.UnavailableSources);
        Assert.Equal(discoveryCalls, tmdb.MovieCalls.Count);
        Assert.Equal(traktCalls, trakt.Calls);

        // A fresh cache and service read the persisted notice rather than relying
        // on request-local or provider-memory state.
        var restartedTmdb = ItemCacheMovie(date);
        var restartedTrakt = new DegradedTraktClient();
        var restarted = await DegradedFinalAsync(CreateService(restartedTmdb, calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(restartedTrakt)]), date);
        Assert.True(restarted.FromCache);
        Assert.True(restarted.IsReusableDegraded);
        Assert.Equal(first.UnavailableSources, restarted.UnavailableSources);
        Assert.Empty(restartedTmdb.MovieCalls);
        Assert.Equal(0, restartedTrakt.Calls);
        var snapshot = await files.ReadAsync(date);
        Assert.False(snapshot.IsComplete);
        Assert.True(snapshot.CanReuseDegraded(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ExpiredDegradedDeadlineProbesProvidersAndSuccessfulRefreshClearsPersistedWarning()
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var trakt = new DegradedTraktClient();
        await DegradedFinalAsync(CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            calendarCache: files.CreateCache(), discoveryProviders: [new TraktDiscoveryProvider(trakt)]), date);
        await files.ExpireRecoveryDeadlineAsync();
        trakt.DenyAccess = false;
        var calls = trakt.Calls;
        var tmdb = ItemCacheMovie(date);

        var recovered = await DegradedFinalAsync(CreateService(tmdb, omdb: ItemCacheOmdb("7.1"),
            calendarCache: files.CreateCache(), discoveryProviders: [new TraktDiscoveryProvider(trakt)]), date);

        Assert.False(recovered.HasSourceErrors);
        Assert.False(recovered.IsReusableDegraded);
        Assert.Empty(recovered.UnavailableSources);
        Assert.Null(recovered.RetryAfterUtc);
        Assert.NotEmpty(tmdb.MovieCalls);
        Assert.True(trakt.Calls > calls);
        var snapshot = await files.ReadAsync(date);
        Assert.True(snapshot.IsComplete);
        Assert.Empty(snapshot.UnavailableSources);
        Assert.False(snapshot.CanReuseDegraded(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ExplicitRefreshOfDegradedWeekRefreshesAvailableProviderAndPreservesPriorUnavailableRows()
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var cache = files.CreateCache();
        var prior = new PremiereItem
        {
            CanonicalId = "movie:902", Type = PremiereItemType.MovieFirstRelease, MediaType = PremiereMediaType.Movie,
            TmdbId = 902, Title = "Prior Trakt discovery", PremiereDate = date, ImdbScore = 7.2,
            LastUpdatedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            MergeContributions = [new PremiereMergeContribution { Source = "Trakt", Status = "accepted", TmdbId = 902 }]
        };
        await cache.SetWeekAsync(date, date, DegradedCacheKey(), [prior], CancellationToken.None);
        var trakt = new DegradedTraktClient();
        var tmdb = ItemCacheMovie(date);
        var service = CreateService(tmdb, omdb: ItemCacheOmdb("7.1"), calendarCache: cache,
            discoveryProviders: [new TraktDiscoveryProvider(trakt)]);

        var first = await DegradedFinalAsync(service, date, forceRefresh: true);
        var discoveryCalls = tmdb.MovieCalls.Count;
        var second = await DegradedFinalAsync(service, date, forceRefresh: true);

        Assert.True(first.IsReusableDegraded);
        Assert.True(second.IsReusableDegraded);
        Assert.False(second.FromCache);
        Assert.True(tmdb.MovieCalls.Count > discoveryCalls);
        Assert.All(tmdb.MovieCalls, call => Assert.True(call.ForceRefresh));
        Assert.Contains(second.Items, item => item.TmdbId == 901);
        Assert.Contains(second.Items, item => item.TmdbId == 902 && item.Title == prior.Title);
        Assert.Contains((await files.ReadAsync(date)).Items, item => item.TmdbId == 902);
    }

    [Fact]
    public async Task HistoricallyInvalidatedCompleteRowCannotReplaceFreshValidatedRowInDegradedPublication()
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var original = await DegradedFinalAsync(CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            calendarCache: files.CreateCache(), itemEnrichmentCache: coordinator), date);
        Assert.False(original.HasSourceErrors);
        var originalItem = Assert.Single(original.Items);
        Assert.Null(originalItem.EnrichmentVersion?.MetadataInvalidatedUtc);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);
        var trakt = new DegradedTraktClient();
        var fresh = ItemCacheMovie(date);
        fresh.MovieDetailsById[901] = fresh.MovieDetailsById[901] with { Overview = "Freshly validated after invalidation" };

        var refreshed = await DegradedFinalAsync(CreateService(fresh, omdb: ItemCacheOmdb("7.1"),
            calendarCache: files.CreateCache(), discoveryProviders: [new TraktDiscoveryProvider(trakt)],
            itemEnrichmentCache: coordinator), date, forceRefresh: true);

        Assert.True(refreshed.IsReusableDegraded);
        var refreshedItem = Assert.Single(refreshed.Items);
        Assert.Equal("Freshly validated after invalidation", refreshedItem.Overview);
        Assert.Equal(1, refreshedItem.EnrichmentVersion?.Generation);
        Assert.Equal(clock.GetUtcNow(), refreshedItem.EnrichmentVersion?.MetadataInvalidatedUtc);
        var persisted = Assert.Single((await files.ReadAsync(date)).Items);
        Assert.Equal(refreshedItem.EnrichmentVersion, persisted.EnrichmentVersion);
        var restartedTmdb = ItemCacheMovie(date);
        var calls = trakt.Calls;
        var repeated = await DegradedFinalAsync(CreateService(restartedTmdb, calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(trakt)], itemEnrichmentCache: coordinator), date);
        Assert.True(repeated.FromCache);
        Assert.True(repeated.IsReusableDegraded);
        Assert.Empty(restartedTmdb.MovieCalls);
        Assert.Equal(calls, trakt.Calls);
    }

    [Fact]
    public async Task ForcedZeroFreshRowsKeepsPriorRowsAndPersistsReusableCoverageWarning()
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var prior = ItemCacheLegacyItem(date, new ItemCacheClock());
        await files.CreateCache().SetWeekAsync(date, date, DegradedCacheKey(), [prior], CancellationToken.None);
        var trakt = new DegradedTraktClient();
        var emptyTmdb = new FakeTmdbClient();

        var forced = await DegradedFinalAsync(CreateService(emptyTmdb, calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(trakt)]), date, forceRefresh: true);

        Assert.True(forced.HasSourceErrors);
        Assert.True(forced.IsReusableDegraded);
        Assert.Equal(prior.CanonicalId, Assert.Single(forced.Items).CanonicalId);
        Assert.Equal("trakt", Assert.Single(forced.UnavailableSources).Provider);
        Assert.NotEmpty(emptyTmdb.MovieCalls);
        var restartedTmdb = new FakeTmdbClient();
        var calls = trakt.Calls;
        var repeated = await DegradedFinalAsync(CreateService(restartedTmdb, calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(trakt)]), date);
        Assert.True(repeated.FromCache);
        Assert.True(repeated.IsReusableDegraded);
        Assert.Single(repeated.Items);
        Assert.Equal(forced.UnavailableSources, repeated.UnavailableSources);
        Assert.Empty(restartedTmdb.MovieCalls);
        Assert.Equal(calls, trakt.Calls);
    }

    [Fact]
    public async Task EmptyWeekWithOptionalAccessDenialIsReusableAndDoesNotRepeatedlyDiscover()
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var tmdb = new FakeTmdbClient();
        var trakt = new DegradedTraktClient();
        var first = await DegradedFinalAsync(CreateService(tmdb, calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(trakt)]), date);
        Assert.Empty(first.Items);
        Assert.True(first.HasSourceErrors);
        Assert.True(first.IsReusableDegraded);
        Assert.Single(first.UnavailableSources);
        var calls = trakt.Calls;
        var restartedTmdb = new FakeTmdbClient();

        var repeated = await DegradedFinalAsync(CreateService(restartedTmdb, calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(trakt)]), date);

        Assert.True(repeated.FromCache);
        Assert.Empty(repeated.Items);
        Assert.True(repeated.IsReusableDegraded);
        Assert.Equal(first.UnavailableSources, repeated.UnavailableSources);
        Assert.Empty(restartedTmdb.MovieCalls);
        Assert.Equal(calls, trakt.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvenCanonicalDebutOutsideWeekRemovesPriorOccurrenceAndAllowsReusableDegradedWeek(bool sessionValidationOnly)
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var filters = new CalendarFilters { ShowMovies = false };
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var initial = DegradedSeriesTmdb(date, discover: true);
        var complete = await DegradedFinalAsync(CreateService(initial, calendarCache: files.CreateCache(),
            itemEnrichmentCache: coordinator), date, filters: filters);
        Assert.Single(complete.Items);
        Assert.False(complete.HasSourceErrors);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Series, [901], clock.GetUtcNow(), CancellationToken.None);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        if (!sessionValidationOnly)
        {
            var accepting = await coordinator.CreateSessionAsync(CancellationToken.None);
            await accepting.PrepareAsync([identity], null, CancellationToken.None);
            await accepting.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(
                previous.Metadata! with { FirstEpisodeDateChecked = true, FirstEpisodeDate = date.AddYears(-3) }, null)), CancellationToken.None);
            await accepting.FlushAsync(CancellationToken.None);
        }
        var fresh = DegradedSeriesTmdb(date, discover: sessionValidationOnly);
        fresh.SeasonOneEpisodeOneDatesById[901] = date.AddYears(-3);
        var trakt = new DegradedTraktClient();

        var degraded = await DegradedFinalAsync(CreateService(fresh, calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(trakt)], itemEnrichmentCache: coordinator), date,
            forceRefresh: true, filters: filters);

        Assert.Empty(degraded.Items);
        Assert.True(degraded.HasSourceErrors);
        Assert.True(degraded.IsReusableDegraded);
        Assert.DoesNotContain("Item cache validation", degraded.FailedSourceNames);
        Assert.Empty((await files.ReadAsync(date, filters)).Items);
        if (sessionValidationOnly)
        {
            Assert.Single(fresh.TvSeasonDetailCalls);
            Assert.Empty(fresh.TvDetailCalls);
            var persistedMetadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
            Assert.Equal(date, persistedMetadata.FirstEpisodeDate);
        }
        var restarted = DegradedSeriesTmdb(date, discover: true);
        var calls = trakt.Calls;
        var repeated = await DegradedFinalAsync(CreateService(restarted, calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(trakt)], itemEnrichmentCache: coordinator), date, filters: filters);
        Assert.True(repeated.FromCache);
        Assert.True(repeated.IsReusableDegraded);
        Assert.Empty(repeated.Items);
        Assert.Empty(restarted.TvCalls);
        Assert.Equal(calls, trakt.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryPartialPublicationDoesNotRestoreCanonicalOccurrenceRemovedByFinalValidation(bool keepOtherRow)
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var filters = new CalendarFilters { ShowMovies = false };
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        FakeTmdbClient Tmdb(bool includeOld)
        {
            var items = new List<TmdbTvDiscoverItem>();
            if (includeOld) items.Add(new() { Id = 901, Name = "Old occurrence", FirstAirDate = date.ToString("yyyy-MM-dd") });
            if (keepOtherRow) items.Add(new() { Id = 902, Name = "Retained occurrence", FirstAirDate = date.ToString("yyyy-MM-dd") });
            var client = new FakeTmdbClient { TvItems = items };
            foreach (var item in items)
            {
                client.SeasonOneEpisodeOneDatesById[item.Id] = date;
                client.TvDetailsById[item.Id] = new TmdbDetailsWithExtras { Id = item.Id, EpisodeRunTime = [47] };
            }
            return client;
        }
        await DegradedFinalAsync(CreateService(Tmdb(includeOld: true), calendarCache: files.CreateCache(),
            itemEnrichmentCache: coordinator), date, filters: filters);
        clock.Advance(TimeSpan.FromMinutes(1));
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var accepting = await coordinator.CreateSessionAsync(CancellationToken.None);
        await accepting.PrepareAsync([identity], null, CancellationToken.None);
        await accepting.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(previous.Metadata! with
        {
            FirstEpisodeDateChecked = true, FirstEpisodeDate = date.AddYears(-3)
        }, null)), CancellationToken.None);
        await accepting.FlushAsync(CancellationToken.None);
        var failure = new DegradedThrowingProvider("Other source", new ProviderRequestException("other",
            new ProviderFailure("rate_limited", 429, DateTimeOffset.UtcNow.AddMinutes(15))));

        var partial = await DegradedFinalAsync(CreateService(Tmdb(includeOld: false), calendarCache: files.CreateCache(),
            discoveryProviders: [failure], itemEnrichmentCache: coordinator), date, forceRefresh: true, filters: filters);

        Assert.True(partial.HasSourceErrors);
        Assert.False(partial.IsReusableDegraded);
        Assert.DoesNotContain(partial.Items, item => item.TmdbId == 901);
        Assert.Equal(keepOtherRow ? 1 : 0, partial.Items.Count);
        var snapshot = await files.ReadAsync(date, filters);
        Assert.Equal(CalendarCacheCompleteness.Partial, snapshot.Metadata.Completeness);
        Assert.Empty(snapshot.UnavailableSources);
        Assert.False(snapshot.CanReuseDegraded(DateTimeOffset.UtcNow));
        Assert.DoesNotContain(snapshot.Items, item => item.TmdbId == 901);
        Assert.Equal(keepOtherRow ? 1 : 0, snapshot.Items.Count);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("null")]
    [InlineData("not_started")]
    [InlineData("pending")]
    [InlineData("old_generation")]
    [InlineData("episode")]
    [InlineData("in_week")]
    [InlineData("unverified")]
    [InlineData("external_date")]
    [InlineData("first_air_date")]
    public async Task CanonicalWindowPruningPreservesOccurrencesWithoutApplicableOutsideDateProof(string proof)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var original = CanonicalSeriesItem(date, clock) with
        {
            DateSemantics = new(date, PremiereDateSourceKind.TmdbSeasonOneEpisodeOne, PremiereDataConfidence.High, "Fixture")
        };
        await coordinator.ImportAsync([original], CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Series, [901], clock.GetUtcNow(), CancellationToken.None);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var session = await coordinator.CreateSessionAsync(CancellationToken.None);
        await session.PrepareAsync([identity], null, CancellationToken.None);
        var generation = session.Find(identity)!.Record.Generation;
        var validation = proof switch
        {
            "failed" => new FirstEpisodeValidation(date.AddYears(-3), false),
            "null" => new FirstEpisodeValidation(null, true),
            "in_week" => new FirstEpisodeValidation(date.AddDays(1), true),
            _ => new FirstEpisodeValidation(date.AddYears(-3), true)
        };
        validation = validation with
        {
            BaselineMetadataRevision = session.Find(identity)!.Record.MetadataRevision,
            BaselineMetadataFetchedUtc = session.Find(identity)!.Record.MetadataFetchedUtc
        };
        var pending = new TaskCompletionSource<FirstEpisodeValidation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lazy = new Lazy<Task<FirstEpisodeValidation>>(() => proof == "pending" ? pending.Task : Task.FromResult(validation));
        session.FirstEpisodeDates[(901, proof == "old_generation" ? generation - 1 : generation)] = lazy;
        if (proof != "not_started") _ = lazy.Value;
        if (proof == "episode") original = original with { Type = PremiereItemType.SeriesEpisode, SeasonNumber = 1, EpisodeNumber = 1 };
        if (proof == "unverified") original = original with { VerificationState = PremiereVerificationState.Unverified };
        if (proof is "external_date" or "first_air_date") original = original with
        {
            DateSemantics = new(date, proof == "external_date" ? PremiereDateSourceKind.ExternalProviderDate
                : PremiereDateSourceKind.TmdbFirstAirDate, PremiereDataConfidence.Medium, "Fixture")
        };

        var result = await session.ReconcileAsync([original], true, CancellationToken.None, date, date.AddDays(6));

        Assert.Equal(original.CanonicalId, Assert.Single(result.Items).CanonicalId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalOutsideWindowProofDoesNotClearConcurrentOrStickyInvalidation(bool sticky)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var original = CanonicalSeriesItem(date, clock) with
        {
            DateSemantics = new(date, PremiereDateSourceKind.TmdbSeasonOneEpisodeOne, PremiereDataConfidence.High, "Fixture")
        };
        await coordinator.ImportAsync([original], CancellationToken.None);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var session = await coordinator.CreateSessionAsync(CancellationToken.None);
        await session.PrepareAsync([identity], null, CancellationToken.None);
        var generation = session.Find(identity)!.Record.Generation;
        if (sticky) session.MarkDiscoveryUnverified(PremiereMediaType.Series);
        else
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await store.InvalidateTmdbAsync(PremiereMediaType.Series, [901], clock.GetUtcNow(), CancellationToken.None);
            generation++;
        }
        var baseline = session.Find(identity)!.Record;
        var proof = new Lazy<Task<FirstEpisodeValidation>>(() => Task.FromResult(new FirstEpisodeValidation(date.AddYears(-3), true)
        {
            BaselineMetadataRevision = baseline.MetadataRevision, BaselineMetadataFetchedUtc = baseline.MetadataFetchedUtc
        }));
        _ = proof.Value;
        session.FirstEpisodeDates[(901, generation)] = proof;

        var result = await session.ReconcileAsync([original], true, CancellationToken.None, date, date.AddDays(6));

        Assert.Empty(result.Items);
        Assert.Contains(PremiereMediaType.Series, result.DirtyMedia);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewerAcceptedCanonicalMetadataWinsOverEarlierSameGenerationSessionProof(bool refreshCoreClock)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var original = CanonicalSeriesItem(date, clock) with
        {
            DateSemantics = new(date, PremiereDateSourceKind.TmdbSeasonOneEpisodeOne, PremiereDataConfidence.High, "Fixture")
        };
        await coordinator.ImportAsync([original], CancellationToken.None);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var first = await coordinator.CreateSessionAsync(CancellationToken.None);
        await first.PrepareAsync([identity], null, CancellationToken.None);
        var baseline = first.Find(identity)!.Record;
        var proof = new Lazy<Task<FirstEpisodeValidation>>(() => Task.FromResult(new FirstEpisodeValidation(date.AddYears(-3), true)
        {
            BaselineMetadataRevision = baseline.MetadataRevision, BaselineMetadataFetchedUtc = baseline.MetadataFetchedUtc
        }));
        _ = proof.Value;
        first.FirstEpisodeDates[(901, first.Find(identity)!.Record.Generation)] = proof;
        clock.Advance(TimeSpan.FromMinutes(1));
        var later = await coordinator.CreateSessionAsync(CancellationToken.None);
        await later.PrepareAsync([identity], null, CancellationToken.None);
        await later.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(previous.Metadata! with
        {
            Title = "Newer accepted canonical metadata", FirstEpisodeDateChecked = true, FirstEpisodeDate = date
        }, null, RefreshMetadataClock: refreshCoreClock)), CancellationToken.None);
        await later.FlushAsync(CancellationToken.None);

        var reconciled = await first.ReconcileAsync([original], true, CancellationToken.None, date, date.AddDays(6));

        Assert.Equal("Newer accepted canonical metadata", Assert.Single(reconciled.Items).Title);
        Assert.Empty(reconciled.DirtyMedia);
    }

    [Fact]
    public async Task OwnVoteRevisionPreservesCurrentGenerationCanonicalOutsideWindowProof()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var original = CanonicalSeriesItem(date, clock) with
        {
            DateSemantics = new(date, PremiereDateSourceKind.TmdbSeasonOneEpisodeOne, PremiereDataConfidence.High, "Fixture")
        };
        await coordinator.ImportAsync([original], CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Series, [901], clock.GetUtcNow(), CancellationToken.None);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var session = await coordinator.CreateSessionAsync(CancellationToken.None);
        await session.PrepareAsync([identity], null, CancellationToken.None);
        var baseline = session.Find(identity)!.Record;
        var proof = new Lazy<Task<FirstEpisodeValidation>>(() => Task.FromResult(new FirstEpisodeValidation(date.AddYears(-3), true)
        {
            BaselineMetadataRevision = baseline.MetadataRevision, BaselineMetadataFetchedUtc = baseline.MetadataFetchedUtc
        }));
        _ = proof.Value;
        session.FirstEpisodeDates[(901, baseline.Generation)] = proof;
        await session.GetAsync(identity, false, (_, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null, null)),
            CancellationToken.None, ratingsInputs: original with { TmdbScore = 9, TmdbVoteCount = 200 });
        await session.FlushAsync(CancellationToken.None);
        Assert.True(session.Find(identity)!.Record.MetadataRevision > baseline.MetadataRevision);
        Assert.Equal(baseline.MetadataFetchedUtc, session.Find(identity)!.Record.MetadataFetchedUtc);

        var result = await session.ReconcileAsync([original], true, CancellationToken.None, date, date.AddDays(6));

        Assert.Empty(result.Items);
        Assert.Empty(result.DirtyMedia);
        Assert.Same(proof, Assert.Single(session.FirstEpisodeDates).Value);
    }

    [Fact]
    public async Task ForeignRevisionWithoutFreshCanonicalProofPreservesPriorOccurrenceAsDirty()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var original = CanonicalSeriesItem(date, clock) with
        {
            DateSemantics = new(date, PremiereDateSourceKind.TmdbSeasonOneEpisodeOne, PremiereDataConfidence.High, "Fixture")
        };
        await coordinator.ImportAsync([original], CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Series, [901], clock.GetUtcNow(), CancellationToken.None);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var first = await coordinator.CreateSessionAsync(CancellationToken.None);
        await first.PrepareAsync([identity], null, CancellationToken.None);
        var baseline = first.Find(identity)!.Record;
        var proof = new Lazy<Task<FirstEpisodeValidation>>(() => Task.FromResult(new FirstEpisodeValidation(date.AddYears(-3), true)
        {
            BaselineMetadataRevision = baseline.MetadataRevision, BaselineMetadataFetchedUtc = baseline.MetadataFetchedUtc
        }));
        _ = proof.Value;
        first.FirstEpisodeDates[(901, baseline.Generation)] = proof;
        var foreign = await coordinator.CreateSessionAsync(CancellationToken.None);
        await foreign.PrepareAsync([identity], null, CancellationToken.None);
        await foreign.GetAsync(identity, false, (_, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null, null)),
            CancellationToken.None, ratingsInputs: original with { TmdbScore = 9, TmdbVoteCount = 200 });
        await foreign.FlushAsync(CancellationToken.None);

        var result = await first.ReconcileAsync([original], true, CancellationToken.None, date, date.AddDays(6));

        Assert.Single(result.Items);
        Assert.Contains(PremiereMediaType.Series, result.DirtyMedia);
    }

    [Fact]
    public async Task UnrelatedDiscoveryFailurePreventsReusableDegradedCoverage()
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var trakt = new DegradedTraktClient();
        var other = new DegradedThrowingProvider("Other source", new ExternalApiException("Fixture outage"));
        var tmdb = ItemCacheMovie(date);
        var service = CreateService(tmdb, omdb: ItemCacheOmdb("7.1"), calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(trakt), other]);

        var first = await DegradedFinalAsync(service, date);
        Assert.True(first.HasSourceErrors);
        Assert.False(first.IsReusableDegraded);
        var snapshot = await files.ReadAsync(date);
        Assert.False(snapshot.CanReuseDegraded(DateTimeOffset.UtcNow));
        Assert.Empty(snapshot.UnavailableSources);
        var calls = tmdb.MovieCalls.Count;
        await DegradedFinalAsync(service, date);
        Assert.True(tmdb.MovieCalls.Count > calls);
        Assert.Equal(2, other.Calls);
    }

    [Theory]
    [InlineData("Trakt", 500)]
    [InlineData("Other source", 403)]
    public async Task OnlyOptionalAccessDenialsQualifyForReusableDegradedCoverage(string name, int status)
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var provider = new DegradedThrowingProvider(name,
            new ProviderRequestException(name, new ProviderFailure("authentication_denied", status, DateTimeOffset.UtcNow.AddMinutes(15))));

        var final = await DegradedFinalAsync(CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            calendarCache: files.CreateCache(), discoveryProviders: [provider]), date);

        Assert.True(final.HasSourceErrors);
        Assert.False(final.IsReusableDegraded);
        Assert.Empty(final.UnavailableSources);
        Assert.False((await files.ReadAsync(date)).CanReuseDegraded(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ItemGenerationInvalidationPreventsReusableDegradedCoverageDespiteOptionalDenial()
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var trakt = new DegradedTraktClient();
        var tmdb = ItemCacheMovie(date);
        var service = CreateService(tmdb, omdb: ItemCacheOmdb("7.1"), calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(trakt)], itemEnrichmentCache: coordinator);
        PremiereLoadProgress? final = null;
        var invalidated = false;

        await foreach (var update in service.StreamCoreAsync(date, date, filters: DegradedFilters()))
        {
            if (!invalidated && !update.IsFinal && update.Items.Any(item => item.RuntimeMinutes == 112))
            {
                invalidated = true;
                clock.Advance(TimeSpan.FromMinutes(1));
                await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);
            }
            if (update.IsFinal) final = update;
        }

        Assert.True(invalidated);
        Assert.NotNull(final);
        Assert.True(final.HasSourceErrors);
        Assert.False(final.IsReusableDegraded);
        Assert.False(final.IsDegradedResult);
        Assert.Null(final.RetryAfterUtc);
        Assert.Contains("Item cache validation", final.FailedSourceNames);
        Assert.False((await files.ReadAsync(date)).CanReuseDegraded(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task LegacyCacheAdapterDoesNotClaimDegradedCoverageWasPersisted()
    {
        var date = new DateOnly(2026, 11, 2);
        var cache = new FakeCalendarCache();
        var final = await DegradedFinalAsync(CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            calendarCache: cache, discoveryProviders: [new TraktDiscoveryProvider(new DegradedTraktClient())]), date);
        Assert.True(final.HasSourceErrors);
        Assert.False(final.IsReusableDegraded);
        Assert.NotEmpty(final.UnavailableSources);
        Assert.Empty(cache.SetCalls);
    }

    private static CalendarFilters DegradedFilters() => new() { ShowSeries = false };
    private static string DegradedCacheKey(CalendarFilters? filters = null) => PremiereDiscoveryCriteria.FromFilters(filters ?? DegradedFilters()).CacheKey();
    private static FakeTmdbClient DegradedSeriesTmdb(DateOnly date, bool discover)
    {
        var tmdb = new FakeTmdbClient { TvItems = discover
            ? [new TmdbTvDiscoverItem { Id = 901, Name = "Canonical fixture", FirstAirDate = date.ToString("yyyy-MM-dd") }] : [] };
        tmdb.TvDetailsById[901] = new TmdbDetailsWithExtras { Id = 901, Runtime = 47 };
        tmdb.SeasonOneEpisodeOneDatesById[901] = date;
        return tmdb;
    }
    private static async Task<PremiereLoadProgress> DegradedFinalAsync(PremiereService service, DateOnly date,
        bool forceRefresh = false, CalendarFilters? filters = null)
    {
        PremiereLoadProgress? final = null;
        await foreach (var update in service.StreamCoreAsync(date, date, forceRefresh, filters ?? DegradedFilters()))
            if (update.IsFinal) final = update;
        return Assert.IsType<PremiereLoadProgress>(final);
    }

    private sealed class DegradedTraktClient : ITraktClient
    {
        public DateTimeOffset RetryAtUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(15);
        public bool DenyAccess = true;
        public int Calls;
        private Task<T> Result<T>(T value)
        {
            Interlocked.Increment(ref Calls);
            return DenyAccess ? Task.FromException<T>(new ProviderRequestException("trakt",
                new ProviderFailure("authentication_denied", 403, RetryAtUtc))) : Task.FromResult(value);
        }
        public Task<IReadOnlyList<TraktMovieCalendarItem>> GetMovieCalendarAsync(DateOnly start, DateOnly end,
            CancellationToken cancellationToken, bool forceRefresh = false) => Result<IReadOnlyList<TraktMovieCalendarItem>>([]);
        public Task<IReadOnlyList<TraktShowCalendarItem>> GetNewShowCalendarAsync(DateOnly start, DateOnly end,
            CancellationToken cancellationToken, bool forceRefresh = false) => Result<IReadOnlyList<TraktShowCalendarItem>>([]);
    }

    private sealed class DegradedThrowingProvider(string name, Exception error) : INamedPremiereDiscoveryProvider
    {
        public string DisplayName => name;
        public int Calls;
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromException<IReadOnlyList<ExternalPremiereCandidate>>(error);
        }
    }

    private sealed class DegradedCoverageFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-degraded-service-" + Guid.NewGuid().ToString("N"));
        public FileCalendarCache CreateCache() => new(Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions { Directory = "cache" }),
            new DegradedCoverageEnvironment(_root), NullLogger<FileCalendarCache>.Instance);
        public async Task<PremiereCalendar.Services.CalendarCacheSnapshot> ReadAsync(DateOnly date, CalendarFilters? filters = null)
            => Assert.IsType<PremiereCalendar.Services.CalendarCacheSnapshot>(await CreateCache()
                .GetWeekSnapshotAsync(date, date, DegradedCacheKey(filters), CancellationToken.None));
        public async Task ExpireRecoveryDeadlineAsync()
        {
            foreach (var path in Directory.GetFiles(Path.Combine(_root, "cache"), "*.json"))
            {
                var envelope = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
                foreach (var notice in envelope["unavailableSources"]!.AsArray())
                    notice!["retryAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1);
                await File.WriteAllTextAsync(path, envelope.ToJsonString());
            }
        }
        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class DegradedCoverageEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PremiereCalendar.UnitTests";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
