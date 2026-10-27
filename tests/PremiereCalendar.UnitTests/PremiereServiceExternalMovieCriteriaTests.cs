using System.Diagnostics;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData("fr", 90)]
    [InlineData("en", 44)]
    public async Task GetPremieresAsync_SkipsKnownNonmatchingExternalMoviesBeforeOptionalEnrichment(
        string detailsLanguage,
        int runtimeMinutes)
    {
        const int tmdbId = 301;
        var date = new DateOnly(2026, 5, 5);
        var tmdb = new FakeTmdbClient();
        tmdb.MovieDetailsById[tmdbId] = MovieDetails(tmdbId, detailsLanguage, runtimeMinutes);
        var omdb = new FakeOmdbClient();
        var rottenTomatoes = new FakeRottenTomatoesClient();
        var artwork = new RecordingArtworkProvider();
        var discovery = MovieProvider(new ExternalPremiereCandidate(
            PremiereMediaType.Movie, date, "Filtered external movie", tmdbId, null, null, "Simkl"));
        var service = CreateService(
            tmdb,
            omdb: omdb,
            artworkProviders: [artwork],
            discoveryProviders: [discovery],
            rottenTomatoes: rottenTomatoes);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: MovieRequestFilters());

        Assert.Empty(items);
        Assert.Empty(omdb.Calls);
        Assert.Empty(rottenTomatoes.Calls);
        Assert.Empty(artwork.Calls);
    }

    [Fact]
    public async Task GetPremieresAsync_EnrichesMatchingExternalMoviesAtInclusiveRuntimeBounds()
    {
        const int minRuntimeMovieId = 302;
        const int maxRuntimeMovieId = 303;
        var date = new DateOnly(2026, 5, 5);
        var tmdb = new FakeTmdbClient();
        tmdb.MovieDetailsById[minRuntimeMovieId] = MovieDetails(minRuntimeMovieId, "en", 45);
        tmdb.MovieDetailsById[maxRuntimeMovieId] = MovieDetails(maxRuntimeMovieId, "nl", 120);
        var omdb = new FakeOmdbClient();
        var rottenTomatoes = new FakeRottenTomatoesClient();
        var artwork = new RecordingArtworkProvider();
        var discovery = MovieProvider(
            new ExternalPremiereCandidate(PremiereMediaType.Movie, date, "Minimum movie", minRuntimeMovieId, null, null, "Simkl"),
            new ExternalPremiereCandidate(PremiereMediaType.Movie, date, "Maximum movie", maxRuntimeMovieId, null, null, "Simkl"));
        var service = CreateService(
            tmdb,
            omdb: omdb,
            artworkProviders: [artwork],
            discoveryProviders: [discovery],
            rottenTomatoes: rottenTomatoes);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: MovieRequestFilters());

        Assert.Equal(2, items.Count);
        Assert.Equal((45, "en"), (items.Single(item => item.TmdbId == minRuntimeMovieId).RuntimeMinutes,
            items.Single(item => item.TmdbId == minRuntimeMovieId).OriginalLanguage));
        Assert.Equal((120, "nl"), (items.Single(item => item.TmdbId == maxRuntimeMovieId).RuntimeMinutes,
            items.Single(item => item.TmdbId == maxRuntimeMovieId).OriginalLanguage));
        Assert.Equal(2, omdb.Calls.Count);
        Assert.Equal(2, rottenTomatoes.Calls.Count);
        Assert.Equal(2, artwork.Calls.Count);
    }

    [Theory]
    [InlineData(null, 90)]
    [InlineData("en", null)]
    public async Task GetPremieresAsync_DoesNotEarlyRejectUnknownExternalMovieLanguageOrRuntime(
        string? detailsLanguage,
        int? runtimeMinutes)
    {
        const int tmdbId = 304;
        var date = new DateOnly(2026, 5, 5);
        var tmdb = new FakeTmdbClient();
        tmdb.MovieDetailsById[tmdbId] = MovieDetails(tmdbId, detailsLanguage, runtimeMinutes);
        var omdb = new FakeOmdbClient();
        var rottenTomatoes = new FakeRottenTomatoesClient();
        var artwork = new RecordingArtworkProvider();
        var discovery = MovieProvider(new ExternalPremiereCandidate(
            PremiereMediaType.Movie, date, "Unknown metadata movie", tmdbId, null, null, "Simkl"));
        var service = CreateService(
            tmdb,
            omdb: omdb,
            artworkProviders: [artwork],
            discoveryProviders: [discovery],
            rottenTomatoes: rottenTomatoes);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: MovieRequestFilters());

        // The final requested filter still removes an item when a required field stays unknown.
        Assert.Empty(items);
        Assert.Single(omdb.Calls);
        Assert.Single(rottenTomatoes.Calls);
        Assert.Single(artwork.Calls);
    }

    [Fact]
    public async Task GetPremieresAsync_ReusesCachedExternalMovieBeforeRequestedCriteriaGate()
    {
        const int tmdbId = 305;
        var date = new DateOnly(2026, 5, 5);
        var cachedItem = new PremiereItem
        {
            CanonicalId = "movie:305",
            Type = PremiereItemType.MovieFirstRelease,
            MediaType = PremiereMediaType.Movie,
            TmdbId = tmdbId,
            Title = "Cached movie title",
            PremiereDate = date,
            OriginalLanguage = "en",
            RuntimeMinutes = 90,
            PosterUrl = "https://images.example/poster.jpg",
            LastUpdatedUtc = DateTimeOffset.UtcNow
        };
        var tmdb = new FakeTmdbClient
        {
            MovieDetailExceptions = { [tmdbId] = new InvalidOperationException("Cached movie details should be reused.") }
        };
        var discovery = MovieProvider(new ExternalPremiereCandidate(
            PremiereMediaType.Movie, date, null, tmdbId, null, null, "ScheduleFixture"));
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery],
            calendarCache: new FakeCalendarCache { ExpiredItems = [cachedItem] });

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: MovieRequestFilters());

        var item = Assert.Single(items);
        Assert.Equal("Cached movie title", item.Title);
        Assert.Contains("ScheduleFixture", item.SourceNames);
    }

    [Fact]
    public async Task GetPremieresAsync_RetainsProgressAndCompletesWhenAllSlowExternalMoviesFailRequestedCriteria()
    {
        const int candidateCount = 128;
        const int firstTmdbId = 10_000;
        var start = new DateOnly(2026, 5, 4);
        var tmdb = new FakeTmdbClient { MovieDetailDelay = TimeSpan.FromMilliseconds(200) };
        var candidates = new ExternalPremiereCandidate[candidateCount];
        for (var index = 0; index < candidateCount; index++)
        {
            var tmdbId = firstTmdbId + index;
            tmdb.MovieDetailsById[tmdbId] = MovieDetails(tmdbId, "fr", 90);
            candidates[index] = new ExternalPremiereCandidate(
                PremiereMediaType.Movie,
                start.AddDays(index % 7),
                $"French external movie {index}",
                tmdbId,
                null,
                null,
                "Simkl");
        }

        var cache = new FakeCalendarCache();
        var discovery = MovieProvider(candidates);
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery],
            calendarCache: cache,
            sourceTimeoutSeconds: 1,
            enrichmentProgressBatchSize: 25);
        var reports = new List<PremiereLoadProgress>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timer = Stopwatch.StartNew();

        var items = await service.GetPremieresAsync(
            start,
            start.AddDays(6),
            cancellation.Token,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add),
            filters: MovieRequestFilters());

        Assert.True(timer.Elapsed > TimeSpan.FromSeconds(1));
        Assert.Empty(items);
        Assert.Contains(reports, report => report.SourceName == "Simkl external movies"
            && report.CompletedWork is >= candidateCount
            && report.TotalWork == candidateCount);
        Assert.Contains(reports, report => report.SourceName == "Simkl external movies"
            && report.Phase == "complete"
            && !report.IsFinal
            && report.SourceItemCount == 0
            && report.CompletedWork == candidateCount
            && report.TotalWork == candidateCount);
        var completion = Assert.Single(reports, report => report.SourceName == "Complete" && report.IsFinal);
        Assert.False(completion.HasSourceErrors);
        Assert.Empty(completion.Items);
        Assert.NotEmpty(cache.SetCalls);
        Assert.Contains(cache.SetCalls, call => call.Items.Count == 0);
    }

    private static CalendarFilters MovieRequestFilters()
    {
        return new CalendarFilters
        {
            ShowSeries = false,
            ShowMovies = true,
            MovieFilters =
            {
                OriginalLanguages = ["en", "nl"],
                RuntimeMinMinutes = 45,
                RuntimeMaxMinutes = 120
            }
        };
    }

    private static FakeDiscoveryProvider MovieProvider(params ExternalPremiereCandidate[] candidates)
    {
        return new FakeDiscoveryProvider
        {
            DisplayName = "Simkl external movies",
            Candidates = candidates
        };
    }

    private static TmdbDetailsWithExtras MovieDetails(int id, string? language, int? runtime)
    {
        return new TmdbDetailsWithExtras
        {
            Id = id,
            OriginalLanguage = language,
            Runtime = runtime,
            ExternalIds = new TmdbExternalIds
            {
                ImdbId = $"tt{id}",
                WikidataId = $"Q{id}"
            }
        };
    }
}
