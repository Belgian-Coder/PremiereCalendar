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

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "authentication_denied")]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, "rate_limited")]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, "http_failure")]
    public async Task ArtworkFailuresDoNotBecomeVerifiedEmptyThroughAdapter(System.Net.HttpStatusCode status, string reason)
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/login")
            ? StubHttpMessageHandler.Json("{\"data\":{\"token\":\"test-token\"}}") : new HttpResponseMessage(status));
        var adapter = new TheTvdbArtworkProvider(CreateClient(handler), Microsoft.Extensions.Options.Options.Create(new TheTvdbOptions()));
        var result = await adapter.GetArtworkWithStatusAsync(new(PremiereCalendar.Models.PremiereMediaType.Series, 42, null, 420, null, "Example"), CancellationToken.None, true);
        Assert.False(result.Successful);
        Assert.Null(result.Value);
        Assert.Equal(reason, result.Failure?.Reason);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    public async Task LoginDenialIsSharedAcrossDistinctTitlesAndNeverVerifiedAsAbsence(System.Net.HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status));
        var client = CreateClient(handler);
        var first = await client.GetSeriesArtworkWithStatusAsync(42, CancellationToken.None);
        var second = await client.GetSeriesArtworkWithStatusAsync(43, CancellationToken.None, true);
        Assert.False(first.Successful);
        Assert.Equal("authentication_denied", first.Failure?.Reason);
        Assert.False(second.Successful);
        Assert.Equal("local_backoff", second.Failure?.Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Verified404IsReusedWhileMalformedPayloadAndTimeoutStayFailures()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/login")
            ? StubHttpMessageHandler.Json("{\"data\":{\"token\":\"test-token\"}}") : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        var client = CreateClient(handler);
        Assert.True((await client.GetSeriesArtworkWithStatusAsync(42, CancellationToken.None)).Successful);
        Assert.True((await client.GetSeriesArtworkWithStatusAsync(42, CancellationToken.None)).Successful);
        Assert.Equal(2, handler.Requests.Count);
        var malformed = CreateClient(new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/login")
            ? StubHttpMessageHandler.Json("{\"data\":{\"token\":\"test-token\"}}") : StubHttpMessageHandler.Json("{}")));
        Assert.False((await malformed.GetSeriesArtworkWithStatusAsync(42, CancellationToken.None)).Successful);
        var timeout = CreateClient(new StubHttpMessageHandler(_ => throw new TaskCanceledException()));
        Assert.Equal("timeout", (await timeout.GetSeriesArtworkWithStatusAsync(42, CancellationToken.None)).Failure?.Reason);
    }

    [Theory]
    [InlineData("{\"id\":82,\"artworks\":[]}", 0)]
    [InlineData("{\"id\":82,\"artworks\":[{\"image\":\"series/poster.jpg\",\"type\":2}]}", 1)]
    public async Task OfficialSeriesExtendedArtworkEnvelopeIsVerifiedAndCached(string data, int count)
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/login")
            ? StubHttpMessageHandler.Json("{\"data\":{\"token\":\"fixture\"}}") : StubHttpMessageHandler.Json("{\"status\":\"success\",\"data\":" + data + "}"));
        var client = CreateClient(handler);
        var first = await client.GetSeriesArtworkWithStatusAsync(82, CancellationToken.None);
        var second = await client.GetSeriesArtworkWithStatusAsync(82, CancellationToken.None);
        Assert.True(first.Successful);
        Assert.True(second.Successful);
        Assert.Equal(count, first.Value.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task EnabledBlankKeyIsSkippedWithoutRetryDeadlineOrHttp()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Unconfigured provider must not send."));
        var result = await CreateClient(handler, " ").GetSeriesArtworkWithStatusAsync(82, CancellationToken.None, true);
        Assert.True(result.Successful);
        Assert.Equal("not_configured", result.Failure?.Reason);
        Assert.Null(result.Failure?.RetryAtUtc);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NullArtworkCollectionIsUnverifiedAndCannotConfirmPosterAbsence()
    {
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/login")
            ? StubHttpMessageHandler.Json("{\"data\":{\"token\":\"fixture\"}}")
            : StubHttpMessageHandler.Json("{\"status\":\"success\",\"data\":{\"id\":82,\"artworks\":null}}"));
        var result = await CreateClient(handler).GetSeriesArtworkWithStatusAsync(82, CancellationToken.None);
        Assert.False(result.Successful);
        Assert.Equal("invalid_response", result.Failure?.Reason);
        Assert.Empty(result.Value);
    }

    private static TheTvdbClient CreateClient(StubHttpMessageHandler handler, string apiKey = "test-key")
    {
        return new TheTvdbClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api4.thetvdb.com/v4/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TheTvdbOptions
            {
                Enabled = true,
                ApiKey = apiKey
            }));
    }
}
