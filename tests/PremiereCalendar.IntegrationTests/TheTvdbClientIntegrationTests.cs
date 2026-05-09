using Microsoft.Extensions.Caching.Memory;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class TheTvdbClientIntegrationTests
{
    [Fact]
    public async Task GetSeriesArtworkAsync_LogsInAndReadsSeriesArtwork()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                return StubHttpMessageHandler.Json("""{"data":{"token":"jwt-token"}}""");
            }

            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("jwt-token", request.Headers.Authorization?.Parameter);
            return StubHttpMessageHandler.Json(
                """
                {
                  "data": [
                    { "image": "series/poster.jpg", "type": 2, "language": "en", "score": 9.4 }
                  ]
                }
                """);
        });
        var client = CreateClient(handler);

        var artworks = await client.GetSeriesArtworkAsync(81189, CancellationToken.None);

        var artwork = Assert.Single(artworks);
        Assert.Equal("2", artwork.Type);
        Assert.Equal("series/poster.jpg", artwork.Image);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath.EndsWith("/login"));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Uri.AbsolutePath.EndsWith("/series/81189/artworks"));
    }

    [Fact]
    public async Task GetSeriesArtworkAsync_ReturnsEmptyWhenHttpClientTimesOutWithoutCallerCancellation()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException("Simulated HTTP timeout."));
        var client = CreateClient(handler);

        var artworks = await client.GetSeriesArtworkAsync(81189, CancellationToken.None);

        Assert.Empty(artworks);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetSeriesArtworkAsync_RefreshesTokenOnceWhenArtworkRequestIsUnauthorized()
    {
        var loginAttempts = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                loginAttempts++;
                return StubHttpMessageHandler.Json(loginAttempts == 1
                    ? """{"data":{"token":"stale-token"}}"""
                    : """{"data":{"token":"fresh-token"}}""");
            }

            if (request.Headers.Authorization?.Parameter == "stale-token")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
            }

            Assert.Equal("fresh-token", request.Headers.Authorization?.Parameter);
            return StubHttpMessageHandler.Json(
                """
                {
                  "data": [
                    { "image": "series/fresh-poster.jpg", "type": 2, "language": "en", "score": 9.4 }
                  ]
                }
                """);
        });
        var client = CreateClient(handler);

        var artworks = await client.GetSeriesArtworkAsync(81189, CancellationToken.None);

        Assert.Equal("series/fresh-poster.jpg", Assert.Single(artworks).Image);
        Assert.Equal(4, handler.Requests.Count);
    }

    private static TheTvdbClient CreateClient(StubHttpMessageHandler handler)
    {
        return new TheTvdbClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api4.thetvdb.com/v4/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TheTvdbOptions
            {
                Enabled = true,
                ApiKey = "test-key"
            }));
    }
}
