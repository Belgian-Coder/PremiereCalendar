using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class TmdbClientIntegrationTests
{
    [Fact]
    public async Task DiscoverTvAsync_SendsExpectedQueryParametersAndMergesPages()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var query = QueryString.Parse(request.RequestUri!);
            var page = query["page"];

            var json = page == "1"
                ? """{"page":1,"total_pages":2,"results":[{"id":1,"name":"Page One","first_air_date":"2026-05-04"}]}"""
                : """{"page":2,"total_pages":2,"results":[{"id":2,"name":"Page Two","first_air_date":"2026-05-05"}]}""";

            return StubHttpMessageHandler.Json(json);
        });
        var client = CreateTmdbClient(handler, maxPages: 5);

        var results = await client.DiscoverTvAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters
            {
                OriginalLanguage = "en",
                OriginCountries = ["US", "GB", "AU"]
            },
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(["1", "2"], handler.Requests.Select(x => QueryString.Parse(x.Uri)["page"]).ToArray());

        var firstQuery = QueryString.Parse(handler.Requests[0].Uri);
        Assert.Equal("2026-05-04", firstQuery["first_air_date.gte"]);
        Assert.Equal("2026-05-10", firstQuery["first_air_date.lte"]);
        Assert.Equal("US|GB|AU", firstQuery["with_origin_country"]);
        Assert.False(firstQuery.ContainsKey("air_date.gte"));
    }

    [Fact]
    public async Task DiscoverMoviesAsync_UsesCacheForSameRequest()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("tmdb/discover-movie-en.json")));
        var client = CreateTmdbClient(handler, maxPages: 5);

        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);

        var filters = new TmdbDiscoverFilters
        {
            OriginalLanguage = "en",
            OriginCountries = ["US", "GB", "AU"]
        };

        await client.DiscoverMoviesAsync(start, end, filters, CancellationToken.None);
        await client.DiscoverMoviesAsync(start, end, filters, CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DiscoverMoviesAsync_ForceRefreshBypassesCacheForSameRequest()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("tmdb/discover-movie-en.json")));
        var client = CreateTmdbClient(handler, maxPages: 5);

        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);

        var filters = new TmdbDiscoverFilters
        {
            OriginalLanguage = "en",
            OriginCountries = ["US", "GB", "AU"]
        };

        await client.DiscoverMoviesAsync(start, end, filters, CancellationToken.None);
        await client.DiscoverMoviesAsync(start, end, filters, CancellationToken.None);
        await client.DiscoverMoviesAsync(start, end, filters, CancellationToken.None, forceRefresh: true);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DiscoverMoviesAsync_RetriesAfterTmdbRateLimitResponse()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var rateLimited = StubHttpMessageHandler.Json("""{"status_message":"slow down"}""", HttpStatusCode.TooManyRequests);
                rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return rateLimited;
            }

            return StubHttpMessageHandler.Json(Fixture.Read("tmdb/discover-movie-en.json"));
        });
        var client = CreateTmdbClient(handler, maxPages: 5);

        var results = await client.DiscoverMoviesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters
            {
                OriginalLanguage = "en"
            },
            CancellationToken.None);

        Assert.NotEmpty(results);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DiscoverMoviesAsync_FetchesAllPagesBeyondFiveWhenCapAllows()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var page = int.Parse(QueryString.Parse(request.RequestUri!)["page"]!);
            return StubHttpMessageHandler.Json($$"""
                {
                  "page": {{page}},
                  "total_pages": 7,
                  "results": [
                    {
                      "id": {{page}},
                      "title": "Movie Page {{page}}",
                      "release_date": "2026-05-04"
                    }
                  ]
                }
                """);
        });
        var client = CreateTmdbClient(handler, maxPages: 10);

        var results = await client.DiscoverMoviesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters(),
            CancellationToken.None);

        Assert.Equal(7, results.Count);
        Assert.Equal(Enumerable.Range(1, 7).Select(page => $"Movie Page {page}"), results.Select(result => result.Title));
        Assert.Equal(
            Enumerable.Range(1, 7).Select(page => page.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            handler.Requests.Select(request => QueryString.Parse(request.Uri)["page"]).OrderBy(value => int.Parse(value!)));
    }

    [Fact]
    public async Task StreamDiscoverMoviesAsync_YieldsFirstPageBeforeLaterPageBatches()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var page = int.Parse(QueryString.Parse(request.RequestUri!)["page"]!);
            return StubHttpMessageHandler.Json($$"""
                {
                  "page": {{page}},
                  "total_pages": 5,
                  "total_results": 100,
                  "results": [
                    {
                      "id": {{page}},
                      "title": "Movie Page {{page}}",
                      "release_date": "2026-05-04"
                    }
                  ]
                }
                """);
        });
        var client = CreateTmdbClient(handler, maxPages: 5, pageBatchSize: 2);

        var batches = new List<TmdbDiscoverBatch<TmdbMovieDiscoverItem>>();
        await foreach (var batch in client.StreamDiscoverMoviesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters(),
            CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.Equal([(1, 1), (2, 3), (4, 5)], batches.Select(batch => (batch.PageStart, batch.PageEnd)).ToArray());
        Assert.Equal("Movie Page 1", Assert.Single(batches[0].Results).Title);
        Assert.Equal(["Movie Page 2", "Movie Page 3"], batches[1].Results.Select(item => item.Title ?? "").ToArray());
        Assert.All(batches, batch => Assert.Equal(100, batch.TotalResults));
    }

    [Fact]
    public async Task DiscoverMoviesAsync_StopsAtConfiguredPageCap()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var page = int.Parse(QueryString.Parse(request.RequestUri!)["page"]!);
            return StubHttpMessageHandler.Json($$"""
                {
                  "page": {{page}},
                  "total_pages": 7,
                  "results": [
                    {
                      "id": {{page}},
                      "title": "Movie Page {{page}}",
                      "release_date": "2026-05-04"
                    }
                  ]
                }
                """);
        });
        var client = CreateTmdbClient(handler, maxPages: 5);

        var results = await client.DiscoverMoviesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters(),
            CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.Equal(Enumerable.Range(1, 5).Select(page => $"Movie Page {page}"), results.Select(result => result.Title));
        Assert.Equal(
            Enumerable.Range(1, 5).Select(page => page.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            handler.Requests.Select(request => QueryString.Parse(request.Uri)["page"]).OrderBy(value => int.Parse(value!)));
    }

    [Fact]
    public async Task DiscoverMoviesAsync_UsesLowerPageCapForBroadUnfilteredQueries()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var page = int.Parse(QueryString.Parse(request.RequestUri!)["page"]!);
            return StubHttpMessageHandler.Json($$"""
                {
                  "page": {{page}},
                  "total_pages": 7,
                  "results": [
                    {
                      "id": {{page}},
                      "title": "Movie Page {{page}}",
                      "release_date": "2026-05-04"
                    }
                  ]
                }
                """);
        });
        var client = CreateTmdbClient(handler, maxPages: 10, maxUnfilteredPages: 2);

        var results = await client.DiscoverMoviesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters(),
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(
            ["1", "2"],
            handler.Requests.Select(request => QueryString.Parse(request.Uri)["page"]).OrderBy(value => int.Parse(value!)).ToArray());
    }

    [Fact]
    public async Task DiscoverMoviesAsync_UsesNormalPageCapWhenServerFiltersArePresent()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var page = int.Parse(QueryString.Parse(request.RequestUri!)["page"]!);
            return StubHttpMessageHandler.Json($$"""
                {
                  "page": {{page}},
                  "total_pages": 7,
                  "results": [
                    {
                      "id": {{page}},
                      "title": "Movie Page {{page}}",
                      "release_date": "2026-05-04"
                    }
                  ]
                }
                """);
        });
        var client = CreateTmdbClient(handler, maxPages: 6, maxUnfilteredPages: 2);

        var results = await client.DiscoverMoviesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters { OriginalLanguage = "nl" },
            CancellationToken.None);

        Assert.Equal(6, results.Count);
        Assert.Equal(
            Enumerable.Range(1, 6).Select(page => page.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            handler.Requests.Select(request => QueryString.Parse(request.Uri)["page"]).OrderBy(value => int.Parse(value!)));
    }

    [Fact]
    public async Task DiscoverTvAsync_OmitsOriginalLanguageForBelgianOriginDiscovery()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"page":1,"total_pages":1,"results":[]}"""));
        var client = CreateTmdbClient(handler, maxPages: 5);

        await client.DiscoverTvAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters { OriginCountries = ["BE"] },
            CancellationToken.None);

        var query = QueryString.Parse(Assert.Single(handler.Requests).Uri);
        Assert.Equal("BE", query["with_origin_country"]);
        Assert.False(query.ContainsKey("with_original_language"));
    }

    [Fact]
    public async Task DiscoverTvByNetworksAsync_UsesNetworkFilterAndCachesRequest()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"page":1,"total_pages":1,"results":[]}"""));
        var client = CreateTmdbClient(handler, maxPages: 5);

        var start = new DateOnly(2026, 4, 1);
        var end = new DateOnly(2026, 4, 30);

        await client.DiscoverTvByNetworksAsync(start, end, [556, 5257, 4496], CancellationToken.None);
        await client.DiscoverTvByNetworksAsync(start, end, [4496, 5257, 556], CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        var query = QueryString.Parse(request.Uri);
        Assert.Equal("556|4496|5257", query["with_networks"]);
        Assert.Equal("2026-04-01", query["first_air_date.gte"]);
        Assert.Equal("2026-04-30", query["first_air_date.lte"]);
        Assert.False(query.ContainsKey("with_origin_country"));
        Assert.False(query.ContainsKey("with_original_language"));
    }

    [Fact]
    public async Task DiscoverMoviesAsync_OmitsOriginalLanguageForBelgianOriginDiscovery()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{"page":1,"total_pages":1,"results":[]}"""));
        var client = CreateTmdbClient(handler, maxPages: 5);

        await client.DiscoverMoviesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters { OriginCountries = ["BE"] },
            CancellationToken.None);

        var query = QueryString.Parse(Assert.Single(handler.Requests).Uri);
        Assert.Equal("BE", query["with_origin_country"]);
        Assert.False(query.ContainsKey("with_original_language"));
        Assert.False(query.ContainsKey("with_runtime.gte"));
    }


    [Fact]
    public async Task GetMovieDetailsAsync_ReadsVideosAndExternalIds()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("tmdb/movie-details-with-videos.json")));
        var client = CreateTmdbClient(handler, maxPages: 5);

        var details = await client.GetMovieDetailsAsync(200, CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal(102, details.Runtime);
        Assert.Equal("tt0000200", details.ExternalIds?.ImdbId);
        Assert.Equal("feature-teaser", details.Videos?.Results.Single().Key);
    }

    [Fact]
    public async Task SearchKeywordsAsync_UsesKeywordSearchEndpointAndCaches()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""
            {
              "page": 1,
              "total_pages": 1,
              "results": [
                { "id": 900, "name": "crime" }
              ]
            }
            """));
        var client = CreateTmdbClient(handler, maxPages: 5);

        var first = await client.SearchKeywordsAsync("crime", CancellationToken.None);
        var second = await client.SearchKeywordsAsync("crime", CancellationToken.None);

        var keyword = Assert.Single(first);
        Assert.Equal(900, keyword.Id);
        Assert.Equal(keyword.Id, Assert.Single(second).Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/3/search/keyword", request.Uri.AbsolutePath);
        Assert.Equal("crime", QueryString.Parse(request.Uri)["query"]);
    }

    [Fact]
    public async Task GetTvDetailsAsync_ReturnsNullForNotFound()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{}", HttpStatusCode.NotFound));
        var client = CreateTmdbClient(handler, maxPages: 5);

        var details = await client.GetTvDetailsAsync(404, CancellationToken.None);

        Assert.Null(details);
    }

    [Fact]
    public async Task FindTmdbIdByExternalIdAsync_UsesFindEndpointAndCachesMapping()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json("""{"movie_results":[{"id":200}],"tv_results":[{"id":100}]}"""));
        var client = CreateTmdbClient(handler, maxPages: 5);

        var first = await client.FindTmdbIdByExternalIdAsync(
            PremiereCalendar.Models.PremiereMediaType.Series,
            "81189",
            "tvdb_id",
            CancellationToken.None);
        var second = await client.FindTmdbIdByExternalIdAsync(
            PremiereCalendar.Models.PremiereMediaType.Series,
            "81189",
            "tvdb_id",
            CancellationToken.None);

        Assert.Equal(100, first);
        Assert.Equal(first, second);
        Assert.Single(handler.Requests);
        Assert.EndsWith("/find/81189", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("tvdb_id", QueryString.Parse(handler.Requests[0].Uri)["external_source"]);
    }

    [Fact]
    public async Task FilterCatalogMethods_ReadOfficialTmdbValueEndpoints()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/genre/movie/list"))
            {
                return StubHttpMessageHandler.Json("""{"genres":[{"id":28,"name":"Action"}]}""");
            }

            if (path.EndsWith("/configuration/languages"))
            {
                return StubHttpMessageHandler.Json("""[{"iso_639_1":"nl","english_name":"Dutch","name":"Nederlands"}]""");
            }

            if (path.EndsWith("/configuration/countries"))
            {
                return StubHttpMessageHandler.Json("""[{"iso_3166_1":"BE","english_name":"Belgium","native_name":"Belgie"}]""");
            }

            if (path.EndsWith("/watch/providers/movie"))
            {
                return StubHttpMessageHandler.Json("""{"results":[{"provider_id":337,"provider_name":"Disney Plus","display_priority":1}]}""");
            }

            if (path.EndsWith("/certification/tv/list"))
            {
                return StubHttpMessageHandler.Json("""{"certifications":{"BE":[{"certification":"KT","meaning":"All ages","order":1}]}}""");
            }

            return StubHttpMessageHandler.Json("{}", HttpStatusCode.NotFound);
        });
        var client = CreateTmdbClient(handler, maxPages: 5);

        var genres = await client.GetGenresAsync(PremiereCalendar.Models.PremiereMediaType.Movie, CancellationToken.None);
        var languages = await client.GetLanguagesAsync(CancellationToken.None);
        var countries = await client.GetCountriesAsync(CancellationToken.None);
        var providers = await client.GetWatchProvidersAsync(PremiereCalendar.Models.PremiereMediaType.Movie, "BE", CancellationToken.None);
        var certifications = await client.GetCertificationsAsync(PremiereCalendar.Models.PremiereMediaType.Series, CancellationToken.None);

        Assert.Equal("Action", Assert.Single(genres).Name);
        Assert.Equal("nl", Assert.Single(languages).Iso6391);
        Assert.Equal("BE", Assert.Single(countries).Iso31661);
        Assert.Equal("Disney Plus", Assert.Single(providers).ProviderName);
        Assert.Equal("KT", Assert.Single(certifications!.Certifications["BE"]).Certification);

        Assert.Equal("BE", QueryString.Parse(handler.Requests.Single(request => request.Uri.AbsolutePath.EndsWith("/watch/providers/movie")).Uri)["watch_region"]);
    }

    [Fact]
    public async Task GetChangedMovieIdsAsync_ReadsMovieChangesAndUsesCache()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.EndsWith("/movie/changes", request.RequestUri!.AbsolutePath);
            var query = QueryString.Parse(request.RequestUri);
            Assert.Equal("2026-05-01", query["start_date"]);
            Assert.Equal("2026-05-09", query["end_date"]);
            return StubHttpMessageHandler.Json("""{"page":1,"total_pages":1,"results":[{"id":10},{"id":20}]}""");
        });
        var client = CreateTmdbClient(handler, maxPages: 5);

        var first = await client.GetChangedMovieIdsAsync(
            new DateOnly(2026, 5, 1),
            new DateOnly(2026, 5, 9),
            CancellationToken.None);
        var second = await client.GetChangedMovieIdsAsync(
            new DateOnly(2026, 5, 1),
            new DateOnly(2026, 5, 9),
            CancellationToken.None);

        Assert.Equal([10, 20], first.Select(item => item.Id).ToArray());
        Assert.Equal([10, 20], second.Select(item => item.Id).ToArray());
        Assert.Single(handler.Requests);
    }

    private static TmdbClient CreateTmdbClient(
        StubHttpMessageHandler handler,
        int maxPages,
        int? maxUnfilteredPages = null,
        int pageBatchSize = 10)
    {
        return new TmdbClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions
            {
                BearerToken = "test-token",
                MaxPagesPerQuery = maxPages,
                MaxUnfilteredPagesPerQuery = maxUnfilteredPages ?? maxPages,
                PageBatchSize = pageBatchSize,
                PageFetchConcurrency = 3
            }),
            NullLogger<TmdbClient>.Instance);
    }
}
