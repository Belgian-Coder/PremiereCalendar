using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task CachedRottenTomatoesHydrationDeduplicatesAndUsesTwoWorkersInItemOrder()
    {
        var date = new DateOnly(2026, 5, 1);
        var cached = HydrationFixtureItems(date);
        var tomatoes = new DelayedHydrationTomatoes(TimeSpan.FromMilliseconds(200));
        var imdb = new HydrationBatchRatings();
        var tmdb = new FakeTmdbClient();
        var service = CreateService(tmdb, calendarCache: new FakeCalendarCache { Items = cached },
            rottenTomatoes: tomatoes, imdbRatingsStore: imdb);
        var timer = Stopwatch.StartNew();

        var items = await service.GetPremieresAsync(date, date.AddDays(20), CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false, ScoreSource = ScoreSource.RottenTomatoes });

        _evidence.WriteLine($"RT cached hydration: {timer.Elapsed.TotalMilliseconds:F0} ms; " +
            $"{tomatoes.Calls.Count} unique calls; peak {tomatoes.PeakConcurrency}; 200 ms per call.");
        Assert.Equal(cached.Select(item => item.CanonicalId), items.Select(item => item.CanonicalId));
        Assert.Equal(8, tomatoes.Calls.Count);
        Assert.All(tomatoes.Calls.GroupBy(title => title), group => Assert.Single(group));
        Assert.Equal(2, tomatoes.PeakConcurrency);
        Assert.Equal(0, tomatoes.Active);
        Assert.Equal(1, imdb.BatchCount);
        Assert.Equal(0, imdb.SingleCount);
        Assert.Equal(8, imdb.LastBatch.Count);
        Assert.Equal(97, items[0].RottenTomatoesScore);
        Assert.Equal(98, items[1].RottenTomatoesAudienceScore);
        Assert.All(items, item =>
        {
            Assert.NotNull(item.RottenTomatoesScore);
            Assert.NotNull(item.RottenTomatoesAudienceScore);
            Assert.Equal(8.1, item.ImdbScore);
            Assert.Equal(1234, item.ImdbVoteCount);
        });
        Assert.Empty(tmdb.MovieCalls);
    }

    [Fact]
    public async Task CancelingCachedRottenTomatoesHydrationJoinsEveryStartedLookup()
    {
        var date = new DateOnly(2026, 5, 1);
        var tomatoes = new DelayedHydrationTomatoes(Timeout.InfiniteTimeSpan);
        var service = CreateService(new FakeTmdbClient(),
            calendarCache: new FakeCalendarCache { Items = HydrationFixtureItems(date) }, rottenTomatoes: tomatoes);
        using var cancellation = new CancellationTokenSource();
        var load = service.GetPremieresAsync(date, date.AddDays(20), cancellation.Token,
            filters: new CalendarFilters { ShowSeries = false, ScoreSource = ScoreSource.RottenTomatoes });
        await tomatoes.TwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.Equal(2, tomatoes.Calls.Count);
        Assert.Equal(2, tomatoes.Canceled);
        Assert.Equal(0, tomatoes.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableRottenTomatoesCacheHydratesFreshCalendarWithoutHttpOrScheduler(bool hasScores)
    {
        var date = new DateOnly(2026, 5, 1);
        var cached = HydrationFixtureItems(date).Take(1).Select(item => item with { RottenTomatoesScore = null }).ToArray();
        var store = new EnrichmentResponseStateStore();
        var responseCache = new ProviderResponseCache(store, NullLogger<ProviderResponseCache>.Instance);
        using var handler = new EnrichmentHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(hasScores
                ? "<search-page-media-row releaseyear=\"2026\" tomatometer-score=\"89\"><a data-qa=\"info-name\" href=\"/m/cached_hydration\">Cached hydration 00</a></search-page-media-row>" +
                  "<script id=\"media-scorecard-json\">{\"criticsScore\":{\"score\":89},\"audienceScore\":{\"score\":91}}</script>"
                : "<div class=\"js-search-no-results search__no-results\">Sorry, no results found for &quot;Example&quot;</div>")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://tomatoes.example/") };
        using (var warmMemory = new MemoryCache(new MemoryCacheOptions()))
        {
            var warmClient = NewEnrichmentTomatoes(http, warmMemory, responseCache);
            await warmClient.GetScoresAsync(PremiereMediaType.Movie, cached[0].Title, 2026, cached[0].WikidataId,
                CancellationToken.None);
        }
        var requestsBeforeRestart = handler.Calls;
        Assert.NotEmpty(store.States);
        using var coldMemory = new MemoryCache(new MemoryCacheOptions());
        var coldClient = NewEnrichmentTomatoes(http, coldMemory,
            new ProviderResponseCache(store, NullLogger<ProviderResponseCache>.Instance));
        var scheduler = new RecordingWorkScheduler([]);
        var service = CreateService(new FakeTmdbClient(), calendarCache: new FakeCalendarCache { Items = cached },
            rottenTomatoes: coldClient, workScheduler: scheduler);

        var items = await service.GetPremieresAsync(date, date, CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false, ScoreSource = ScoreSource.RottenTomatoes });

        Assert.Equal(requestsBeforeRestart, handler.Calls);
        Assert.Equal(0, scheduler.EnqueueCount);
        Assert.Equal(hasScores ? 89 : null, Assert.Single(items).RottenTomatoesScore);
    }

    [Theory]
    [InlineData(0, 1, 2026)]
    [InlineData(2, 3, 2026)]
    [InlineData(0, 2, 2025)]
    public async Task ForcedRefreshReusesVerifiedProviderAbsenceAcrossExternalMappingAndFinalBackfill(
        int transientFailures, int expectedTomatoesCalls, int releaseYear)
    {
        var date = new DateOnly(2026, 5, 5);
        var tmdb = new FakeTmdbClient();
        tmdb.MovieDetailsById[4400] = new TmdbDetailsWithExtras
        {
            Id = 4400, Title = "Enrichment generation", OriginalTitle = "Original enrichment title", OriginalLanguage = "en",
            Overview = "TMDb overview", Runtime = 110, PosterPath = "/fixture.jpg", BackdropPath = "/fixture-backdrop.jpg",
            ExternalIds = new TmdbExternalIds { ImdbId = "tt0004400", WikidataId = "Q4400" },
            Genres = [new TmdbGenre { Id = 18, Name = "Drama" }],
            Keywords = new TmdbKeywordResponse { Keywords = [new TmdbKeyword { Id = 1, Name = "fixture keyword" }] },
            ProductionCountries = [new TmdbProductionCountry { Iso31661 = "US" }],
            Videos = new TmdbVideoResponse { Results = [new TmdbVideo { Key = "fixture-trailer", Site = "YouTube", Type = "Trailer", Official = true }] }
        };
        var discovery = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(PremiereMediaType.Movie, date, "Enrichment generation", 4400,
                "tt0004400", null, "Fixture feed") { ReleaseYear = releaseYear, OriginalLanguage = "en" }]
        };
        var omdbItem = new OmdbItem { Response = "True", Plot = "OMDb plot", Poster = "https://image.example/fixture.jpg" };
        using var omdbHandler = new EnrichmentHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(omdbItem), Encoding.UTF8, "application/json") });
        using var omdbHttp = new HttpClient(omdbHandler) { BaseAddress = new Uri("https://omdb.example/") };
        using var omdbMemory = new MemoryCache(new MemoryCacheOptions());
        var omdb = new OmdbClient(omdbHttp, omdbMemory,
            Microsoft.Extensions.Options.Options.Create(new OmdbOptions { Enabled = true, ApiKey = "fixture" }));
        using var tomatoesHandler = new EnrichmentHttpHandler((_, call) => new HttpResponseMessage(
            call <= transientFailures ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent("<div class=\"js-search-no-results search__no-results\">Sorry, no results found for &quot;Example&quot;</div>") });
        using var tomatoesHttp = new HttpClient(tomatoesHandler) { BaseAddress = new Uri("https://tomatoes.example/") };
        using var tomatoesMemory = new MemoryCache(new MemoryCacheOptions());
        var tomatoes = NewEnrichmentTomatoes(tomatoesHttp, tomatoesMemory);
        var backfill = new ScoreBackfillService(null, omdb, new RatingMapper(), tomatoes, NullLogger<ScoreBackfillService>.Instance);
        var service = CreateService(tmdb, omdb: omdb, discoveryProviders: [discovery], rottenTomatoes: tomatoes, scoreBackfill: backfill);

        var first = Assert.Single(await service.GetPremieresAsync(date, date, CancellationToken.None, forceRefresh: true,
            filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal(1, omdbHandler.Calls);
        Assert.Equal(expectedTomatoesCalls, tomatoesHandler.Calls);
        Assert.Null(first.RottenTomatoesScore);
        Assert.Null(first.ImdbScore);
        Assert.Contains(first.MergeContributions, contribution => contribution.Source == "TMDb");
        Assert.Contains(first.MergeContributions, contribution => contribution.Source == "Fixture feed");
        Assert.Equal(PremiereDateSourceKind.ExternalProviderDate, first.DateSemantics?.SourceKind);

        var second = Assert.Single(await service.GetPremieresAsync(date, date, CancellationToken.None, forceRefresh: true,
            filters: new CalendarFilters { ShowSeries = false }));
        Assert.Equal(2, omdbHandler.Calls);
        Assert.Equal(expectedTomatoesCalls + (releaseYear == 2026 ? 1 : 2), tomatoesHandler.Calls);
        Assert.Equal(NormalizeEnrichmentItem(first), NormalizeEnrichmentItem(second));

        // Compare the complete normalized projection with independently supplied provider values.
        var fixtureOmdb = new FakeOmdbClient();
        fixtureOmdb.ItemsByImdbId["tt0004400"] = omdbItem;
        var fixtureTomatoes = new FakeRottenTomatoesClient();
        var expectedService = CreateService(tmdb, omdb: fixtureOmdb, discoveryProviders: [discovery], rottenTomatoes: fixtureTomatoes,
            scoreBackfill: new ScoreBackfillService(null, fixtureOmdb, new RatingMapper(), fixtureTomatoes, NullLogger<ScoreBackfillService>.Instance));
        var expected = Assert.Single(await expectedService.GetPremieresAsync(date, date, CancellationToken.None, forceRefresh: true,
            filters: new CalendarFilters { ShowSeries = false }));
        Assert.Equal(NormalizeEnrichmentItem(expected), NormalizeEnrichmentItem(first));
    }

    private static string NormalizeEnrichmentItem(PremiereItem item)
        => JsonSerializer.Serialize(item with { LastUpdatedUtc = default });

    [Fact]
    public async Task FinalIdRepairStillFetchesNewImdbAndWikidataProviderKeys()
    {
        var date = new DateOnly(2026, 5, 5);
        var tmdb = new FakeTmdbClient();
        var discovery = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(PremiereMediaType.Movie, date, "Repaired identity", 4500,
                null, null, "Fixture feed", OriginalLanguage: "en")]
        };
        using var omdbHandler = new EnrichmentHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"Response\":\"True\",\"imdbRating\":\"7.6\"}", Encoding.UTF8, "application/json") });
        using var omdbHttp = new HttpClient(omdbHandler) { BaseAddress = new Uri("https://omdb.example/") };
        using var omdbMemory = new MemoryCache(new MemoryCacheOptions());
        var omdb = new OmdbClient(omdbHttp, omdbMemory,
            Microsoft.Extensions.Options.Options.Create(new OmdbOptions { Enabled = true, ApiKey = "fixture" }));
        using var tomatoesHandler = new EnrichmentHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<div class=\"js-search-no-results search__no-results\">Sorry, no results found for &quot;Example&quot;</div>") });
        using var tomatoesHttp = new HttpClient(tomatoesHandler) { BaseAddress = new Uri("https://tomatoes.example/") };
        using var tomatoesMemory = new MemoryCache(new MemoryCacheOptions());
        var tomatoes = NewEnrichmentTomatoes(tomatoesHttp, tomatoesMemory);
        var service = CreateService(tmdb, omdb: omdb, discoveryProviders: [discovery], rottenTomatoes: tomatoes,
            scoreBackfill: new ScoreBackfillService(null, omdb, new RatingMapper(), tomatoes, NullLogger<ScoreBackfillService>.Instance),
            missingExternalIdRepair: new MissingExternalIdRepairService(tmdb, NullLogger<MissingExternalIdRepairService>.Instance));
        PremiereItem? final = null;

        await foreach (var update in service.StreamCoreAsync(date, date, forceRefresh: true,
            filters: new CalendarFilters { ShowSeries = false }))
        {
            if (update.IsFinal) final = Assert.Single(update.Items);
            else if (update.Items.Count > 0)
            {
                // The initial lookup had no IDs. Simulate their recovery before the final repair stage.
                tmdb.MovieDetailsById[4500] = new TmdbDetailsWithExtras
                    { Id = 4500, ExternalIds = new TmdbExternalIds { ImdbId = "tt0004500", WikidataId = "Q4500" } };
            }
        }

        Assert.NotNull(final);
        Assert.Equal("tt0004500", final.ImdbId);
        Assert.Equal("Q4500", final.WikidataId);
        Assert.Equal(7.6, final.ImdbScore);
        Assert.Equal(1, omdbHandler.Calls);
        Assert.Equal(2, tomatoesHandler.Calls);
    }

    private static PremiereItem[] HydrationFixtureItems(DateOnly date)
    {
        var unique = Enumerable.Range(0, 8).Select(index => new PremiereItem
        {
            CanonicalId = $"movie:{index + 1}", TmdbId = index + 1, MediaType = PremiereMediaType.Movie,
            Type = PremiereItemType.MovieFirstRelease, Title = $"Cached hydration {index:D2}", PremiereDate = date.AddDays(index),
            ImdbId = $"tt{index + 1:D7}", WikidataId = $"Q{index + 1}", OriginalLanguage = "en", RuntimeMinutes = 100,
            RottenTomatoesScore = index == 0 ? 97 : null, RottenTomatoesAudienceScore = index == 1 ? 98 : null
        }).ToArray();
        return unique.Concat(unique.Take(4).Select((item, index) => item with
            { CanonicalId = $"movie:{index + 101}", TmdbId = index + 101, PremiereDate = date.AddDays(index + 8) })).ToArray();
    }

    private static RottenTomatoesClient NewEnrichmentTomatoes(HttpClient http, IMemoryCache memory, ProviderResponseCache? responseCache = null)
        => new(http, memory, Microsoft.Extensions.Options.Options.Create(new RottenTomatoesOptions { Enabled = true, BaseUrl = "https://tomatoes.example/" }),
            NullLogger<RottenTomatoesClient>.Instance, responseCache: responseCache);

    private sealed class DelayedHydrationTomatoes(TimeSpan delay) : IRottenTomatoesClient
    {
        private int _active;
        private int _peak;
        private int _canceled;
        public ConcurrentBag<string> Calls { get; } = [];
        public int Active => Volatile.Read(ref _active);
        public int PeakConcurrency => Volatile.Read(ref _peak);
        public int Canceled => Volatile.Read(ref _canceled);
        public TaskCompletionSource TwoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RottenTomatoesScores> GetScoresAsync(PremiereMediaType mediaType, string title, int? year,
            string? wikidataId, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Calls.Add(title);
            var active = Interlocked.Increment(ref _active);
            int previous;
            while (active > (previous = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, active, previous) != previous) { }
            if (active == 2) TwoStarted.TrySetResult();
            try
            {
                await Task.Delay(delay, cancellationToken);
                return new RottenTomatoesScores(89, 91);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _canceled);
                throw;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class HydrationBatchRatings : IImdbRatingsStore
    {
        public int BatchCount { get; private set; }
        public int SingleCount { get; private set; }
        public IReadOnlyCollection<string> LastBatch { get; private set; } = [];
        public Task<ImdbRatingRecord?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken)
        { SingleCount++; return Task.FromResult<ImdbRatingRecord?>(null); }
        public Task<IReadOnlyDictionary<string, ImdbRatingRecord>> GetByImdbIdsAsync(IReadOnlyCollection<string> imdbIds, CancellationToken cancellationToken)
        {
            BatchCount++;
            LastBatch = imdbIds;
            return Task.FromResult<IReadOnlyDictionary<string, ImdbRatingRecord>>(imdbIds.ToDictionary(id => id,
                id => new ImdbRatingRecord(id, 8.1, 1234, DateTimeOffset.UtcNow)));
        }
        public Task ReplaceAllAsync(IEnumerable<ImdbRatingRecord> ratings, DateTimeOffset importedAtUtc, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ImdbDatasetState> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult(new ImdbDatasetState(null, 8, null));
        public Task SaveStateAsync(ImdbDatasetState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EnrichmentHttpHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request, Interlocked.Increment(ref _calls)));
    }

    private sealed class EnrichmentResponseStateStore : IProviderCacheStateStore
    {
        public ConcurrentDictionary<(string Provider, ProviderCacheScope Scope, string Key), ProviderCacheState> States { get; } = [];
        public Task<ProviderCacheState?> GetAsync(string provider, ProviderCacheScope scope, string key, CancellationToken cancellationToken)
            => Task.FromResult(States.GetValueOrDefault((provider, scope, key)));
        public Task SaveAsync(ProviderCacheState state, CancellationToken cancellationToken)
        { States[(state.Provider, state.Scope, state.Key)] = state; return Task.CompletedTask; }
        public Task<IReadOnlyList<ProviderCacheState>> GetRecentAsync(int take, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderCacheState>>(States.Values.Take(take).ToArray());
        public Task<IReadOnlyList<ProviderCacheState>> GetByProviderAsync(string provider, int take, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderCacheState>>(States.Values.Where(state => state.Provider == provider).Take(take).ToArray());
    }
}
