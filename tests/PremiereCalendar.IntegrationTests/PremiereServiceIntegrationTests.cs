using Microsoft.Extensions.DependencyInjection;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class PremiereServiceIntegrationTests
{
    [Fact]
    public async Task GetPremieresAsync_ReturnsStableListFromFixtureJson()
    {
        var tmdbHandler = new StubHttpMessageHandler(RespondFromTmdbFixtures);
        var omdbHandler = new StubHttpMessageHandler(RespondFromOmdbFixtures);
        var tvmazeHandler = new StubHttpMessageHandler(RespondFromTvmazeFixtures);
        var cache = new FakeCalendarCache();
        using var provider = CreateProvider(tmdbHandler, omdbHandler, tvmazeHandler, cache);
        var service = provider.GetRequiredService<IPremiereService>();

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        Assert.Equal(2, items.Count);

        var series = Assert.Single(items, item => item.MediaType == PremiereMediaType.Series);
        Assert.Equal("Pilot Week", series.Title);
        Assert.Equal("tv:100", series.CanonicalId);
        Assert.Equal(PremiereItemType.SeriesPremiere, series.Type);
        Assert.Equal("tt0000100", series.ImdbId);
        Assert.Equal(81189, series.TvdbId);
        Assert.Equal("Q23572", series.WikidataId);
        Assert.Equal("https://www.youtube.com/watch?v=pilot-trailer", series.TrailerUrl);
        Assert.Equal("https://image.tmdb.org/t/p/w342/pilot-week.jpg", series.PosterUrl);
        Assert.Equal("TMDb poster", series.ImageSource);
        Assert.Equal(48, series.RuntimeMinutes);
        Assert.Equal(8.2, series.ImdbScore);
        Assert.Equal(1234, series.ImdbVoteCount);
        Assert.Equal(91, series.RottenTomatoesScore);
        Assert.Equal(72, series.MetacriticScore);
        Assert.Equal("HBO", series.NetworkName);
        Assert.Contains("HBO", series.SourceNames);
        Assert.Contains("VTM GO", series.SourceNames);
        Assert.Equal(8.6, series.TvmazeRating);
        Assert.Equal("https://www.tvmaze.com/shows/82/game-of-thrones", series.TvmazeUrl);

        var movie = Assert.Single(items, item => item.MediaType == PremiereMediaType.Movie);
        Assert.Equal("Independent Feature", movie.Title);
        Assert.Equal("movie:200", movie.CanonicalId);
        Assert.Equal(PremiereItemType.MovieFirstRelease, movie.Type);
        Assert.Equal("https://www.youtube.com/watch?v=feature-teaser", movie.TrailerUrl);
        Assert.Equal("https://m.media-amazon.com/images/M/independent-feature.jpg", movie.PosterUrl);
        Assert.Equal("OMDb poster", movie.ImageSource);
        Assert.Equal(102, movie.RuntimeMinutes);
        Assert.Equal(7.4, movie.ImdbScore);
        Assert.Null(movie.RottenTomatoesScore);
        Assert.Contains("Netflix", movie.SourceNames);
        Assert.Contains("Apple TV", movie.SourceNames);

        Assert.Contains(tmdbHandler.Requests, request => request.Uri.AbsolutePath.EndsWith("/discover/tv"));
        Assert.Contains(tmdbHandler.Requests, request =>
        {
            var query = QueryString.Parse(request.Uri);

            return request.Uri.AbsolutePath.EndsWith("/discover/tv")
                && !query.ContainsKey("with_origin_country")
                && !query.ContainsKey("with_original_language")
                && !query.ContainsKey("with_networks");
        });
        Assert.Contains(tmdbHandler.Requests, request =>
        {
            var query = QueryString.Parse(request.Uri);

            return request.Uri.AbsolutePath.EndsWith("/discover/movie")
                && !query.ContainsKey("with_origin_country")
                && !query.ContainsKey("with_original_language");
        });
        Assert.Contains(omdbHandler.Requests, request => QueryString.Parse(request.Uri)["i"] == "tt0000100");
        Assert.Contains(omdbHandler.Requests, request => QueryString.Parse(request.Uri)["i"] == "tt0000200");
        Assert.Contains(tvmazeHandler.Requests, request => QueryString.Parse(request.Uri)["thetvdb"] == "81189");
        Assert.NotNull(cache.StoredItems);
    }

    [Fact]
    public async Task GetPremieresAsync_UsesUnfilteredTmdbDateWindowDiscovery()
    {
        var tmdbHandler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = QueryString.Parse(request.RequestUri);

            if (path.EndsWith("/discover/tv")
                && !query.ContainsKey("with_origin_country")
                && !query.ContainsKey("with_original_language")
                && !query.ContainsKey("with_networks"))
            {
                return StubHttpMessageHandler.Json(
                    """
                    {
                      "page": 1,
                      "total_pages": 1,
                      "results": [
                        {
                          "id": 410,
                          "name": "Belgian Origin Show",
                          "first_air_date": "2026-05-04",
                          "original_language": "fr",
                          "origin_country": ["BE"],
                          "vote_average": 7.8,
                          "vote_count": 9
                        }
                      ]
                    }
                    """);
            }

            if (path.EndsWith("/discover/movie")
                && !query.ContainsKey("with_origin_country")
                && !query.ContainsKey("with_original_language"))
            {
                return StubHttpMessageHandler.Json(
                    """
                    {
                      "page": 1,
                      "total_pages": 1,
                      "results": [
                        {
                          "id": 420,
                          "title": "Belgian Origin Movie",
                          "release_date": "2026-05-05",
                          "original_language": "fr",
                          "origin_country": ["BE"],
                          "vote_average": 6.9,
                          "vote_count": 4
                        }
                      ]
                    }
                    """);
            }

            if (path.EndsWith("/tv/410"))
            {
                return StubHttpMessageHandler.Json("""{"id":410,"external_ids":{},"videos":{"results":[]},"watch/providers":{"results":{}}}""");
            }

            if (path.EndsWith("/movie/420"))
            {
                return StubHttpMessageHandler.Json("""{"id":420,"external_ids":{},"videos":{"results":[]},"watch/providers":{"results":{}},"production_countries":[{"iso_3166_1":"BE"}]}""");
            }

            return StubHttpMessageHandler.Json("""{"page":1,"total_pages":1,"results":[]}""");
        });
        var omdbHandler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("omdb/response-false.json")));
        var tvmazeHandler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{}", System.Net.HttpStatusCode.NotFound));
        var cache = new FakeCalendarCache();
        using var provider = CreateProvider(tmdbHandler, omdbHandler, tvmazeHandler, cache);
        var service = provider.GetRequiredService<IPremiereService>();

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        Assert.Contains(items, item => item.Title == "Belgian Origin Show" && item.OriginCountries.Contains("BE"));
        Assert.Contains(items, item => item.Title == "Belgian Origin Movie" && item.OriginCountries.Contains("BE"));
        Assert.DoesNotContain(tmdbHandler.Requests, request => QueryString.Parse(request.Uri).ContainsKey("with_origin_country"));
        Assert.DoesNotContain(tmdbHandler.Requests, request => QueryString.Parse(request.Uri).ContainsKey("with_original_language"));
    }

    [Fact]
    public async Task GetPremieresAsync_UsesWeekCacheWhenAvailable()
    {
        var tmdbHandler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("TMDb should not be called when cache is warm."));
        var omdbHandler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("OMDb should not be called when cache is warm."));
        var tvmazeHandler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("TVmaze should not be called when cache is warm."));
        var cache = new FakeCalendarCache
        {
            CachedItems =
            [
                new PremiereItem
                {
                    CanonicalId = "movie:1",
                    Type = PremiereItemType.MovieFirstRelease,
                    MediaType = PremiereMediaType.Movie,
                    TmdbId = 1,
                    Title = "Cached Premiere",
                    PremiereDate = new DateOnly(2026, 5, 4)
                }
            ]
        };
        using var provider = CreateProvider(tmdbHandler, omdbHandler, tvmazeHandler, cache);
        var service = provider.GetRequiredService<IPremiereService>();

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("Cached Premiere", item.Title);
        Assert.Empty(tmdbHandler.Requests);
    }

    [Fact]
    public async Task GetPremieresAsync_FallsBackToCachedWeekWhenRefreshFails()
    {
        var tmdbHandler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json("""{"status_message":"temporarily unavailable"}""", System.Net.HttpStatusCode.InternalServerError));
        var omdbHandler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("OMDb should not be called when discovery fails."));
        var tvmazeHandler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("TVmaze should not be called when discovery fails."));
        var cache = new FakeCalendarCache
        {
            CachedItems =
            [
                new PremiereItem
                {
                    CanonicalId = "tv:300",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 300,
                    Title = "Cached Fallback",
                    PremiereDate = new DateOnly(2026, 5, 4)
                }
            ]
        };
        using var provider = CreateProvider(tmdbHandler, omdbHandler, tvmazeHandler, cache);
        var service = provider.GetRequiredService<IPremiereService>();

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            forceRefresh: true);

        Assert.Equal("Cached Fallback", Assert.Single(items).Title);
        Assert.NotEmpty(tmdbHandler.Requests);
        Assert.Null(cache.StoredItems);
    }

    private static ServiceProvider CreateProvider(
        StubHttpMessageHandler tmdbHandler,
        StubHttpMessageHandler omdbHandler,
        StubHttpMessageHandler tvmazeHandler,
        ICalendarCache cache)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddMemoryCache();
        services.Configure<TmdbOptions>(options =>
        {
            options.BaseUrl = "https://api.themoviedb.org/3/";
            options.ImageBaseUrl = "https://image.tmdb.org/t/p/";
            options.PosterSize = "w342";
            options.BackdropSize = "w780";
            options.BearerToken = "test-token";
            options.MaxPagesPerQuery = 2;
        });
        services.Configure<OmdbOptions>(options =>
        {
            options.BaseUrl = "https://www.omdbapi.com/";
            options.Enabled = true;
            options.ApiKey = "test-key";
        });
        services.Configure<TvmazeOptions>(options =>
        {
            options.BaseUrl = "https://api.tvmaze.com/";
            options.Enabled = true;
        });

        services.AddHttpClient<ITmdbClient, TmdbClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.themoviedb.org/3/");
        }).ConfigurePrimaryHttpMessageHandler(() => tmdbHandler);

        services.AddHttpClient<IOmdbClient, OmdbClient>(client =>
        {
            client.BaseAddress = new Uri("https://www.omdbapi.com/");
        }).ConfigurePrimaryHttpMessageHandler(() => omdbHandler);

        services.AddHttpClient<ITvmazeClient, TvmazeClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.tvmaze.com/");
        }).ConfigurePrimaryHttpMessageHandler(() => tvmazeHandler);

        services.AddSingleton(cache);
        services.AddSingleton<IWatchmodeClient, DisabledWatchmodeClient>();
        services.AddSingleton<TrailerSelector>();
        services.AddSingleton<RatingMapper>();
        services.AddScoped<IPremiereService, PremiereService>();

        return services.BuildServiceProvider();
    }

    private static CalendarFilters NewSeriesOnlyFilters()
    {
        return new CalendarFilters
        {
            SeriesFilters =
            {
                SeriesDateMode = SeriesDateMode.NewSeriesOnly
            }
        };
    }

    private static HttpResponseMessage RespondFromTmdbFixtures(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = QueryString.Parse(request.RequestUri);

        if (path.EndsWith("/discover/tv")
            && !query.ContainsKey("with_original_language")
            && !query.ContainsKey("with_origin_country")
            && !query.ContainsKey("with_networks"))
        {
            return StubHttpMessageHandler.Json(Fixture.Read("tmdb/discover-tv-en-premieres.json"));
        }

        if (path.EndsWith("/discover/movie")
            && !query.ContainsKey("with_original_language")
            && !query.ContainsKey("with_origin_country"))
        {
            return StubHttpMessageHandler.Json(Fixture.Read("tmdb/discover-movie-en.json"));
        }

        if (path.EndsWith("/tv/100"))
        {
            return StubHttpMessageHandler.Json(Fixture.Read("tmdb/tv-details-with-videos.json"));
        }

        if (path.EndsWith("/movie/200"))
        {
            return StubHttpMessageHandler.Json(Fixture.Read("tmdb/movie-details-with-videos.json"));
        }

        return StubHttpMessageHandler.Json("""{"status_message":"unexpected TMDb route"}""", System.Net.HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage RespondFromOmdbFixtures(HttpRequestMessage request)
    {
        var query = QueryString.Parse(request.RequestUri!);

        return query["i"] switch
        {
            "tt0000100" => StubHttpMessageHandler.Json(Fixture.Read("omdb/by-imdb-id-success.json")),
            "tt0000200" => StubHttpMessageHandler.Json(Fixture.Read("omdb/by-imdb-id-missing-rt.json")),
            _ => StubHttpMessageHandler.Json(Fixture.Read("omdb/response-false.json"))
        };
    }

    private static HttpResponseMessage RespondFromTvmazeFixtures(HttpRequestMessage request)
    {
        var query = QueryString.Parse(request.RequestUri!);

        if (query.TryGetValue("thetvdb", out var tvdbId) && tvdbId == "81189")
        {
            return StubHttpMessageHandler.Json(Fixture.Read("tvmaze/lookup-show-success.json"));
        }

        return StubHttpMessageHandler.Json("{}", System.Net.HttpStatusCode.NotFound);
    }

    private sealed class FakeCalendarCache : ICalendarCache
    {
        public IReadOnlyList<PremiereItem>? CachedItems { get; init; }
        public IReadOnlyList<PremiereItem>? StoredItems { get; private set; }

        public Task<IReadOnlyList<PremiereItem>?> GetWeekAsync(
            DateOnly start,
            DateOnly end,
            string cacheKey,
            CancellationToken cancellationToken,
            bool allowExpired = false)
        {
            return Task.FromResult(CachedItems);
        }

        public Task SetWeekAsync(DateOnly start, DateOnly end, string cacheKey, IReadOnlyList<PremiereItem> items, CancellationToken cancellationToken)
        {
            StoredItems = items;
            return Task.CompletedTask;
        }
    }

    private sealed class DisabledWatchmodeClient : IWatchmodeClient
    {
        public Task<IReadOnlyList<PremiereSource>> GetTitleSourcesAsync(
            PremiereMediaType mediaType,
            int tmdbId,
            string? imdbId,
            IReadOnlyList<string> regions,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<PremiereSource>>([]);
        }

        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetReleaseCandidatesAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<ExternalPremiereCandidate>>([]);
        }
    }
}
