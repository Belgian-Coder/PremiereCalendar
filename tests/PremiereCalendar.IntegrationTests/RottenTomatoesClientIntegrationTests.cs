using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class RottenTomatoesClientIntegrationTests
{
    [Fact]
    public async Task TryGetCachedScores_ReturnsOnlyPreviouslyFetchedScoresWithoutAnotherRequest()
    {
        var handler = new StubHttpMessageHandler(_ => Html(
            """
            <search-page-media-row release-year="2026" tomatometer-score="72">
              <a href="https://www.rottentomatoes.com/m/saccharine" data-qa="info-name" slot="title">Saccharine</a>
            </search-page-media-row>
            """));
        var client = CreateClient(handler);

        Assert.False(client.TryGetCachedScores(
            PremiereMediaType.Movie,
            "Saccharine",
            2026,
            wikidataId: null,
            out _));

        await client.GetScoresAsync(
            PremiereMediaType.Movie,
            "Saccharine",
            2026,
            wikidataId: null,
            CancellationToken.None);
        var requestCountAfterFetch = handler.Requests.Count;

        Assert.True(client.TryGetCachedScores(
            PremiereMediaType.Movie,
            "Saccharine",
            2026,
            wikidataId: null,
            out var cachedScores));
        Assert.Equal(72, cachedScores.CriticScore);
        Assert.Equal(requestCountAfterFetch, handler.Requests.Count);
    }

    [Fact]
    public async Task GetScoresAsync_FetchesAudienceScoreWhenSearchRowAlreadyHasTomatometerScore()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/search" => Html(
                    """
                    <search-page-media-row startyear="2026" tomatometerscore="95">
                      <a href="https://www.rottentomatoes.com/tv/the_boroughs" data-qa="info-name" slot="title">The Boroughs</a>
                    </search-page-media-row>
                    """),
                "/tv/the_boroughs" => Html(
                    """
                    <script id="media-scorecard-json" type="application/json">
                    {"criticsScore":{"score":"95","scorePercent":"95%"},"audienceScore":{"score":"85","scorePercent":"85%","bandedRatingCount":"50+ Ratings"}}
                    </script>
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var client = CreateClient(handler);

        var scores = await client.GetScoresAsync(
            PremiereMediaType.Series,
            "The Boroughs",
            2026,
            wikidataId: null,
            CancellationToken.None);

        Assert.Equal(95, scores.CriticScore);
        Assert.Equal(85, scores.AudienceScore);
        Assert.Equal(["/search", "/tv/the_boroughs"], handler.Requests.Select(request => request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task GetScoresAsync_FetchesAudienceScoreFromScoredSearchRowWithoutYear()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/search" => Html(
                    """
                    <search-page-media-row tomatometerscore="100">
                      <a href="https://www.rottentomatoes.com/tv/kylie" data-qa="info-name" slot="title">Kylie</a>
                    </search-page-media-row>
                    """),
                "/tv/kylie" => Html(
                    """
                    <script id="media-scorecard-json" type="application/json">
                    {"criticsScore":{"score":"100","scorePercent":"100%"},"audienceScore":{"score":"97","scorePercent":"97%","bandedRatingCount":"Fewer than 50 Ratings"}}
                    </script>
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var client = CreateClient(handler);

        var scores = await client.GetScoresAsync(
            PremiereMediaType.Series,
            "Kylie",
            2026,
            wikidataId: null,
            CancellationToken.None);

        Assert.Equal(100, scores.CriticScore);
        Assert.Equal(97, scores.AudienceScore);
        Assert.Equal(["/search", "/tv/kylie"], handler.Requests.Select(request => request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task GetTomatometerScoreAsync_UsesExactSearchRowScore()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/search" => Html(
                    """
                    <search-page-media-row startyear="2026" tomatometerscore="95">
                      <a href="https://www.rottentomatoes.com/tv/the_boroughs" data-qa="info-name" slot="title">The Boroughs</a>
                    </search-page-media-row>
                    """),
                "/tv/the_boroughs" => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var client = CreateClient(handler);

        var score = await client.GetTomatometerScoreAsync(
            PremiereMediaType.Series,
            "The Boroughs",
            2026,
            wikidataId: null,
            CancellationToken.None);

        Assert.Equal(95, score);
        Assert.Equal(["/search", "/tv/the_boroughs"], handler.Requests.Select(request => request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task GetTomatometerScoreAsync_FetchesDirectPageWhenSearchRowScoreIsMissingAndIdentifierContextExists()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/search" => Html(
                    """
                    <search-page-media-row release-year="2026" tomatometer-score="">
                      <a href="https://www.rottentomatoes.com/m/corporate_retreat" data-qa="info-name" slot="title">Corporate Retreat</a>
                    </search-page-media-row>
                    """),
                "/m/corporate_retreat" => Html(
                    """
                    <script id="media-scorecard-json" type="application/json">
                    {"criticsScore":{"likedCount":2,"notLikedCount":6,"ratingCount":8,"reviewCount":8,"title":"Tomatometer"}}
                    </script>
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var client = CreateClient(handler);

        var score = await client.GetTomatometerScoreAsync(
            PremiereMediaType.Movie,
            "Corporate Retreat",
            2026,
            wikidataId: "Q680",
            CancellationToken.None);

        Assert.Equal(25, score);
        Assert.Equal(["/search", "/m/corporate_retreat"], handler.Requests.Select(request => request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task GetTomatometerScoreAsync_UsesExactMovieSearchRowScoreWithoutIdentifierContext()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/search" => Html(
                    """
                    <search-page-media-row release-year="2026" tomatometer-score="72">
                      <a href="https://www.rottentomatoes.com/m/saccharine" data-qa="info-name" slot="title">Saccharine</a>
                    </search-page-media-row>
                    """),
                "/m/saccharine" => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var client = CreateClient(handler);

        var score = await client.GetTomatometerScoreAsync(
            PremiereMediaType.Movie,
            "Saccharine",
            2026,
            wikidataId: null,
            CancellationToken.None);

        Assert.Equal(72, score);
        Assert.Equal(["/search", "/m/saccharine"], handler.Requests.Select(request => request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task GetTomatometerScoreAsync_FetchesDirectMoviePageForExactYearMatchWithoutIdentifierContext()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/search" => Html(
                    """
                    <search-page-media-row release-year="2026" tomatometer-score="">
                      <a href="https://www.rottentomatoes.com/m/diamond_2026" data-qa="info-name" slot="title">Diamond</a>
                    </search-page-media-row>
                    """),
                "/m/diamond_2026" => Html(
                    """
                    <script id="media-scorecard-json" type="application/json">
                    {"criticsScore":{"scorePercent":"64%","title":"Tomatometer"}}
                    </script>
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var client = CreateClient(handler);

        var score = await client.GetTomatometerScoreAsync(
            PremiereMediaType.Movie,
            "Diamond",
            2026,
            wikidataId: null,
            CancellationToken.None);

        Assert.Equal(64, score);
        Assert.Equal(["/search", "/m/diamond_2026"], handler.Requests.Select(request => request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task GetTomatometerScoreAsync_RejectsWrongYearWhenMultipleExactTitleMatchesExist()
    {
        var handler = new StubHttpMessageHandler(_ => Html(
            """
            <search-page-media-row release-year="1986" tomatometer-score="">
              <a href="https://www.rottentomatoes.com/m/the_surfer" data-qa="info-name" slot="title">The Surfer</a>
            </search-page-media-row>
            <search-page-media-row release-year="2024" tomatometer-score="84">
              <a href="https://www.rottentomatoes.com/m/the_surfer_2024" data-qa="info-name" slot="title">The Surfer</a>
            </search-page-media-row>
            """));
        var client = CreateClient(handler);

        var score = await client.GetTomatometerScoreAsync(
            PremiereMediaType.Movie,
            "The Surfer",
            2026,
            wikidataId: "QSurfer",
            CancellationToken.None);

        Assert.Null(score);
        Assert.Single(handler.Requests);
    }

    private static RottenTomatoesClient CreateClient(StubHttpMessageHandler handler)
    {
        return new RottenTomatoesClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://www.rottentomatoes.com/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new RottenTomatoesOptions { Enabled = true, CacheHours = 12 }),
            NullLogger<RottenTomatoesClient>.Instance);
    }

    private static HttpResponseMessage Html(string html)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
        };
    }
}
