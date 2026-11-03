using System.Net;
using Microsoft.Extensions.Caching.Memory;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task RealSimklAccessDenialCreatesReusableDegradedCoverageWithoutRepeatedFeedRequests()
    {
        using var files = new DegradedCoverageFiles();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var date = new DateOnly(2026, 11, 2);
        var handler = new DeniedSimklCalendarHandler();
        var client = new SimklClient(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(
            new SimklOptions { Enabled = true, ClientId = "fixture-client" }), new DeniedSimklStateStore(), cache: memory);
        var tmdb = ItemCacheMovie(date);
        var service = CreateService(tmdb, omdb: ItemCacheOmdb("7.1"), calendarCache: files.CreateCache(),
            discoveryProviders: [new SimklCalendarDiscoveryProvider(client)]);
        var first = await DegradedFinalAsync(service, date);
        Assert.Single(first.Items);
        Assert.True(first.HasSourceErrors);
        Assert.True(first.IsReusableDegraded);
        var notice = Assert.Single(first.UnavailableSources);
        Assert.Equal("simkl", notice.Provider);
        Assert.Equal("authentication_denied", notice.Reason);
        Assert.Equal(403, notice.StatusCode);
        Assert.True(notice.RetryAtUtc > DateTimeOffset.UtcNow);
        var requests = handler.Requests;
        var repeated = await DegradedFinalAsync(service, date);
        Assert.True(repeated.FromCache);
        Assert.True(repeated.IsReusableDegraded);
        Assert.Equal(first.UnavailableSources, repeated.UnavailableSources);
        Assert.Equal(requests, handler.Requests);
    }

    private sealed class DeniedSimklCalendarHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
    }

    private sealed class DeniedSimklStateStore : ISimklSyncStateStore
    {
        public Task<SimklSyncState> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new SimklSyncState());
        public Task SaveAsync(SimklSyncState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
