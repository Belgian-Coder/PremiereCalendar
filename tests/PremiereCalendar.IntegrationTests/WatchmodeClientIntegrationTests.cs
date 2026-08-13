using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class WatchmodeClientIntegrationTests
{
    [Fact]
    public async Task GetTitleSourcesAsync_MapsTmdbIdAndReadsRegionalSources()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Watchmode request URI is missing.");
            AssertUsesApiKeyHeader(request);

            if (uri.AbsolutePath.EndsWith("/search/", StringComparison.Ordinal))
            {
                var query = QueryString.Parse(uri);
                Assert.Equal("tmdb_tv_id", query["search_field"]);
                Assert.Equal("1396", query["search_value"]);

                return StubHttpMessageHandler.Json(
                    """
                    {
                      "title_results": [
                        {
                          "id": 3173903,
                          "name": "Breaking Bad",
                          "type": "tv_series",
                          "tmdb_id": 1396,
                          "tmdb_type": "tv",
                          "imdb_id": "tt0903747"
                        }
                      ],
                      "people_results": []
                    }
                    """);
            }

            Assert.EndsWith("/title/3173903/sources/", uri.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal("BE,NL", QueryString.Parse(uri)["regions"]);
            return StubHttpMessageHandler.Json(
                """
                [
                  { "source_id": 203, "name": "Netflix", "type": "sub", "region": "BE" },
                  { "source_id": 371, "name": "AppleTV+", "type": "buy", "region": "NL" }
                ]
                """);
        });
        var client = CreateClient(handler);

        var sources = await client.GetTitleSourcesAsync(
            PremiereMediaType.Series,
            1396,
            "tt0903747",
            ["BE", "NL"],
            CancellationToken.None);

        Assert.Equal(["Netflix", "AppleTV+"], sources.Select(source => source.Name).ToArray());
        Assert.Contains(sources, source => source is { Name: "Netflix", Id: 203, Kind: "flatrate" });
        Assert.Contains(sources, source => source is { Name: "AppleTV+", Id: 371, Kind: "buy" });
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetReleaseCandidatesAsync_ReadsRecentReleasesAsCalendarCandidates()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Watchmode request URI is missing.");
            AssertUsesApiKeyHeader(request);

            Assert.EndsWith("/releases/", uri.AbsolutePath, StringComparison.Ordinal);
            var query = QueryString.Parse(uri);
            Assert.Equal("20260504", query["start_date"]);
            Assert.Equal("20260510", query["end_date"]);
            Assert.Equal("250", query["limit"]);

            return StubHttpMessageHandler.Json(
                """
                {
                  "releases": [
                    {
                      "id": 3165490,
                      "title": "Slow Horses",
                      "type": "tv_series",
                      "tmdb_id": 95480,
                      "tmdb_type": "tv",
                      "imdb_id": "tt5875444",
                      "season_number": 5,
                      "source_release_date": "2026-05-05",
                      "source_id": 371,
                      "source_name": "AppleTV+",
                      "is_original": 1
                    },
                    {
                      "id": 1234,
                      "title": "New Movie",
                      "type": "movie",
                      "tmdb_id": 222,
                      "tmdb_type": "movie",
                      "imdb_id": "tt0000222",
                      "source_release_date": "2026-05-06",
                      "source_id": 203,
                      "source_name": "Netflix"
                    }
                  ]
                }
                """);
        });
        var client = CreateClient(handler);

        var candidates = await client.GetReleaseCandidatesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        Assert.Contains(candidates, candidate => candidate is
        {
            MediaType: PremiereMediaType.Series,
            TmdbId: 95480,
            PremiereDate: { Year: 2026, Month: 5, Day: 5 },
            Source: "AppleTV+"
        });
        Assert.Contains(candidates, candidate => candidate is
        {
            MediaType: PremiereMediaType.Movie,
            TmdbId: 222,
            PremiereDate: { Year: 2026, Month: 5, Day: 6 },
            Source: "Netflix"
        });
    }

    [Fact]
    public async Task GetTitleSourcesAsync_LeavesRateLimitRetriesToSharedProviderPolicy()
    {
        var attempts = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/search/", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Json(
                    """
                    { "title_results": [ { "id": 3173903, "name": "Breaking Bad", "type": "tv_series", "tmdb_id": 1396, "tmdb_type": "tv" } ] }
                    """);
            }

            attempts++;
            if (attempts == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return response;
            }

            return StubHttpMessageHandler.Json(
                """
                [ { "source_id": 203, "name": "Netflix", "type": "sub", "region": "BE" } ]
                """);
        });
        var client = CreateClient(handler);

        var sources = await client.GetTitleSourcesAsync(
            PremiereMediaType.Series,
            1396,
            null,
            ["BE"],
            CancellationToken.None);

        Assert.Empty(sources);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetTitleSourcesAsync_RetriesRegionsIndividuallyWhenCombinedRegionRequestFails()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Watchmode request URI is missing.");
            if (uri.AbsolutePath.EndsWith("/search/", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Json(
                    """
                    { "title_results": [ { "id": 3173903, "name": "Breaking Bad", "type": "tv_series", "tmdb_id": 1396, "tmdb_type": "tv" } ] }
                    """);
            }

            var regions = QueryString.Parse(uri)["regions"];
            return regions switch
            {
                "BE,NL" => StubHttpMessageHandler.Json(
                    """{"success":false,"statusCode":400,"statusMessage":"NL is not enabled for your current plan."}""",
                    HttpStatusCode.BadRequest),
                "BE" => StubHttpMessageHandler.Json(
                    """[ { "source_id": 203, "name": "Netflix", "type": "sub", "region": "BE" } ]"""),
                "NL" => StubHttpMessageHandler.Json(
                    """{"success":false,"statusCode":400,"statusMessage":"NL is not enabled for your current plan."}""",
                    HttpStatusCode.BadRequest),
                _ => throw new InvalidOperationException($"Unexpected regions query: {regions}")
            };
        });
        var client = CreateClient(handler);

        var sources = await client.GetTitleSourcesAsync(
            PremiereMediaType.Series,
            1396,
            null,
            ["BE", "NL"],
            CancellationToken.None);

        var source = Assert.Single(sources);
        Assert.Equal("Netflix", source.Name);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task GetTitleSourcesAsync_DoesNotCacheTransientSourceTimeoutAsEmptyResult()
    {
        var sourceAttempts = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Watchmode request URI is missing.");
            if (uri.AbsolutePath.EndsWith("/search/", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Json(
                    """
                    { "title_results": [ { "id": 3173903, "name": "Breaking Bad", "type": "tv_series", "tmdb_id": 1396, "tmdb_type": "tv" } ] }
                    """);
            }

            sourceAttempts++;
            if (sourceAttempts == 1)
            {
                throw new TaskCanceledException("Simulated source timeout.");
            }

            return StubHttpMessageHandler.Json(
                """
                [ { "source_id": 203, "name": "Netflix", "type": "sub", "region": "BE" } ]
                """);
        });
        var client = CreateClient(handler);

        var timedOutSources = await client.GetTitleSourcesAsync(
            PremiereMediaType.Series,
            1396,
            null,
            ["BE"],
            CancellationToken.None);
        var recoveredSources = await client.GetTitleSourcesAsync(
            PremiereMediaType.Series,
            1396,
            null,
            ["BE"],
            CancellationToken.None);

        Assert.Empty(timedOutSources);
        Assert.Equal("Netflix", Assert.Single(recoveredSources).Name);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task GetReleaseCandidatesAsync_SkipsRequestsWhenApiKeyIsMissing()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Watchmode should not be called without an API key."));
        var client = CreateClient(handler, apiKey: "");

        var candidates = await client.GetReleaseCandidatesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        Assert.Empty(candidates);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetReleaseCandidatesAsync_ReturnsEmptyWhenHttpClientTimesOutWithoutCallerCancellation()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException("Simulated HTTP timeout."));
        var client = CreateClient(handler);

        var candidates = await client.GetReleaseCandidatesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        Assert.Empty(candidates);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetReleaseCandidatesAsync_ShortCircuitsLongRateLimitRetryAfter()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return response;
        });
        var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var candidates = await client.GetReleaseCandidatesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            cancellation.Token);

        Assert.Empty(candidates);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetReleaseCandidatesAsync_SkipsRequestsDuringRateLimitBackoff()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return response;
        });
        var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var firstCandidates = await client.GetReleaseCandidatesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            cancellation.Token);
        var secondCandidates = await client.GetReleaseCandidatesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        Assert.Empty(firstCandidates);
        Assert.Empty(secondCandidates);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetReleaseCandidatesAsync_DoesNotCacheTransientTimeoutAsEmptyResult()
    {
        var attempts = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new TaskCanceledException("Simulated HTTP timeout.");
            }

            return StubHttpMessageHandler.Json(
                """
                {
                  "releases": [
                    {
                      "id": 22,
                      "title": "Recovered Release",
                      "type": "movie",
                      "tmdb_id": 222,
                      "tmdb_type": "movie",
                      "source_release_date": "2026-05-06",
                      "source_name": "Netflix"
                    }
                  ]
                }
                """);
        });
        var client = CreateClient(handler);
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);

        var timeoutCandidates = await client.GetReleaseCandidatesAsync(start, end, CancellationToken.None);
        var recoveredCandidates = await client.GetReleaseCandidatesAsync(start, end, CancellationToken.None);

        Assert.Empty(timeoutCandidates);
        Assert.Equal("Recovered Release", Assert.Single(recoveredCandidates).Title);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetTitleSourcesAsync_CapsOptionalAvailabilityEnrichmentBudget()
    {
        var client = new WatchmodeClient(
            new HttpClient(new DelayedHandler(TimeSpan.FromSeconds(5)))
            {
                BaseAddress = new Uri("https://api.watchmode.com/v1/")
            },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new WatchmodeOptions
            {
                Enabled = true,
                ApiKey = "test-watchmode-key",
                Regions = ["BE"],
                EnableAvailabilityEnrichment = true,
                RequestTimeoutSeconds = 20,
                AvailabilityEnrichmentBudgetSeconds = 1
            }));

        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetTitleSourcesAsync(
            PremiereMediaType.Movie,
            42,
            null,
            ["BE"],
            CancellationToken.None));

        Assert.InRange(elapsed.ElapsedMilliseconds, 500, 2_500);
    }

    private static WatchmodeClient CreateClient(StubHttpMessageHandler handler, string apiKey = "test-watchmode-key")
    {
        return new WatchmodeClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.watchmode.com/v1/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new WatchmodeOptions
            {
                Enabled = true,
                ApiKey = apiKey,
                Regions = ["BE", "NL"],
                EnableReleaseDiscovery = true,
                EnableAvailabilityEnrichment = true
            }));
    }

    private static void AssertUsesApiKeyHeader(HttpRequestMessage request)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Watchmode request URI is missing.");
        var query = QueryString.Parse(uri);

        Assert.DoesNotContain("apiKey", query.Keys);
        Assert.True(request.Headers.TryGetValues("X-API-Key", out var values));
        Assert.Equal("test-watchmode-key", Assert.Single(values));
    }

    private sealed class DelayedHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return StubHttpMessageHandler.Json("{}");
        }
    }
}
