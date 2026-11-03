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
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath == "/search" ? Html(
            """
            <search-page-media-row release-year="2026" tomatometer-score="72">
              <a href="https://www.rottentomatoes.com/m/saccharine" data-qa="info-name" slot="title">Saccharine</a>
            </search-page-media-row>
            """) : Html("<script id=\"media-scorecard-json\">{}</script>"));
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

        var outcome = await client.GetScoresWithStatusAsync(
            PremiereMediaType.Series,
            "The Boroughs",
            2026,
            wikidataId: null,
            CancellationToken.None);

        Assert.True(outcome.Successful);
        Assert.Equal(95, outcome.Value.CriticScore);
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

        var outcome = await client.GetScoresWithStatusAsync(
            PremiereMediaType.Movie,
            "Saccharine",
            2026,
            wikidataId: null,
            CancellationToken.None);

        Assert.True(outcome.Successful);
        Assert.Equal(72, outcome.Value.CriticScore);
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

    [Fact]
    public async Task AccessDenialIsReusedAcrossTitlesAndForcedRequests()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var identifiers = new CountingWikimediaClient();
        var client = new RottenTomatoesClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://www.rottentomatoes.com/") },
            new MemoryCache(new MemoryCacheOptions()), Microsoft.Extensions.Options.Options.Create(new RottenTomatoesOptions()),
            NullLogger<RottenTomatoesClient>.Instance, identifiers);
        var first = await client.GetScoresWithStatusAsync(PremiereMediaType.Movie, "First", 2026, "QFirst", CancellationToken.None);
        var second = await client.GetScoresWithStatusAsync(PremiereMediaType.Movie, "Second", 2026, "QSecond", CancellationToken.None, forceRefresh: true);
        Assert.False(first.Successful);
        Assert.Equal("authentication_denied", first.Failure?.Reason);
        Assert.False(second.Successful);
        Assert.Equal("local_backoff", second.Failure?.Reason);
        Assert.Single(handler.Requests);
        Assert.Equal(1, identifiers.Calls);
    }

    [Fact]
    public async Task FailedSearchIsNotMemoizedForTheFullRatingCacheLifetime()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Html("<div class=\"js-search-no-results search__no-results\">Sorry, no results found for &quot;Example&quot;</div>"));
        var client = CreateClient(handler);
        var first = await client.GetScoresWithStatusAsync(PremiereMediaType.Movie, "First", 2026, null, CancellationToken.None);
        var second = await client.GetScoresWithStatusAsync(PremiereMediaType.Movie, "First", 2026, null, CancellationToken.None);
        Assert.False(first.Successful);
        Assert.Equal("http_failure", first.Failure?.Reason);
        Assert.True(second.Successful);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("<html><title>Just a moment...</title>Verify you are human</html>")]
    [InlineData("<html>Something went wrong</html>")]
    [InlineData("<search-page-media-row></search-page-media-row>")]
    [InlineData("<search-page-result type=\"movie\"></search-page-result>")]
    public async Task UnrecognizedSearch200IsFailureWhileRecognizedEmptySearchIsVerified(string document)
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ => Html(++calls == 1 ? document
            : "<div class=\"js-search-no-results search__no-results\">Sorry, no results found for &quot;Example&quot;</div>"));
        var client = CreateClient(handler);
        var failed = await client.GetScoresWithStatusAsync(PremiereMediaType.Movie, "Example", 2026, null, CancellationToken.None);
        var verified = await client.GetScoresWithStatusAsync(PremiereMediaType.Movie, "Example", 2026, null, CancellationToken.None);
        Assert.False(failed.Successful);
        Assert.Equal("invalid_response", failed.Failure?.Reason);
        Assert.True(verified.Successful);
        Assert.False(verified.Value.HasAnyScore);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CurrentPublicNoResultMarkupIsVerifiedAndNonLatinLookupIsNotApplicable()
    {
        // Minimal structure observed on the public RT search page on 3 November 2026.
        var handler = new StubHttpMessageHandler(_ => Html("<div class=\"js-search-no-results search__no-results\"><rt-text>Sorry, no results found for &quot;Example&quot;</rt-text></div>"));
        var client = CreateClient(handler);
        var absent = await client.GetScoresWithStatusAsync(PremiereMediaType.Movie, "Example", 2026, null, CancellationToken.None);
        Assert.True(absent.Successful);
        Assert.False(absent.Value.HasAnyScore);
        var skipped = await client.GetScoresWithStatusAsync(PremiereMediaType.Movie, "\u65e5\u672c\u8a9e", 2026, null, CancellationToken.None);
        Assert.True(skipped.Successful);
        Assert.Equal("not_applicable", skipped.Failure?.Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ScoreRefreshCanKeepStableIdentifierCacheWhileExplicitIdentityRefreshBypassesIt()
    {
        var identifiers = new CountingWikimediaClient();
        var client = new RottenTomatoesClient(
            new HttpClient(new StubHttpMessageHandler(_ => Html("<script id=\"media-scorecard-json\">{\"criticsScore\":{\"score\":83}}</script>")))
                { BaseAddress = new Uri("https://www.rottentomatoes.com/") },
            new MemoryCache(new MemoryCacheOptions()), Microsoft.Extensions.Options.Options.Create(new RottenTomatoesOptions()),
            NullLogger<RottenTomatoesClient>.Instance, identifiers);
        Assert.True((await client.GetScoresWithRefreshPolicyAsync(PremiereMediaType.Movie, "Example", 2026, "QExample", CancellationToken.None, true, false)).Successful);
        Assert.True((await client.GetScoresWithRefreshPolicyAsync(PremiereMediaType.Movie, "Example", 2026, "QExample", CancellationToken.None, true, true)).Successful);
        Assert.Equal([false, true], identifiers.Forced);
    }

    [Fact]
    public async Task DisabledIdentifierProviderDoesNotBlockVerifiedTitleSearchRatings()
    {
        var identifierHandler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Disabled Wikimedia must not send requests."));
        var identifiers = new WikimediaClient(new HttpClient(identifierHandler), new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new WikimediaOptions { Enabled = false }));
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath == "/search"
            ? Html("<search-page-media-row release-year=\"2026\" tomatometer-score=\"83\"><a href=\"/m/example\" data-qa=\"info-name\">Example</a></search-page-media-row>")
            : Html("<script id=\"media-scorecard-json\">{}</script>"));
        var client = new RottenTomatoesClient(new HttpClient(handler) { BaseAddress = new Uri("https://www.rottentomatoes.com/") },
            new MemoryCache(new MemoryCacheOptions()), Microsoft.Extensions.Options.Options.Create(new RottenTomatoesOptions()),
            NullLogger<RottenTomatoesClient>.Instance, identifiers);
        var result = await client.GetScoresWithStatusAsync(PremiereMediaType.Movie, "Example", 2026, "QExample", CancellationToken.None);
        Assert.True(result.Successful);
        Assert.Equal(83, result.Value.CriticScore);
        Assert.Empty(identifierHandler.Requests);
    }

    private sealed class CountingWikimediaClient : IWikimediaClient
    {
        public int Calls { get; private set; }
        public List<bool> Forced { get; } = [];
        public Task<string?> GetReusableImageUrlAsync(string id, CancellationToken token, bool forceRefresh = false)
            => Task.FromResult<string?>(null);
        public Task<string?> GetRottenTomatoesIdAsync(string id, CancellationToken token, bool forceRefresh = false)
        {
            Calls++;
            Forced.Add(forceRefresh);
            return Task.FromResult<string?>("m/example");
        }
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
