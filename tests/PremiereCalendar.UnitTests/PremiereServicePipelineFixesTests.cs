using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    private static async Task<List<PremiereLoadProgress>> CollectStreamAsync(PremiereService service, DateOnly start, DateOnly end, CalendarFilters filters)
    {
        var updates = new List<PremiereLoadProgress>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var update in service.StreamPremieresAsync(start, end, false, filters, budget.Token)) updates.Add(update);
        return updates;
    }

    [Fact]
    public async Task TmdbThrottlingDuringExternalIdResolutionFailsTheSourceInsteadOfCachingAnUnverifiedCard()
    {
        var date = new DateOnly(2026, 11, 3);
        var tmdb = new FakeTmdbClient();
        tmdb.FindExceptions[(PremiereMediaType.Series, "imdb_id", "tt0000777")] = new ExternalApiException(
            "TMDb throttled.", new ProviderRetryAfterException("tmdb", DateTimeOffset.UtcNow.AddMinutes(2)));
        var cache = new FakeCalendarCache();
        var feed = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(PremiereMediaType.Series, date, "Throttled show", null, "tt0000777", null, "Fixture feed")]
        };
        var service = CreateService(tmdb, discoveryProviders: [feed], calendarCache: cache);

        var updates = await CollectStreamAsync(service, date, date, new CalendarFilters { ShowMovies = false });

        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.True(final.HasSourceErrors);
        Assert.NotNull(final.RetryAfterUtc);
        Assert.DoesNotContain(final.Items, item => item.VerificationState == PremiereVerificationState.Unverified);
        Assert.Empty(cache.SetCalls);
    }

    [Fact]
    public async Task FailedExternalIdResolutionIsNotMemoisedForTheRestOfTheLoad()
    {
        var date = new DateOnly(2026, 11, 3);
        var clock = new ItemCacheClock();
        var tmdb = new FakeTmdbClient();
        tmdb.FindExceptions[(PremiereMediaType.Series, "imdb_id", "tt0000779")] = new ExternalApiException(
            "TMDb throttled.", new ProviderRetryAfterException("tmdb", DateTimeOffset.UtcNow.AddMinutes(2)));
        var candidate = new ExternalPremiereCandidate(PremiereMediaType.Series, date, "Throttled twice", null, "tt0000779", null, "Fixture feed");
        // The second source asks for the same ID later, after the first lookup has already failed.
        var first = new FakeDiscoveryProvider { DisplayName = "First feed", Candidates = [candidate] };
        var second = new FakeDiscoveryProvider { DisplayName = "Second feed", Candidates = [candidate with { Source = "Second feed" }], Delay = TimeSpan.FromMilliseconds(400) };
        var service = CreateService(tmdb, discoveryProviders: [first, second],
            itemEnrichmentCache: ItemCacheCoordinator(new MemoryItemEnrichmentStore(clock), clock));

        var updates = await CollectStreamAsync(service, date, date, new CalendarFilters { ShowMovies = false });

        Assert.True(Assert.Single(updates, update => update.IsFinal).HasSourceErrors);
        Assert.Equal(2, tmdb.FindCalls.Count(call => call.ExternalId == "tt0000779"));
    }

    [Fact]
    public async Task ThrottledTvmazeScheduleEndsAsAPartialLoadWithTheServerRetryTimeInsteadOfThrowing()
    {
        var date = new DateOnly(2026, 11, 3);
        var cache = new FakeCalendarCache();
        var retryAfter = TimeSpan.FromMinutes(10);
        using var handler = new StatusHandler(System.Net.HttpStatusCode.TooManyRequests, retryAfter);
        using var memory = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var options = Microsoft.Extensions.Options.Options.Create(new PremiereCalendar.Options.TvmazeOptions
            { Enabled = true, EnableScheduleDiscovery = true, ScheduleCountries = ["BE", "US"] });
        var client = new TvmazeClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.tvmaze.com/") }, memory, options);
        var service = CreateService(new FakeTmdbClient(), discoveryProviders: [new TvmazeScheduleDiscoveryProvider(client, options)], calendarCache: cache);
        var before = DateTimeOffset.UtcNow;

        // TVmaze is the only source and returns nothing, yet the load must not throw (which would retry the job at once).
        var updates = await CollectStreamAsync(service, date, date, new CalendarFilters { ShowMovies = false });

        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.True(final.HasSourceErrors);
        Assert.NotNull(final.RetryAfterUtc);
        Assert.True(final.RetryAfterUtc > before.AddMinutes(9), $"Retry deadline {final.RetryAfterUtc} should reflect Retry-After.");
        Assert.Empty(cache.SetCalls);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.NotFound)]
    [InlineData(System.Net.HttpStatusCode.BadRequest)]
    public async Task DeterministicTvmazeRejectionOfADayIsAnEmptyDayNotAFailedSource(System.Net.HttpStatusCode status)
    {
        var date = new DateOnly(2026, 11, 3);
        using var handler = new StatusHandler(status, null);
        using var memory = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var options = Microsoft.Extensions.Options.Options.Create(new PremiereCalendar.Options.TvmazeOptions
            { Enabled = true, EnableScheduleDiscovery = true, ScheduleCountries = ["ZZ"] });
        var client = new TvmazeClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.tvmaze.com/") }, memory, options);

        var candidates = await new TvmazeScheduleDiscoveryProvider(client, options).GetCandidatesAsync(date, date, CancellationToken.None);

        Assert.Empty(candidates);
    }

    private sealed class StatusHandler(System.Net.HttpStatusCode status, TimeSpan? retryAfter) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status);
            if (retryAfter is { } delay) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task OrdinaryUnmatchedExternalIdStillBecomesAnUnverifiedCard()
    {
        var date = new DateOnly(2026, 11, 3);
        var tmdb = new FakeTmdbClient();
        tmdb.FindExceptions[(PremiereMediaType.Series, "imdb_id", "tt0000778")] = new ExternalApiException("TMDb rejected the lookup.");
        var feed = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(PremiereMediaType.Series, date, "Unmatched show", null, "tt0000778", null, "Fixture feed")]
        };
        var service = CreateService(tmdb, discoveryProviders: [feed]);

        var updates = await CollectStreamAsync(service, date, date, new CalendarFilters { ShowMovies = false });

        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.False(final.HasSourceErrors);
        Assert.Contains(final.Items, item => item.Title == "Unmatched show");
    }

    [Fact]
    public async Task ExternalSeriesWithAKnownMismatchingLanguageIsRejectedBeforeOptionalEnrichment()
    {
        var date = new DateOnly(2026, 11, 3);
        var clock = new ItemCacheClock();
        var tmdb = new FakeTmdbClient();
        tmdb.TvDetailsById[951] = new TmdbDetailsWithExtras
        { Id = 951, Name = "Foreign show", OriginalLanguage = "fr", EpisodeRunTime = [45], ExternalIds = new TmdbExternalIds { ImdbId = "tt0000951" } };
        var omdb = new FakeOmdbClient();
        var artwork = new RecordingArtworkProvider();
        var feed = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(PremiereMediaType.Series, date, "Foreign show", 951, "tt0000951", null, "Fixture feed")]
        };
        var service = CreateService(tmdb, omdb: omdb, artworkProviders: [artwork], discoveryProviders: [feed],
            itemEnrichmentCache: ItemCacheCoordinator(new MemoryItemEnrichmentStore(clock), clock));

        var items = await service.GetPremieresAsync(date, date, CancellationToken.None,
            filters: new CalendarFilters { ShowMovies = false, SeriesFilters = { OriginalLanguages = ["en"] } });

        Assert.Empty(items);
        Assert.Empty(omdb.Calls);
        Assert.Empty(artwork.Calls);
    }

    [Theory]
    [InlineData("vls", "nl")]
    [InlineData("en-GB", "en")]
    public async Task ExternalSeriesLanguageIsComparedNormalised(string? detailsLanguage, string requested)
    {
        var date = new DateOnly(2026, 11, 3);
        var clock = new ItemCacheClock();
        var tmdb = new FakeTmdbClient();
        tmdb.TvDetailsById[952] = new TmdbDetailsWithExtras
        { Id = 952, Name = "Matching show", OriginalLanguage = detailsLanguage, EpisodeRunTime = [45], ExternalIds = new TmdbExternalIds { ImdbId = "tt0000952" } };
        var feed = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(PremiereMediaType.Series, date, "Matching show", 952, "tt0000952", null, "Fixture feed")]
        };
        var service = CreateService(tmdb, omdb: ItemCacheOmdb("7.1"), discoveryProviders: [feed],
            itemEnrichmentCache: ItemCacheCoordinator(new MemoryItemEnrichmentStore(clock), clock));

        var items = await service.GetPremieresAsync(date, date, CancellationToken.None,
            filters: new CalendarFilters { ShowMovies = false, SeriesFilters = { OriginalLanguages = [requested] } });

        Assert.Equal(952, Assert.Single(items).TmdbId);
    }

    [Fact]
    public async Task ExternalSeriesWithUnknownLanguageIsStillEnrichedBeforeTheFinalFilterDecides()
    {
        var date = new DateOnly(2026, 11, 3);
        var clock = new ItemCacheClock();
        var tmdb = new FakeTmdbClient();
        tmdb.TvDetailsById[953] = new TmdbDetailsWithExtras
        { Id = 953, Name = "Unknown language", OriginalLanguage = null, EpisodeRunTime = [45], ExternalIds = new TmdbExternalIds { ImdbId = "tt0000953" } };
        var omdb = new FakeOmdbClient();
        omdb.ItemsByImdbId["tt0000953"] = new OmdbItem { Response = "True", ImdbRating = "7.0", ImdbVotes = "1,000" };
        var feed = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(PremiereMediaType.Series, date, "Unknown language", 953, "tt0000953", null, "Fixture feed")]
        };
        var service = CreateService(tmdb, omdb: omdb, discoveryProviders: [feed],
            itemEnrichmentCache: ItemCacheCoordinator(new MemoryItemEnrichmentStore(clock), clock));

        await service.GetPremieresAsync(date, date, CancellationToken.None,
            filters: new CalendarFilters { ShowMovies = false, SeriesFilters = { OriginalLanguages = ["en"] } });

        Assert.Contains("tt0000953", omdb.Calls);
    }

    [Fact]
    public async Task FailedTvmazeScheduleReportsAFailedSourceAndNeverPublishesACompleteWeek()
    {
        var date = new DateOnly(2026, 11, 3);
        var cache = new FakeCalendarCache();
        var schedule = new TvmazeScheduleDiscoveryProvider(
            new FailingScheduleTvmazeClient(),
            Microsoft.Extensions.Options.Options.Create(new PremiereCalendar.Options.TvmazeOptions { Enabled = true, EnableScheduleDiscovery = true, ScheduleCountries = [] }));
        var tmdb = new FakeTmdbClient { TvItems = [new TmdbTvDiscoverItem { Id = 901, Name = "Healthy show", FirstAirDate = date.ToString("yyyy-MM-dd") }] };
        tmdb.SeasonOneEpisodeOneDatesById[901] = date;
        var service = CreateService(tmdb, discoveryProviders: [schedule], calendarCache: cache);

        var updates = await CollectStreamAsync(service, date, date, new CalendarFilters { ShowMovies = false });

        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.True(final.HasSourceErrors);
        Assert.Contains(final.Items, item => item.TmdbId == 901);
        Assert.Empty(cache.SetCalls);
    }

    private sealed class FailingScheduleTvmazeClient : ITvmazeClient
    {
        public Task<TvmazeShow?> LookupShowAsync(int? tvdbId, string? imdbId, CancellationToken cancellationToken, bool forceRefresh = false) => Task.FromResult<TvmazeShow?>(null);
        public Task<TvmazeShow?> SearchShowByNameAsync(string title, CancellationToken cancellationToken, bool forceRefresh = false) => Task.FromResult<TvmazeShow?>(null);
        public Task<IReadOnlyList<TvmazeShowImage>> GetShowImagesAsync(int showId, CancellationToken cancellationToken, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<TvmazeShowImage>>([]);
        public Task<IReadOnlyList<TvmazeScheduleEpisode>> GetScheduleAsync(DateOnly date, string? country, bool webSchedule, CancellationToken cancellationToken, bool forceRefresh = false)
            => throw new ExternalApiException("TVmaze schedule failed.");
        public Task<IReadOnlyList<TvmazeShowUpdate>> GetShowUpdatesAsync(TvmazeUpdateWindow since, CancellationToken cancellationToken, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<TvmazeShowUpdate>>([]);
    }

    [Fact]
    public async Task ReadOnlyReconcileWhoseValidationReadFailsReportsItWasNotValidated()
    {
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 901);
        await using var session = await coordinator.CreateSessionAsync(default);
        await session.PrepareAsync([identity], null, default);
        var failRead = true;
        store.BeforeCurrentRead = _ => { if (failRead) throw new IOException("Transient validation failure."); };

        var failed = await session.ReconcileAsync([], includeConsumed: true, default, readOnly: true);

        // The placeholder dirt withholds readiness but is not evidence the caller may record as durable dirt.
        Assert.False(failed.Validated);
        Assert.NotEmpty(failed.DirtyMedia);

        failRead = false;
        var validated = await session.ReconcileAsync([], includeConsumed: true, default, readOnly: true);
        Assert.True(validated.Validated);
        Assert.Empty(validated.DirtyMedia);
    }
}
