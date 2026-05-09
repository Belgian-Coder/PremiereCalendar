using Microsoft.Extensions.Caching.Memory;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class TvmazeClientIntegrationTests
{
    [Fact]
    public async Task LookupShowAsync_UsesTvdbIdBeforeImdbIdAndCachesResponse()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("tvmaze/lookup-show-success.json")));
        var client = CreateClient(handler, enabled: true);

        var first = await client.LookupShowAsync(81189, "tt0944947", CancellationToken.None);
        var second = await client.LookupShowAsync(81189, "tt0944947", CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Single(handler.Requests);
        Assert.Equal("81189", QueryString.Parse(handler.Requests[0].Uri)["thetvdb"]);
        Assert.False(QueryString.Parse(handler.Requests[0].Uri).ContainsKey("imdb"));
    }

    [Fact]
    public async Task LookupShowAsync_SkipsLookupWithoutExactExternalId()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("TVmaze should not be called."));
        var client = CreateClient(handler, enabled: true);

        var show = await client.LookupShowAsync(null, null, CancellationToken.None);

        Assert.Null(show);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SearchShowByNameAsync_ReturnsOnlyExactNormalizedTitleMatchesAndCachesResponse()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("tvmaze/search-show-success.json")));
        var client = CreateClient(handler, enabled: true);

        var first = await client.SearchShowByNameAsync("No Poster Show", CancellationToken.None);
        var second = await client.SearchShowByNameAsync("No Poster Show", CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal("https://static.tvmaze.com/uploads/images/original_untouched/1/1.jpg", first.Image?.Original);
        Assert.Single(handler.Requests);
        Assert.Equal("No Poster Show", QueryString.Parse(handler.Requests[0].Uri)["q"]);
    }

    [Fact]
    public async Task SearchShowByNameAsync_ForceRefreshBypassesCacheForSameRequest()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("tvmaze/search-show-success.json")));
        var client = CreateClient(handler, enabled: true);

        await client.SearchShowByNameAsync("No Poster Show", CancellationToken.None);
        await client.SearchShowByNameAsync("No Poster Show", CancellationToken.None);
        await client.SearchShowByNameAsync("No Poster Show", CancellationToken.None, forceRefresh: true);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetShowImagesAsync_ReadsPosterImagesAndCachesResponse()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(
                """
                [
                  {
                    "type": "poster",
                    "resolutions": {
                      "original": { "url": "https://static.tvmaze.com/uploads/images/original_untouched/1/1.jpg" }
                    }
                  }
                ]
                """));
        var client = CreateClient(handler, enabled: true);

        var first = await client.GetShowImagesAsync(82, CancellationToken.None);
        var second = await client.GetShowImagesAsync(82, CancellationToken.None);

        Assert.Single(first);
        Assert.Same(first, second);
        Assert.Single(handler.Requests);
        Assert.EndsWith("/shows/82/images", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task GetScheduleAsync_ReadsConfiguredScheduleWhenDiscoveryIsEnabled()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(
                """
                [
                  {
                    "season": 1,
                    "number": 1,
                    "airdate": "2026-05-04",
                    "_embedded": {
                      "show": {
                        "id": 82,
                        "name": "Scheduled Show",
                        "externals": { "thetvdb": 81189, "imdb": "tt0944947" }
                      }
                    }
                  }
                ]
                """));
        var client = CreateClient(handler, enabled: true, enableScheduleDiscovery: true);

        var episodes = await client.GetScheduleAsync(
            new DateOnly(2026, 5, 4),
            "US",
            webSchedule: true,
            CancellationToken.None);

        var episode = Assert.Single(episodes);
        Assert.Equal(1, episode.Season);
        Assert.Equal(81189, episode.Embedded?.Show?.Externals?.TheTvdb);
        Assert.EndsWith("/schedule/web", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("US", QueryString.Parse(handler.Requests[0].Uri)["country"]);
        Assert.Equal("2026-05-04", QueryString.Parse(handler.Requests[0].Uri)["date"]);
    }

    [Fact]
    public async Task GetScheduleAsync_ReturnsEmptyWhenHttpClientTimesOutWithoutCallerCancellation()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException("Simulated HTTP timeout."));
        var client = CreateClient(handler, enabled: true, enableScheduleDiscovery: true);

        var episodes = await client.GetScheduleAsync(
            new DateOnly(2026, 5, 4),
            "US",
            webSchedule: true,
            CancellationToken.None);

        Assert.Empty(episodes);
        Assert.Single(handler.Requests);
    }

    private static TvmazeClient CreateClient(
        StubHttpMessageHandler handler,
        bool enabled,
        bool enableScheduleDiscovery = false)
    {
        return new TvmazeClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.tvmaze.com/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TvmazeOptions
            {
                Enabled = enabled,
                EnableScheduleDiscovery = enableScheduleDiscovery
            }));
    }
}
