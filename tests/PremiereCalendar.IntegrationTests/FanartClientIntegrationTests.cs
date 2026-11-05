using Microsoft.Extensions.Caching.Memory;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class FanartClientIntegrationTests
{
    [Fact]
    public async Task GetMovieArtworkAsync_ReadsFanartMoviePostersAndCachesResponse()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(
                """
                {
                  "movieposter": [
                    { "url": "https://assets.fanart.tv/fanart/movie/poster.jpg", "lang": "en", "likes": "10" }
                  ]
                }
                """));
        var client = CreateClient(handler);

        var first = await client.GetMovieArtworkAsync(200, CancellationToken.None);
        var second = await client.GetMovieArtworkAsync(200, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal("https://assets.fanart.tv/fanart/movie/poster.jpg", Assert.Single(first.MoviePosters).Url);
        Assert.Single(handler.Requests);
        Assert.EndsWith("/movies/200", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("test-key", QueryString.Parse(handler.Requests[0].Uri)["api_key"]);
    }

    [Fact]
    public async Task GetTvArtworkAsync_ReadsFanartTvPosters()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(
                """
                {
                  "tvposter": [
                    { "url": "https://assets.fanart.tv/fanart/tv/poster.jpg", "lang": "nl", "likes": 4 }
                  ]
                }
                """));
        var client = CreateClient(handler);

        var artwork = await client.GetTvArtworkAsync(81189, CancellationToken.None);

        Assert.NotNull(artwork);
        Assert.Equal("https://assets.fanart.tv/fanart/tv/poster.jpg", Assert.Single(artwork.TvPosters).Url);
        Assert.EndsWith("/tv/81189", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task GetMovieArtworkAsync_ReturnsNullWhenHttpClientTimesOutWithoutCallerCancellation()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException("Simulated HTTP timeout."));
        var client = CreateClient(handler);

        var artwork = await client.GetMovieArtworkAsync(200, CancellationToken.None);

        Assert.Null(artwork);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, "authentication_denied")]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "authentication_denied")]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, "rate_limited")]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, "http_failure")]
    public async Task ProviderFailuresRemainUnsuccessfulThroughArtworkAdapter(System.Net.HttpStatusCode status, string reason)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status));
        var adapter = new FanartArtworkProvider(CreateClient(handler));
        var result = await adapter.GetArtworkWithStatusAsync(new(PremiereCalendar.Models.PremiereMediaType.Movie, 42, null, null, null, "Example"), CancellationToken.None, true);
        Assert.False(result.Successful);
        Assert.Null(result.Value);
        Assert.Equal(reason, result.Failure?.Reason);
        if (status != System.Net.HttpStatusCode.ServiceUnavailable)
        {
            var deferred = await adapter.GetArtworkWithStatusAsync(new(PremiereCalendar.Models.PremiereMediaType.Movie, 43, null, null, null, "Other"), CancellationToken.None, true);
            Assert.False(deferred.Successful);
            Assert.Equal("local_backoff", deferred.Failure?.Reason);
            Assert.Single(handler.Requests);
        }
    }

    [Fact]
    public async Task Verified404IsCachedButMalformed200AndTimeoutAreNotVerifiedAbsence()
    {
        var missingHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        var missing = CreateClient(missingHandler);
        Assert.True((await missing.GetMovieArtworkWithStatusAsync(42, CancellationToken.None)).Successful);
        Assert.True((await missing.GetMovieArtworkWithStatusAsync(42, CancellationToken.None)).Successful);
        Assert.Single(missingHandler.Requests);
        var malformed = CreateClient(new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{\"unexpected\":true}")));
        Assert.False((await malformed.GetMovieArtworkWithStatusAsync(42, CancellationToken.None)).Successful);
        var timeout = CreateClient(new StubHttpMessageHandler(_ => throw new TaskCanceledException()));
        var failed = await timeout.GetMovieArtworkWithStatusAsync(42, CancellationToken.None);
        Assert.False(failed.Successful);
        Assert.Equal("timeout", failed.Failure?.Reason);
    }

    [Theory]
    [InlineData("{\"tmdb_id\":\"42\",\"hdmovielogo\":[{\"url\":\"https://images.example/logo.png\"}]}")]
    [InlineData("{\"name\":\"Posterless title\",\"hdmovielogo\":[]}")]
    public async Task IdentityResponseWithoutPosterCategoriesIsVerifiedEmptyAndCached(string payload)
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(payload));
        var client = CreateClient(handler);
        var first = await client.GetMovieArtworkWithStatusAsync(42, CancellationToken.None);
        var second = await client.GetMovieArtworkWithStatusAsync(42, CancellationToken.None);
        Assert.True(first.Successful);
        Assert.True(second.Successful);
        Assert.NotNull(first.Value);
        Assert.Empty(first.Value.MoviePosters);
        Assert.Empty(first.Value.MovieBackgrounds);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TvIdentityWithoutWantedArtAndUnconfiguredClientAreIntentionalSuccessfulOutcomes()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("{\"thetvdb_id\":82,\"hdtvlogo\":[]}"));
        var result = await CreateClient(handler).GetTvArtworkWithStatusAsync(82, CancellationToken.None);
        Assert.True(result.Successful);
        Assert.Empty(result.Value!.TvPosters);
        var unused = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Unconfigured provider must not send."));
        var skipped = await CreateClient(unused, " ").GetMovieArtworkWithStatusAsync(42, CancellationToken.None, true);
        Assert.True(skipped.Successful);
        Assert.Equal("not_configured", skipped.Failure?.Reason);
        Assert.Null(skipped.Failure?.RetryAtUtc);
        Assert.Empty(unused.Requests);
    }

    private static FanartClient CreateClient(StubHttpMessageHandler handler, string apiKey = "test-key")
    {
        return new FanartClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://webservice.fanart.tv/v3/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new FanartOptions
            {
                Enabled = true,
                ApiKey = apiKey
            }));
    }
}
