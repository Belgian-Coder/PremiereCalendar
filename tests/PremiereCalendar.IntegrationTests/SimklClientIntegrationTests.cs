using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Http.Headers;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class SimklClientIntegrationTests
{
    [Fact]
    public async Task RequestPinCodeAsync_GetsPinCodeWithClientId()
    {
        var stateStore = new FakeSimklSyncStateStore();
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("/oauth/pin", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-client-id", QueryString.Parse(request.RequestUri)["client_id"]);

            return StubHttpMessageHandler.Json(
                """
                {
                  "result": "OK",
                  "device_code": "device-code",
                  "user_code": "ABCD-EFGH",
                  "verification_url": "https://simkl.com/pin/",
                  "expires_in": 600,
                  "interval": 5
                }
                """);
        });
        var client = CreateClient(handler, stateStore);

        var result = await client.RequestPinCodeAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ABCD-EFGH", result.UserCode);
        Assert.Equal("https://simkl.com/pin/", result.VerificationUrl);
        Assert.Equal(600, result.ExpiresInSeconds);
        Assert.Equal(5, result.IntervalSeconds);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CheckPinCodeAsync_ReturnsAccessTokenWhenAuthorized()
    {
        var stateStore = new FakeSimklSyncStateStore();
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("/oauth/pin/ABCD-EFGH", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-client-id", QueryString.Parse(request.RequestUri)["client_id"]);

            return StubHttpMessageHandler.Json(
                """
                {
                  "result": "OK",
                  "access_token": "simkl-access-token"
                }
                """);
        });
        var client = CreateClient(handler, stateStore);

        var result = await client.CheckPinCodeAsync("ABCD-EFGH", CancellationToken.None);

        Assert.Equal(SimklPinStatus.Authorized, result.Status);
        Assert.Equal("simkl-access-token", result.AccessToken);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CheckPinCodeAsync_ReturnsPendingWhenAuthorizationIsNotComplete()
    {
        var stateStore = new FakeSimklSyncStateStore();
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("/oauth/pin/ABCD-EFGH", request.RequestUri!.AbsolutePath);

            return StubHttpMessageHandler.Json(
                """
                {
                  "result": "KO",
                  "message": "Authorization pending"
                }
                """);
        });
        var client = CreateClient(handler, stateStore);

        var result = await client.CheckPinCodeAsync("ABCD-EFGH", CancellationToken.None);

        Assert.Equal(SimklPinStatus.Pending, result.Status);
        Assert.Equal("Authorization pending", result.Message);
    }

    [Fact]
    public async Task SyncLibraryAsync_InitialSyncChecksActivitiesThenFetchesLibrariesSequentially()
    {
        var stateStore = new FakeSimklSyncStateStore();
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Contains("test-client-id", request.Headers.GetValues("simkl-api-key"));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-access-token", request.Headers.Authorization?.Parameter);

            return request.RequestUri!.AbsolutePath switch
            {
                "/sync/activities" => StubHttpMessageHandler.Json(
                    """
                    { "all": "2023-10-12T09:03:45Z" }
                    """),
                "/sync/shows" => StubHttpMessageHandler.Json("[]"),
                "/sync/movies" => StubHttpMessageHandler.Json("[]"),
                "/sync/anime" => StubHttpMessageHandler.Json("[]"),
                _ => throw new InvalidOperationException($"Unexpected path {request.RequestUri.AbsolutePath}")
            };
        });
        var client = CreateClient(handler, stateStore);

        var result = await client.SyncLibraryAsync(CancellationToken.None);

        Assert.Equal(SimklSyncStatus.InitialSyncCompleted, result.Status);
        Assert.Equal(
            ["/sync/activities", "/sync/shows", "/sync/movies", "/sync/anime"],
            handler.Requests.Select(request => request.Uri.AbsolutePath).ToArray());
        Assert.Equal("2023-10-12T09:03:45Z", stateStore.State.LastActivitiesAllUtc);
        Assert.True(stateStore.State.InitialSyncCompleted);
    }

    [Fact]
    public async Task SyncLibraryAsync_SubsequentSyncSkipsWhenActivitiesAreUnchanged()
    {
        var stateStore = new FakeSimklSyncStateStore
        {
            State = new SimklSyncState(
                "2023-10-12T09:03:45Z",
                """{ "all": "2023-10-12T09:03:45Z" }""",
                InitialSyncCompleted: true)
        };
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("/sync/activities", request.RequestUri!.AbsolutePath);
            return StubHttpMessageHandler.Json(
                """
                { "all": "2023-10-12T09:03:45Z" }
                """);
        });
        var client = CreateClient(handler, stateStore);

        var result = await client.SyncLibraryAsync(CancellationToken.None);

        Assert.Equal(SimklSyncStatus.Unchanged, result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SyncLibraryAsync_SubsequentSyncUsesSavedActivityDateForAllItemsDelta()
    {
        var stateStore = new FakeSimklSyncStateStore
        {
            State = new SimklSyncState(
                "2023-10-12T09:03:45Z",
                """{ "all": "2023-10-12T09:03:45Z" }""",
                InitialSyncCompleted: true)
        };
        var handler = new StubHttpMessageHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/sync/activities" => StubHttpMessageHandler.Json(
                    """
                    { "all": "2023-10-12T09:03:46Z" }
                    """),
                "/sync/all-items/" => StubHttpMessageHandler.Json("[]"),
                _ => throw new InvalidOperationException($"Unexpected path {request.RequestUri.AbsolutePath}")
            };
        });
        var client = CreateClient(handler, stateStore);

        var result = await client.SyncLibraryAsync(CancellationToken.None);

        Assert.Equal(SimklSyncStatus.DeltaSyncCompleted, result.Status);
        var deltaRequest = Assert.Single(handler.Requests, request => request.Uri.AbsolutePath == "/sync/all-items/");
        Assert.Equal("2023-10-12T09:03:45Z", QueryString.Parse(deltaRequest.Uri)["date_from"]);
        Assert.Equal("2023-10-12T09:03:46Z", stateStore.State.LastActivitiesAllUtc);
    }

    [Fact]
    public async Task SyncLibraryAsync_SkipsRequestsWhenAccessTokenIsMissing()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Simkl should not be called without an access token."));
        var stateStore = new FakeSimklSyncStateStore();
        var client = CreateClient(handler, stateStore, accessToken: "");

        var result = await client.SyncLibraryAsync(CancellationToken.None);

        Assert.Equal(SimklSyncStatus.Disabled, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SyncLibraryAsync_ReturnsFailedWhenActivityCheckTimesOutWithoutCallerCancellation()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException("Simulated HTTP timeout."));
        var stateStore = new FakeSimklSyncStateStore();
        var client = CreateClient(handler, stateStore);

        var result = await client.SyncLibraryAsync(CancellationToken.None);

        Assert.Equal(SimklSyncStatus.Failed, result.Status);
        Assert.Equal("Could not fetch Simkl activities.", result.Error);
        Assert.NotNull(stateStore.State.LastCheckedUtc);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetCalendarAsync_ReadsPublicCalendarFilesWithRequiredQuery()
    {
        var stateStore = new FakeSimklSyncStateStore();
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("data.simkl.in", request.RequestUri!.Host);
            Assert.Equal("test-client-id", QueryString.Parse(request.RequestUri)["client_id"]);
            Assert.Equal("premiere-calendar", QueryString.Parse(request.RequestUri)["app-name"]);
            Assert.Equal("1.0", QueryString.Parse(request.RequestUri)["app-version"]);

            return request.RequestUri.AbsolutePath switch
            {
                "/calendar/tv.json" => StubHttpMessageHandler.Json(
                    """
                    [
                      {
                        "title": "Simkl Show",
                        "date": "2026-05-04T00:00:00-05:00",
                        "release_date": "2026-05-04",
                        "url": "https://simkl.com/tv/100/simkl-show",
                        "ratings": { "imdb": { "rating": 8.1, "votes": 1200 } },
                        "ids": { "simkl_id": 100, "tmdb": "110", "imdb": "tt0000110", "tvdb": "1110" },
                        "episode": { "season": 1, "episode": 1, "url": "https://simkl.com/tv/100/simkl-show/season-1/episode-1" }
                      }
                    ]
                    """),
                "/calendar/movie_release.json" => StubHttpMessageHandler.Json(
                    """
                    [
                      {
                        "title": "Simkl Movie",
                        "date": "2026-05-05T00:00:00+00:00",
                        "release_date": "2026-05-05",
                        "url": "/movies/200/simkl-movie",
                        "ratings": { "imdb": { "rating": 7.2, "votes": 340 } },
                        "ids": { "simkl_id": 200, "tmdb": "220", "imdb": "tt0000220" }
                      }
                    ]
                    """),
                _ => StubHttpMessageHandler.Json("[]")
            };
        });
        var client = CreateClient(handler, stateStore);

        var items = await client.GetCalendarAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Type == SimklCalendarItemType.Tv && item.Title == "Simkl Show");
        Assert.Contains(items, item => item.Type == SimklCalendarItemType.MovieRelease && item.Title == "Simkl Movie");
        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath == "/calendar/tv.json");
        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath == "/calendar/movie_release.json");
    }

    [Fact]
    public async Task GetCalendarAsync_SkipsRequestsWhenClientIdIsMissing()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Simkl calendar should not be called without a client ID."));
        var stateStore = new FakeSimklSyncStateStore();
        var client = CreateClient(handler, stateStore, clientId: "");

        var items = await client.GetCalendarAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        Assert.Empty(items);
        Assert.Empty(handler.Requests);
    }

    private static SimklClient CreateClient(
        StubHttpMessageHandler handler,
        ISimklSyncStateStore stateStore,
        string accessToken = "test-access-token",
        string clientId = "test-client-id")
    {
        return new SimklClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.simkl.com/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new SimklOptions
            {
                Enabled = true,
                ClientId = clientId,
                ClientSecret = "test-client-secret",
                AccessToken = accessToken,
                MinimumActivityCheckMinutes = 0
            }),
            stateStore);
    }

    private sealed class FakeSimklSyncStateStore : ISimklSyncStateStore
    {
        public SimklSyncState State { get; set; } = new();

        public Task<SimklSyncState> GetAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(State);
        }

        public Task SaveAsync(SimklSyncState state, CancellationToken cancellationToken)
        {
            State = state;
            return Task.CompletedTask;
        }
    }
}
