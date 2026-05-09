using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class OmdbClientIntegrationTests
{
    [Fact]
    public async Task GetByImdbIdAsync_SkipsHttpWhenDisabled()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("OMDb should not be called."));
        var client = CreateOmdbClient(handler, enabled: false);

        var result = await client.GetByImdbIdAsync("tt0000100", CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetByImdbIdAsync_ReturnsFalseResponseWithoutThrowing()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("omdb/response-false.json")));
        var client = CreateOmdbClient(handler, enabled: true);

        var result = await client.GetByImdbIdAsync("tt-missing", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("False", result.Response);
    }

    [Fact]
    public async Task GetByImdbIdAsync_ThrowsHelpfulExceptionWhenRequestLimitIsReached()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                "{\"Response\":\"False\",\"Error\":\"Request limit reached!\"}",
                System.Text.Encoding.UTF8,
                "application/json")
        });
        var client = CreateOmdbClient(handler, enabled: true);

        var error = await Assert.ThrowsAsync<ExternalApiException>(() =>
            client.GetByImdbIdAsync("tt0000100", CancellationToken.None));

        Assert.Contains("Request limit reached", error.Message);
    }

    [Fact]
    public async Task GetByImdbIdAsync_CachesSuccessfulResponse()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("omdb/by-imdb-id-success.json")));
        var client = CreateOmdbClient(handler, enabled: true);

        await client.GetByImdbIdAsync("tt0000100", CancellationToken.None);
        await client.GetByImdbIdAsync("tt0000100", CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetByImdbIdAsync_CoalescesConcurrentCacheMisses()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var handler = new AsyncStubHttpMessageHandler(async _ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return StubHttpMessageHandler.Json(Fixture.Read("omdb/by-imdb-id-success.json"));
        });
        var client = CreateOmdbClient(handler, enabled: true);

        var first = client.GetByImdbIdAsync("tt0000100", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.GetByImdbIdAsync("tt0000100", CancellationToken.None);

        release.SetResult();

        Assert.NotNull(await first);
        Assert.NotNull(await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetByImdbIdAsync_ForceRefreshBypassesCacheForSameRequest()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(Fixture.Read("omdb/by-imdb-id-success.json")));
        var client = CreateOmdbClient(handler, enabled: true);

        await client.GetByImdbIdAsync("tt0000100", CancellationToken.None);
        await client.GetByImdbIdAsync("tt0000100", CancellationToken.None);
        await client.GetByImdbIdAsync("tt0000100", CancellationToken.None, forceRefresh: true);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetByImdbIdAsync_ReturnsNullWhenHttpClientTimesOutWithoutCallerCancellation()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException("Simulated HTTP timeout."));
        var client = CreateOmdbClient(handler, enabled: true);

        var result = await client.GetByImdbIdAsync("tt0000100", CancellationToken.None);

        Assert.Null(result);
        Assert.Single(handler.Requests);
    }

    private static OmdbClient CreateOmdbClient(HttpMessageHandler handler, bool enabled)
    {
        return new OmdbClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://www.omdbapi.com/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new OmdbOptions
            {
                Enabled = enabled,
                ApiKey = enabled ? "test-key" : null
            }));
    }

    private sealed class AsyncStubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responder(request);
        }
    }
}
