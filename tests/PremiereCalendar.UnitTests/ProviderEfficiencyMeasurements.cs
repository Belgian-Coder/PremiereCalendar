using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

// Deliberately uses only APIs available at cef3cb6 so the identical fixture can
// run in separate baseline/current processes. Timings are evidence, not asserts.
public sealed partial class PremiereServiceTests
{
    [Fact]
    public Task ProviderEfficiencyMeasurement_SimklAdjacentWeeksSeries()
        => MeasureAdjacentSimklWeeksAsync(series: true);

    [Fact]
    public Task ProviderEfficiencyMeasurement_SimklAdjacentWeeksMovies()
        => MeasureAdjacentSimklWeeksAsync(series: false);

    [Fact]
    public Task ProviderEfficiencyMeasurement_SeriesCriticalPathWithArtwork()
        => MeasureSeriesCriticalPathAsync(returnArtwork: true);

    [Fact]
    public Task ProviderEfficiencyMeasurement_SeriesCriticalPathWithBackdropFallback()
        => MeasureSeriesCriticalPathAsync(returnArtwork: false);

    private async Task MeasureAdjacentSimklWeeksAsync(bool series)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var handler = new EfficiencyCalendarHandler(series);
        using var firstHttp = new HttpClient(handler, disposeHandler: false);
        using var secondHttp = new HttpClient(handler, disposeHandler: false);
        var options = Microsoft.Extensions.Options.Options.Create(new SimklOptions
            { Enabled = true, ClientId = "captured-client", CalendarBaseUrl = "https://captured.simkl.example/" });
        var firstClient = new SimklClient(firstHttp, options, new EfficiencySyncStore(), cache: memory);
        var secondClient = new SimklClient(secondHttp, options, new EfficiencySyncStore(), cache: memory);

        var timer = Stopwatch.StartNew();
        var first = await firstClient.GetCalendarAsync(new(2020, 8, 31), new(2020, 9, 6), series, !series, CancellationToken.None);
        var second = await secondClient.GetCalendarAsync(new(2020, 9, 7), new(2020, 9, 13), series, !series, CancellationToken.None);
        timer.Stop();

