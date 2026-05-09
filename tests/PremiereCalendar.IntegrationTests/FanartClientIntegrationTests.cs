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

    private static FanartClient CreateClient(StubHttpMessageHandler handler)
    {
        return new FanartClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://webservice.fanart.tv/v3/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new FanartOptions
            {
                Enabled = true,
                ApiKey = "test-key"
            }));
    }
}
