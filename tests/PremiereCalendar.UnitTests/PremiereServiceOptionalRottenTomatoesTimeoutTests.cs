using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalRottenTomatoesTimeoutRetainsDiscoveryCardsAcrossDays(bool external)
    {
        var start = new DateOnly(2026, 5, 4);
        var tmdb = new FakeTmdbClient
        {
            FilterMovieItemsByDate = true,
            MovieItems = external ? [] :
            [
                new() { Id = 9081, Title = "Optional timeout first movie", ReleaseDate = "2026-05-04" },
                new() { Id = 9082, Title = "Optional timeout second movie", ReleaseDate = "2026-05-05" }
            ]
        };
        tmdb.MovieDetailsById[9081] = new() { Id = 9081, Title = "Optional timeout first movie", ReleaseDate = "2026-05-04" };
        tmdb.MovieDetailsById[9082] = new() { Id = 9082, Title = "Optional timeout second movie", ReleaseDate = "2026-05-05" };
        var provider = new FakeDiscoveryProvider
        {
            DisplayName = "Simkl",
            Candidates =
            [
                new(PremiereMediaType.Movie, start, "Optional timeout first movie", 9081, null, null, "Simkl"),
                new(PremiereMediaType.Movie, start.AddDays(1), "Optional timeout second movie", 9082, null, null, "Simkl")
            ]
        };
        var tomatoes = new OptionalTimeoutRottenTomatoesClient();
        var reports = new List<PremiereLoadProgress>();
        var root = Path.Combine(Path.GetTempPath(), "premiere-optional-timeout", Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new PerformanceDiagnosticStore(new TelemetryOptions { LogDirectory = root }, root);
            var service = CreateService(tmdb, rottenTomatoes: tomatoes, discoveryProviders: external ? [provider] : [], telemetry: new(store));
            var items = await service.GetPremieresAsync(start, start.AddDays(1), CancellationToken.None,
                filters: new CalendarFilters { ShowSeries = false, ShowMovies = true },
                progress: new RecordingProgress<PremiereLoadProgress>(reports.Add));
            Assert.Equal([9081, 9082], items.OrderBy(item => item.PremiereDate).Select(item => item.TmdbId));
            Assert.Equal([start, start.AddDays(1)], items.OrderBy(item => item.PremiereDate).Select(item => item.PremiereDate));
            Assert.True(tomatoes.Calls >= 2);
            Assert.DoesNotContain(reports, report => report.HasSourceErrors);
            Assert.Contains(reports, report => report.IsFinal && report.TotalItemCount == 2);
            store.Flush();
            store.Dispose();
            var events = Directory.GetFiles(Path.Combine(root, "performance"), "performance-*.ndjson")
                .SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            var timeouts = events.Where(value => value.GetProperty("event").GetString() == "enrichment.optional.timeout").ToArray();
            Assert.NotEmpty(timeouts);
            Assert.All(timeouts, value =>
            {
                var details = value.GetProperty("details");
                Assert.Equal("rottentomatoes", details.GetProperty("provider").GetString());
                Assert.Equal("dependency_timeout", details.GetProperty("reason").GetString());
                Assert.Equal("skipped", details.GetProperty("outcome").GetString());
                Assert.DoesNotContain("Optional timeout first movie", value.GetRawText());
                Assert.DoesNotContain("secret-url", value.GetRawText());
            });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OptionalRottenTomatoesTimeoutDoesNotNegativeCacheClientOrServiceResult()
    {
        var tmdb = OptionalTomatoesMovie();
        var requests = 0;
        using var http = new HttpClient(new OptionalTomatoesHttpHandler(request =>
        {
            if (Interlocked.Increment(ref requests) == 1) throw new TaskCanceledException("Optional request timeout.");
            var html = request.RequestUri!.AbsolutePath == "/search"
                ? "<search-page-media-row release-year=\"2026\" tomatometer-score=\"88\"><a href=\"https://www.rottentomatoes.com/m/optional_timeout_movie\" data-qa=\"info-name\" slot=\"title\">Optional Timeout Movie</a></search-page-media-row>"
                : "<script id=\"media-scorecard-json\" type=\"application/json\">{\"criticsScore\":{\"score\":\"88\"},\"audienceScore\":{\"score\":\"91\"}}</script>";
            return new(HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") };
        })) { BaseAddress = new Uri("https://www.rottentomatoes.com/") };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var tomatoes = new RottenTomatoesClient(http, cache, Microsoft.Extensions.Options.Options.Create(new RottenTomatoesOptions { Enabled = true }),
            NullLogger<RottenTomatoesClient>.Instance);
        var service = CreateService(tmdb, rottenTomatoes: tomatoes);
        var filters = new CalendarFilters { ShowSeries = false, ShowMovies = true };
        var start = new DateOnly(2026, 5, 4);
        var first = Assert.Single(await service.GetPremieresAsync(start, start, CancellationToken.None, filters: filters));
        Assert.Null(first.RottenTomatoesScore);
        Assert.Equal(1, requests);
        Assert.False(tomatoes.TryGetCachedScores(PremiereMediaType.Movie, "Optional Timeout Movie", 2026, null, out _));
        var recovered = Assert.Single(await service.GetPremieresAsync(start, start, CancellationToken.None, filters: filters));
        Assert.Equal(88, recovered.RottenTomatoesScore);
        Assert.Equal(91, recovered.RottenTomatoesAudienceScore);
        Assert.True(requests > 1);
        Assert.True(tomatoes.TryGetCachedScores(PremiereMediaType.Movie, "Optional Timeout Movie", 2026, null, out var cached));
        Assert.Equal(88, cached.CriticScore);
    }

    [Fact]
    public async Task OptionalRottenTomatoesTimeoutPreservesExistingCachedCriticScore()
    {
        var start = new DateOnly(2026, 5, 4);
        var cached = new PremiereItem
        {
            CanonicalId = "movie:9083", Type = PremiereItemType.MovieFirstRelease, MediaType = PremiereMediaType.Movie,
            TmdbId = 9083, Title = "Optional Timeout Movie", PremiereDate = start, RottenTomatoesScore = 78
        };
        var tomatoes = new OptionalTimeoutRottenTomatoesClient();
        var service = CreateService(new FakeTmdbClient(), calendarCache: new FakeCalendarCache { Items = [cached] }, rottenTomatoes: tomatoes);
        var item = Assert.Single(await service.GetPremieresAsync(start, start, CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true, ScoreSource = ScoreSource.RottenTomatoes }));
        Assert.Equal(78, item.RottenTomatoesScore);
        Assert.Null(item.RottenTomatoesAudienceScore);
        Assert.True(tomatoes.Calls > 0);
    }

    [Fact]
    public async Task OptionalRottenTomatoesLookupStillPropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var tomatoes = new OptionalTimeoutRottenTomatoesClient(blockUntilCancelled: true);
        var service = CreateService(OptionalTomatoesMovie(), rottenTomatoes: tomatoes);
        var start = new DateOnly(2026, 5, 4);
        var request = service.GetPremieresAsync(start, start, cancellation.Token,
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });
        await tomatoes.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(tomatoes.ObservedCancellation);
        Assert.Equal(0, tomatoes.ActiveCalls);
    }

    [Fact]
    public async Task OptionalRottenTomatoesLookupStillFailsSourceWhenActualSourceDeadlineExpires()
    {
        var tomatoes = new OptionalTimeoutRottenTomatoesClient(blockUntilCancelled: true);
        var calendar = new FakeCalendarCache();
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(OptionalTomatoesMovie(), rottenTomatoes: tomatoes, calendarCache: calendar, sourceTimeoutSeconds: 1);
        var start = new DateOnly(2026, 5, 4);
        var items = await service.GetPremieresAsync(start, start, CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true },
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add));
        // Discovery may already have published a metadata card before enrichment
        // reaches the source deadline; its failure must still remain visible.
        Assert.Equal(9083, Assert.Single(items).TmdbId);
        Assert.Contains(reports, report => report.HasSourceErrors);
        Assert.True(tomatoes.ObservedCancellation);
        Assert.Equal(0, tomatoes.ActiveCalls);
        Assert.Empty(calendar.SetCalls);
    }

    private static FakeTmdbClient OptionalTomatoesMovie() => new()
    {
        FilterMovieItemsByDate = true,
        MovieItems = [new() { Id = 9083, Title = "Optional Timeout Movie", ReleaseDate = "2026-05-04" }]
    };

    private sealed class OptionalTimeoutRottenTomatoesClient(bool blockUntilCancelled = false) : IRottenTomatoesClient
    {
        private int _calls;
        private int _activeCalls;
        private int _observedCancellation;
        public int Calls => Volatile.Read(ref _calls);
        public int ActiveCalls => Volatile.Read(ref _activeCalls);
        public bool ObservedCancellation => Volatile.Read(ref _observedCancellation) != 0;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RottenTomatoesScores> GetScoresAsync(PremiereMediaType mediaType, string title, int? year,
            string? wikidataId, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Interlocked.Increment(ref _calls);
            Interlocked.Increment(ref _activeCalls);
            Started.TrySetResult();
            try
            {
                if (blockUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new TaskCanceledException("Optional HTTP deadline with secret-url context.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _observedCancellation, 1);
                throw;
            }
            finally { Interlocked.Decrement(ref _activeCalls); }
        }
    }
    private sealed class OptionalTomatoesHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
