using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class RefreshProviderReuseIntegrationTests
{
    [Fact]
    public async Task ForcedVerifiedAbsentRatingsAreFetchedOncePerGeneration()
    {
        using var rtCache = new MemoryCache(new MemoryCacheOptions());
        var rtHandler = new StubHttpMessageHandler(_ => Html("<div class=\"js-search-no-results search__no-results\">Sorry, no results found for &quot;Example&quot;</div>"));
        var rt = RottenTomatoes(rtHandler, rtCache);
        using var omdbCache = new MemoryCache(new MemoryCacheOptions());
        var omdbHandler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json("""{"Response":"False","Error":"Movie not found!"}"""));
        var omdb = Omdb(omdbHandler, omdbCache);
        for (var generation = 0; generation < 2; generation++)
        {
            using var scope = ProviderRefreshScope.Use(ProviderRefreshScope.Create());
            for (var pass = 0; pass < 3; pass++)
            {
                Assert.False((await rt.GetScoresAsync(PremiereMediaType.Movie, "Missing", 2026, null,
                    CancellationToken.None, true)).HasAnyScore);
                Assert.Equal("False", (await omdb.GetByImdbIdAsync("tt0000001", CancellationToken.None, true))!.Response);
            }
            Assert.Equal(generation + 1, rtHandler.Requests.Count);
            Assert.Equal(generation + 1, omdbHandler.Requests.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRottenTomatoesLookupIsNotCachedAsVerifiedAbsence(bool force)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Html("<div class=\"js-search-no-results search__no-results\">Sorry, no results found for &quot;Example&quot;</div>"));
        var client = RottenTomatoes(handler, cache);
        using var scope = ProviderRefreshScope.Use(ProviderRefreshScope.Create());
        await client.GetScoresAsync(PremiereMediaType.Movie, "Missing", 2026, null, CancellationToken.None, force);
        Assert.False(client.TryGetCachedScores(PremiereMediaType.Movie, "Missing", 2026, null, out _));
        Assert.Null(await client.TryGetCachedScoresAsync(PremiereMediaType.Movie, "Missing", 2026, null, CancellationToken.None));
        await client.GetScoresAsync(PremiereMediaType.Movie, "Missing", 2026, null, CancellationToken.None, force);
        await client.GetScoresAsync(PremiereMediaType.Movie, "Missing", 2026, null, CancellationToken.None, force);
        Assert.Equal(2, calls);
        Assert.True(client.TryGetCachedScores(PremiereMediaType.Movie, "Missing", 2026, null, out var empty));
        Assert.False(empty.HasAnyScore);
    }

    [Fact]
    public async Task OrdinaryScopedResultCannotHideAnotherJobsForcedPublication()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var score = 72;
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath == "/search"
            ? Html($"""<search-page-media-row release-year="2026" tomatometer-score="{score}"><a href="https://www.rottentomatoes.com/m/example" data-qa="info-name" slot="title">Example</a></search-page-media-row>""")
            : Html("<script id=\"media-scorecard-json\" type=\"application/json\">{\"criticsScore\":{\"score\":\"" + score + "\"}}</script>"));
        var client = RottenTomatoes(handler, cache);
        var original = ProviderRefreshScope.Create();
        using (ProviderRefreshScope.Use(original))
            Assert.Equal(72, (await client.GetScoresAsync(PremiereMediaType.Movie, "Example", 2026, null, CancellationToken.None)).CriticScore);
        score = 96;
        await client.GetScoresAsync(PremiereMediaType.Movie, "Example", 2026, null, CancellationToken.None, true);
        var requests = handler.Requests.Count;
        using (ProviderRefreshScope.Use(original))
            Assert.Equal(96, (await client.GetScoresAsync(PremiereMediaType.Movie, "Example", 2026, null, CancellationToken.None)).CriticScore);
        Assert.Equal(requests, handler.Requests.Count);
    }

    [Fact]
    public async Task ForcedDetailsWithNoExternalIdsAreReusedButNewGenerationFetchesAgain()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            """{"id":100,"title":"Example","external_ids":{}}"""));
        var client = new TmdbClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") },
            cache, Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "fixture" }),
            NullLogger<TmdbClient>.Instance,
            new TmdbRequestLimiter(Microsoft.Extensions.Options.Options.Create(new TmdbOptions())));
        for (var generation = 0; generation < 2; generation++)
        {
            using var scope = ProviderRefreshScope.Use(ProviderRefreshScope.Create());
            var first = await client.GetMovieDetailsAsync(100, CancellationToken.None, true);
            var again = await client.GetMovieDetailsAsync(100, CancellationToken.None, true);
            Assert.Same(first, again);
            Assert.Equal(generation + 1, handler.Requests.Count);
        }
    }

    private static RottenTomatoesClient RottenTomatoes(HttpMessageHandler handler, IMemoryCache cache)
        => new(new HttpClient(handler) { BaseAddress = new Uri("https://www.rottentomatoes.com/") }, cache,
            Microsoft.Extensions.Options.Options.Create(new RottenTomatoesOptions { Enabled = true }),
            NullLogger<RottenTomatoesClient>.Instance);

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Response\":\"unexpected\"}")]
    public async Task MalformedOmdbSuccessRemainsRetryableWithinForcedGeneration(string malformed)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(++calls == 1
            ? malformed : """{"Response":"True","imdbRating":"8.0"}"""));
        var client = Omdb(handler, cache);
        using var scope = ProviderRefreshScope.Use(ProviderRefreshScope.Create());
        await client.GetByImdbIdAsync("tt0000001", CancellationToken.None, true);
        var valid = await client.GetByImdbIdAsync("tt0000001", CancellationToken.None, true);
        Assert.Equal("8.0", valid!.ImdbRating);
        await client.GetByImdbIdAsync("tt0000001", CancellationToken.None, true);
        Assert.Equal(2, calls);
    }

    private static OmdbClient Omdb(HttpMessageHandler handler, IMemoryCache cache)
        => new(new HttpClient(handler) { BaseAddress = new Uri("https://www.omdbapi.com/") }, cache,
            Microsoft.Extensions.Options.Options.Create(new OmdbOptions { Enabled = true, ApiKey = "fixture" }));

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK)
        { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") };
}