        var expected = new[]
        {
            new[] { EfficiencyCalendarItem(series, 1, "Current first", "2020-09-02"),
                EfficiencyCalendarItem(series, 3, "August archive", "2020-08-31"),
                EfficiencyCalendarItem(series, 4, "September first", "2020-09-05") },
            new[] { EfficiencyCalendarItem(series, 2, "Current second", "2020-09-09"),
                EfficiencyCalendarItem(series, 5, "September second", "2020-09-12") }
        };
        var projection = JsonSerializer.Serialize(new[] { first.ToArray(), second.ToArray() });
        Assert.Equal(JsonSerializer.Serialize(expected), projection);
        Assert.Equal(3, handler.Paths.Distinct(StringComparer.Ordinal).Count());
        Assert.InRange(handler.Paths.Count, 1, 5); // Baseline 5 and feed-reuse 3 are both valid here.
        var suffix = series ? "/tv.json" : "/movie_release.json";
        Assert.All(handler.Paths, path => Assert.EndsWith(suffix, path, StringComparison.Ordinal));
        _evidence.WriteLine(FormattableString.Invariant(
            $"MEASUREMENT scenario=simkl_adjacent_weeks media={(series ? "series" : "movie")} fixture_latency_ms=100 elapsed_ms={timer.Elapsed.TotalMilliseconds:F2} sends={handler.Paths.Count} distinct_feeds=3 first_rows={first.Count} second_rows={second.Count} projection_sha256={EfficiencyHash(projection)}"));
    }

    private static SimklCalendarItem EfficiencyCalendarItem(bool series, int id, string title, string day) => new(
        series ? SimklCalendarItemType.Tv : SimklCalendarItemType.MovieRelease, title,
        new DateTimeOffset(DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture), $"https://simkl.com/titles/{id}",
        new SimklCalendarIds(id, (1000 + id).ToString(CultureInfo.InvariantCulture), $"tt{id:0000000}", (2000 + id).ToString(CultureInfo.InvariantCulture)),
        new SimklCalendarRatings(new SimklRating(7 + id / 10d, 1000 + id)),
        series ? new SimklCalendarEpisode(1, id, $"https://simkl.com/episodes/{id}") : null);

    private async Task MeasureSeriesCriticalPathAsync(bool returnArtwork)
    {
        var date = new DateOnly(2026, 8, 12);
        var tmdb = new FakeTmdbClient
        {
            TvItems = [new TmdbTvDiscoverItem
            {
                Id = 9501, Name = "Captured series", OriginalName = "Captured original title", FirstAirDate = "2026-08-12",
                Overview = "TMDb captured overview", OriginalLanguage = "en", OriginCountry = ["US"], GenreIds = [18],
                VoteAverage = 7.2, VoteCount = 234, BackdropPath = "/captured-backdrop.jpg"
            }]
        };
        tmdb.TvDetailsById[9501] = new TmdbDetailsWithExtras
        {
            Id = 9501, Name = "Captured details", OriginalLanguage = "en", Genres = [new TmdbGenre { Id = 18, Name = "Drama" }],
            ExternalIds = new TmdbExternalIds { ImdbId = "tt0095010", TvdbId = 9502, WikidataId = "Q9501" },
            Videos = new TmdbVideoResponse { Results = [new TmdbVideo
                { Site = "YouTube", Type = "Trailer", Key = "captured-series-trailer", Official = true }] }
        };
        var omdb = new EfficiencyOmdbClient();
        var tvmaze = new EfficiencyTvmazeClient();
        var tomatoes = new EfficiencyTomatoesClient();
        var artwork = new EfficiencyArtworkProvider(returnArtwork);
        var watchmode = new EfficiencyWatchmodeClient();
        var service = new PremiereService(tmdb, omdb, tvmaze, watchmode, new NullCalendarCache(), new TrailerSelector(),
            new RatingMapper(), [artwork], [], Microsoft.Extensions.Options.Options.Create(new TmdbOptions()),
            NullLogger<PremiereService>.Instance, rottenTomatoesClient: tomatoes);

        var timer = Stopwatch.StartNew();
        var rows = await service.GetPremieresAsync(date, date, CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = true, ShowMovies = false,
                SeriesFilters = { SeriesDateMode = SeriesDateMode.NewSeriesOnly } });
        timer.Stop();

        var item = Assert.Single(rows);
        Assert.Equal(PremiereIdentity.CanonicalId(PremiereMediaType.Series, 9501), item.CanonicalId);
        Assert.Equal("Captured series", item.Title);
        Assert.Equal("Captured original title", item.OriginalTitle);
        Assert.Equal(date, item.PremiereDate);
        Assert.Equal("TMDb captured overview", item.Overview);
        Assert.Equal("tt0095010", item.ImdbId);
        Assert.Equal(9502, item.TvdbId);
        Assert.Equal("Q9501", item.WikidataId);
        Assert.Equal(43, item.RuntimeMinutes);
        Assert.Equal(43, item.TvmazeAverageRuntimeMinutes);
        Assert.Equal(8.8, item.TvmazeRating);
        Assert.Equal("https://captured.example/series", item.OfficialSiteUrl);
        Assert.Equal("https://www.tvmaze.com/shows/9502/captured", item.TvmazeUrl);
        Assert.Equal(7.2, item.TmdbScore);
        Assert.Equal(234, item.TmdbVoteCount);
        Assert.Equal(7.6, item.ImdbScore);
        Assert.Equal(12_345, item.ImdbVoteCount);
        Assert.Equal(80, item.RottenTomatoesScore);
        Assert.Equal(69, item.RottenTomatoesAudienceScore);
        Assert.Equal(71, item.MetacriticScore);
        Assert.Equal(returnArtwork ? "https://captured.example/series.jpg" : item.BackdropUrl, item.PosterUrl);
        Assert.Equal(returnArtwork ? "Captured artwork" : "TMDb backdrop", item.ImageSource);
        Assert.EndsWith("/captured-backdrop.jpg", item.BackdropUrl!, StringComparison.Ordinal);
        Assert.Equal(["Captured availability"], item.SourceNames);
        Assert.Equal(new PremiereSource { Id = 31, Name = "Captured availability", Kind = "flatrate" }, Assert.Single(item.Sources));
        Assert.Equal([18], item.GenreIds);
        Assert.Equal(["Drama"], item.Genres);
        Assert.Equal(["US"], item.OriginCountries);
        Assert.Equal("en", item.OriginalLanguage);
        Assert.Empty(item.MissingDataIssues);
        Assert.Single(tmdb.TvCalls);
        Assert.Single(tmdb.TvDetailCalls);
        Assert.Equal(2, tmdb.TvSeasonDetailCalls.Count);
        Assert.Empty(tmdb.MovieCalls);
        Assert.Equal(1, omdb.Calls);
        Assert.Equal(1, tvmaze.LookupCalls);
        Assert.Equal(1, tvmaze.SearchCalls);
        Assert.Equal(1, tomatoes.Calls);
        Assert.Equal(1, artwork.Calls);
        Assert.Equal(1, watchmode.Calls);
        // Hash the complete row; only its wall-clock observation time is normalized.
        var projection = JsonSerializer.Serialize(item with { LastUpdatedUtc = DateTimeOffset.UnixEpoch });
        _evidence.WriteLine(FormattableString.Invariant(
            $"MEASUREMENT scenario=series_critical_path artwork={(returnArtwork ? "candidate" : "backdrop")} fixture_latency_ms=200 elapsed_ms={timer.Elapsed.TotalMilliseconds:F2} rows={rows.Count} tmdb_discover_calls={tmdb.TvCalls.Count} tmdb_detail_calls={tmdb.TvDetailCalls.Count} tmdb_season_calls={tmdb.TvSeasonDetailCalls.Count} omdb_calls={omdb.Calls} tvmaze_lookup_calls={tvmaze.LookupCalls} tvmaze_search_calls={tvmaze.SearchCalls} rt_calls={tomatoes.Calls} artwork_calls={artwork.Calls} availability_calls={watchmode.Calls} projection_sha256={EfficiencyHash(projection)}"));
    }

    private static string EfficiencyHash(string projection)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projection)));

    private sealed class EfficiencyCalendarHandler(bool series) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Paths { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Enqueue(path);
            await Task.Delay(100, token);
            var rows = path.Contains("/2020/8/", StringComparison.Ordinal)
                ? new[] { (3, "August archive", "2020-08-31") }
                : path.Contains("/2020/9/", StringComparison.Ordinal)
                    ? new[] { (1, "Current first", "2020-09-02"), (4, "September first", "2020-09-05"), (5, "September second", "2020-09-12") }
                    : new[] { (1, "Current first", "2020-09-02"), (2, "Current second", "2020-09-09") };
            var payload = JsonSerializer.Serialize(rows.Select(row => new
            {
                title = row.Item2, date = row.Item3 + "T00:00:00Z", release_date = row.Item3, url = $"/titles/{row.Item1}",
                ids = new { simkl_id = row.Item1, tmdb = 1000 + row.Item1, imdb = $"tt{row.Item1:0000000}", tvdb = 2000 + row.Item1 },
                ratings = new { imdb = new { rating = 7 + row.Item1 / 10d, votes = 1000 + row.Item1 } },
                episode = series ? new { season = 1, episode = row.Item1, url = $"/episodes/{row.Item1}" } : null
            }));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class EfficiencySyncStore : ISimklSyncStateStore
    {
        public Task<SimklSyncState> GetAsync(CancellationToken token) => Task.FromResult(new SimklSyncState());
        public Task SaveAsync(SimklSyncState state, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class EfficiencyOmdbClient : IOmdbClient
    {
        public int Calls;
        public async Task<OmdbItem?> GetByImdbIdAsync(string id, CancellationToken token, bool forceRefresh = false)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(200, token);
            return new OmdbItem { Response = "True", ImdbRating = "7.6", ImdbVotes = "12,345", Metascore = "71",
                Ratings = [new OmdbRating { Source = "Rotten Tomatoes", Value = "80%" }] };
        }
    }

    private sealed class EfficiencyTomatoesClient : IRottenTomatoesClient
    {
        public int Calls;
        public async Task<RottenTomatoesScores> GetScoresAsync(PremiereMediaType type, string title, int? year,
            string? wikidataId, CancellationToken token, bool forceRefresh = false)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(200, token);
            return new RottenTomatoesScores(91, 69);
        }
    }

    private sealed class EfficiencyArtworkProvider(bool returnArtwork) : IArtworkProvider
    {
        public int Calls;
        public async Task<ArtworkCandidate?> GetArtworkAsync(ArtworkRequest request, CancellationToken token, bool forceRefresh = false)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(200, token);
            return returnArtwork ? new ArtworkCandidate("https://captured.example/series.jpg", "Captured artwork") : null;
        }
    }

    private sealed class EfficiencyTvmazeClient : ITvmazeClient
    {
        public int LookupCalls;
        public int SearchCalls;
        public async Task<TvmazeShow?> LookupShowAsync(int? tvdbId, string? imdbId, CancellationToken token, bool forceRefresh = false)
        {
            Interlocked.Increment(ref LookupCalls);
            await Task.Delay(200, token);
            return new TvmazeShow { Id = 9502, Name = "Captured series", AverageRuntime = 43,
                Rating = new TvmazeRating { Average = 8.8 }, OfficialSite = "https://captured.example/series",
                Url = "https://www.tvmaze.com/shows/9502/captured", Summary = "<p>TVmaze captured summary</p>" };
        }
        public Task<TvmazeShow?> SearchShowByNameAsync(string title, CancellationToken token, bool forceRefresh = false)
        {
            Interlocked.Increment(ref SearchCalls);
            return Task.FromResult<TvmazeShow?>(null);
        }
        public Task<IReadOnlyList<TvmazeShowImage>> GetShowImagesAsync(int id, CancellationToken token, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<TvmazeShowImage>>([]);
        public Task<IReadOnlyList<TvmazeScheduleEpisode>> GetScheduleAsync(DateOnly date, string? country, bool web, CancellationToken token, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<TvmazeScheduleEpisode>>([]);
        public Task<IReadOnlyList<TvmazeShowUpdate>> GetShowUpdatesAsync(TvmazeUpdateWindow since, CancellationToken token, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<TvmazeShowUpdate>>([]);
    }

    private sealed class EfficiencyWatchmodeClient : IWatchmodeClient
    {
        public int Calls;
        public async Task<IReadOnlyList<PremiereSource>> GetTitleSourcesAsync(PremiereMediaType type, int tmdbId, string? imdbId,
            IReadOnlyList<string> regions, CancellationToken token, bool forceRefresh = false)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(200, token);
            return [new PremiereSource { Id = 31, Name = "Captured availability", Kind = "flatrate" }];
        }
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetReleaseCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken token, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<ExternalPremiereCandidate>>([]);
    }
}
