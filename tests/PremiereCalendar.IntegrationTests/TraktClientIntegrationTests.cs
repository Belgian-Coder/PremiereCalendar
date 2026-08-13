using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class TraktClientIntegrationTests
{
    [Fact]
    public async Task Calendars_ReadMovieAndNewShowItemsWithRequiredHeaders()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Contains("test-client-id", request.Headers.GetValues("trakt-api-key"));
            Assert.Contains("2", request.Headers.GetValues("trakt-api-version"));
            Assert.Contains(request.Headers.UserAgent, value => value.Product?.Name == "PremiereCalendar");

            return request.RequestUri!.AbsolutePath.Contains("/movies/", StringComparison.Ordinal)
                ? StubHttpMessageHandler.Json(
                    """
                    [
                      {
                        "released": "2026-05-04",
                        "movie": { "title": "Trakt Movie", "ids": { "tmdb": 200, "imdb": "tt0000200" } }
                      }
                    ]
                    """)
                : StubHttpMessageHandler.Json(
                    """
                    [
                      {
                        "first_aired": "2026-05-05T20:00:00.000Z",
                        "episode": { "season": 1, "number": 1 },
                        "show": { "title": "Trakt Show", "ids": { "tmdb": 100, "tvdb": 81189, "imdb": "tt0000100" } }
                      }
                    ]
                    """);
        });
        var client = CreateClient(handler, clientId: "test-client-id");

        var movies = await client.GetMovieCalendarAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);
        var shows = await client.GetNewShowCalendarAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        Assert.Equal("Trakt Movie", Assert.Single(movies).Movie?.Title);
        Assert.Equal("Trakt Show", Assert.Single(shows).Show?.Title);
        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/calendars/all/movies/2026-05-04/7"));
        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/calendars/all/shows/new/2026-05-04/7"));
    }

    [Fact]
    public async Task Calendars_SkipRequestsWhenClientIdIsMissing()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Trakt should not be called without a client ID."));
        var client = CreateClient(handler, clientId: null);

        var movies = await client.GetMovieCalendarAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);
        var shows = await client.GetNewShowCalendarAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        Assert.Empty(movies);
        Assert.Empty(shows);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Calendars_ReadClientIdFromLocalSettingsStore()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Contains("database-client-id", request.Headers.GetValues("trakt-api-key"));
            Assert.Contains("2", request.Headers.GetValues("trakt-api-version"));
            return StubHttpMessageHandler.Json("[]");
        });
        var client = new TraktClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TraktOptions { ClientId = null, ApiVersion = "2" }),
            new FakeIntegrationSettingsStore(new IntegrationSettings
            {
                Sources = new SourceIntegrationSettings
                {
                    Trakt = new TraktSourceSettings
                    {
                        Enabled = true,
                        ClientId = "database-client-id"
                    }
                }
            }));

        await client.GetMovieCalendarAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Calendars_LeaveRateLimitRetriesToSharedProviderPolicy()
    {
        var attempts = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return response;
            }

            return StubHttpMessageHandler.Json(
                """
                [
                  {
                    "released": "2026-05-04",
                    "movie": { "title": "Retried Movie", "ids": { "tmdb": 200 } }
                  }
                ]
                """);
        });
        var client = CreateClient(handler, clientId: "test-client-id");

        var movies = await client.GetMovieCalendarAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Empty(movies);
    }

    [Fact]
    public async Task GetMovieCalendarAsync_ReturnsEmptyWhenHttpClientTimesOutWithoutCallerCancellation()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException("Simulated HTTP timeout."));
        var client = CreateClient(handler, clientId: "test-client-id");

        var movies = await client.GetMovieCalendarAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        Assert.Empty(movies);
        Assert.Single(handler.Requests);
    }

    private static TraktClient CreateClient(StubHttpMessageHandler handler, string? clientId)
    {
        return new TraktClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TraktOptions
            {
                ClientId = clientId,
                ApiVersion = "2"
            }));
    }

    private sealed class FakeIntegrationSettingsStore : IIntegrationSettingsStore
    {
        private IntegrationSettings _settings;

        public FakeIntegrationSettingsStore(IntegrationSettings settings)
        {
            _settings = settings;
        }

        public Task<IntegrationSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_settings);
        }

        public Task SaveAsync(IntegrationSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }
}
