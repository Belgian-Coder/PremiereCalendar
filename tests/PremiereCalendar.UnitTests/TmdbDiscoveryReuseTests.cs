using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class TmdbDiscoveryReuseTests
{
    private static readonly DateOnly Start = new(2026, 5, 4);
    private static readonly DateOnly End = new(2026, 5, 10);

    [Fact]
    public async Task StaggeredStreamsReplayCompletedPagesAndCancelOnlyTheirOwnWait()
    {
        var secondPageStarted = Signal();
        var releaseSecondPage = Signal();
        var handler = new Handler(async (request, token) =>
        {
            var page = Query(request.RequestUri!)["page"];
            if (page == "2")
            {
                secondPageStarted.TrySetResult();
                await releaseSecondPage.Task.WaitAsync(token);
            }
            return Json($$"""{"page":{{page}},"total_pages":2,"results":[{"id":{{page}}}]}""");
        });
        var client = Client(handler);
        using var firstCancellation = new CancellationTokenSource();
        await using var first = client.StreamDiscoverMoviesAsync(Start, End, new(), firstCancellation.Token).GetAsyncEnumerator();
        await using var second = client.StreamDiscoverMoviesAsync(Start, End, new(), CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await first.MoveNextAsync());
        Assert.True(await second.MoveNextAsync());
        Assert.Equal(1, Assert.Single(second.Current.Results).Id);
        var firstNext = first.MoveNextAsync().AsTask();
        var secondNext = second.MoveNextAsync().AsTask();
        await secondPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await firstCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstNext);
        Assert.False(secondNext.IsCompleted);
        releaseSecondPage.SetResult();
        Assert.True(await secondNext.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, Assert.Single(second.Current.Results).Id);
        Assert.False(await second.MoveNextAsync());

        var cached = await client.DiscoverMoviesAsync(Start, End, new(), CancellationToken.None);
        Assert.Equal([1, 2], cached.Select(item => item.Id));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ForceRefreshNeverJoinsNormalPagesAndFencesTheirLaterPublication()
    {
        var normalStarted = Signal();
        var releaseNormal = Signal();
        var calls = 0;
        var handler = new Handler(async (_, token) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                normalStarted.SetResult();
                await releaseNormal.Task.WaitAsync(token);
            }
            return Json($$"""{"page":1,"total_pages":1,"results":[{"id":{{call}}}]}""");
        });
        var client = Client(handler);
        var normal = client.DiscoverMoviesAsync(Start, End, new(), CancellationToken.None);
        await normalStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var refreshed = await client.DiscoverMoviesAsync(Start, End, new(), CancellationToken.None, forceRefresh: true)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, Assert.Single(refreshed).Id);
        releaseNormal.SetResult();
        Assert.Equal(1, Assert.Single(await normal).Id);
        Assert.Equal(2, Assert.Single(await client.DiscoverMoviesAsync(Start, End, new(), CancellationToken.None)).Id);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NormalStartedDuringRefreshCannotOverwriteItAfterCompletion()
    {
        var refreshStarted = Signal();
        var normalStarted = Signal();
        var releaseRefresh = Signal();
        var releaseNormal = Signal();
        var calls = 0;
        var handler = new Handler(async (_, token) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1) { refreshStarted.SetResult(); await releaseRefresh.Task.WaitAsync(token); }
            else { normalStarted.SetResult(); await releaseNormal.Task.WaitAsync(token); }
            return Json($$"""{"page":1,"total_pages":1,"results":[{"id":{{call}}}]}""");
        });
        var client = Client(handler);
        var refresh = client.DiscoverMoviesAsync(Start, End, new(), CancellationToken.None, forceRefresh: true);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var normal = client.DiscoverMoviesAsync(Start, End, new(), CancellationToken.None);
        await normalStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releaseRefresh.SetResult();
        Assert.Equal(1, Assert.Single(await refresh).Id);
        releaseNormal.SetResult();
        await normal;
        Assert.Equal(1, Assert.Single(await client.DiscoverMoviesAsync(Start, End, new(), CancellationToken.None)).Id);
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("tv")]
    [InlineData("season")]
    public async Task Verified404IsReusedAndForceBypassesIt(string endpoint)
    {
        var calls = 0;
        var handler = new Handler((_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? Json("""{"success":false,"status_code":34,"status_message":"The resource you requested could not be found."}""", HttpStatusCode.NotFound)
            : Json("""{"id":42,"episodes":[{"episode_number":1,"season_number":1}]}""")));
        var client = Client(handler);

        Assert.Null(await Detail(client, endpoint));
        Assert.Null(await Detail(client, endpoint));
        Assert.Equal(1, calls);
        Assert.NotNull(await Detail(client, endpoint, force: true));
        Assert.NotNull(await Detail(client, endpoint));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(404, "{}")]
    [InlineData(404, "<html>Missing proxy route</html>")]
    [InlineData(404, "{\"success\":false,\"status_code\":37}")]
    [InlineData(503, "{}")]
    [InlineData(200, "{broken")]
    [InlineData(200, "null")]
    public async Task FailureOrUnverifiedBodyIsNotRetainedAsMissing(int status, string body)
    {
        var handler = new Handler((_, _) => Task.FromResult(Json(body, (HttpStatusCode)status)));
        var client = Client(handler);
        for (var attempt = 0; attempt < 2; attempt++)
            await Record.ExceptionAsync(() => client.GetMovieDetailsAsync(42, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FailedForcedFetchPreservesThePreviousSuccessfulDetail()
    {
        var calls = 0;
        var handler = new Handler((_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? Json("""{"id":42,"title":"Preserved"}""") : Json("{}", HttpStatusCode.ServiceUnavailable)));
        var client = Client(handler);
        Assert.Equal("Preserved", (await client.GetMovieDetailsAsync(42, CancellationToken.None))!.Title);
        await Assert.ThrowsAsync<ExternalApiException>(() => client.GetMovieDetailsAsync(42, CancellationToken.None, forceRefresh: true));
        Assert.Equal("Preserved", (await client.GetMovieDetailsAsync(42, CancellationToken.None))!.Title);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task VerifiedMissingCacheHasABoundedCapacity()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json("""{"success":false,"status_code":6}""", HttpStatusCode.NotFound)));
        var client = Client(handler);
        for (var id = 1; id <= 257; id++) await client.GetMovieDetailsAsync(id, CancellationToken.None);
        await client.GetMovieDetailsAsync(257, CancellationToken.None);
        Assert.Equal(257, handler.Requests.Count);
        await client.GetMovieDetailsAsync(1, CancellationToken.None);
        Assert.Equal(258, handler.Requests.Count);
    }

    [Fact]
    public async Task TypedClientsSharingMemoryReuseActiveDiscoveryAndVerifiedMissingEntries()
    {
        var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.Contains("discover")
            ? Json("""{"page":1,"total_pages":1,"results":[{"id":42}]}""")
            : Json("""{"success":false,"status_code":34}""", HttpStatusCode.NotFound)));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var first = Client(handler, memory);
        var second = Client(handler, memory);
        await using var firstStream = first.StreamDiscoverMoviesAsync(Start, End, new(), CancellationToken.None).GetAsyncEnumerator();
        await using var secondStream = second.StreamDiscoverMoviesAsync(Start, End, new(), CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await firstStream.MoveNextAsync());
        Assert.True(await secondStream.MoveNextAsync());
        Assert.Single(handler.Requests);
        Assert.False(await firstStream.MoveNextAsync());
        Assert.False(await secondStream.MoveNextAsync());
        Assert.Null(await first.GetMovieDetailsAsync(42, CancellationToken.None));
        Assert.Null(await second.GetMovieDetailsAsync(42, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SharedDiscoveryCachePreservesEachClientsPageCap()
    {
        var handler = new Handler((request, _) =>
        {
            var page = Query(request.RequestUri!)["page"];
            return Task.FromResult(Json($$"""{"page":{{page}},"total_pages":3,"results":[{"id":{{page}}}]}"""));
        });
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var limited = Client(handler, memory, new TmdbOptions { BearerToken = "test", MaxPagesPerQuery = 1, MaxUnfilteredPagesPerQuery = 1 });
        var complete = Client(handler, memory, new TmdbOptions { BearerToken = "test", MaxPagesPerQuery = 3, MaxUnfilteredPagesPerQuery = 3 });

        Assert.Single(await limited.DiscoverMoviesAsync(Start, End, new(), CancellationToken.None));
        Assert.Equal(3, (await complete.DiscoverMoviesAsync(Start, End, new(), CancellationToken.None)).Count);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task SharedDiscoveryPromotesQueuedPagesForForegroundSubscribersAndRemovesCancelledPriority()
    {
        var admission = new CapturingAdmission();
        var handler = new Handler((_, _) => Task.FromResult(Json("""{"page":1,"total_pages":1,"results":[{"id":42}]}""")));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var maintenanceClient = Client(handler, memory, singleFlight: admission);
        var foregroundClient = Client(handler, memory, singleFlight: admission);
        var producerPriority = ProviderWorkPriority.Maintenance;
        Task<IReadOnlyList<TmdbMovieDiscoverItem>> maintenance;
        using (ProviderExecutionContext.Push(() => producerPriority))
            maintenance = maintenanceClient.DiscoverMoviesAsync(Start, End, new(), CancellationToken.None);
        await admission.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ProviderWorkPriority.Maintenance, admission.Priority!());

        using var cancellation = new CancellationTokenSource();
        Task<IReadOnlyList<TmdbMovieDiscoverItem>> foreground;
        using (ProviderExecutionContext.Push(() => ProviderWorkPriority.Foreground))
            foreground = foregroundClient.DiscoverMoviesAsync(Start, End, new(), cancellation.Token);
        Assert.Equal(ProviderWorkPriority.Foreground, admission.Priority());
        Assert.Equal(1, admission.Calls);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreground);
        Assert.Equal(ProviderWorkPriority.Maintenance, admission.Priority());
        producerPriority = ProviderWorkPriority.ActiveWeek;
        Assert.Equal(ProviderWorkPriority.ActiveWeek, admission.Priority());
        Assert.False(maintenance.IsCompleted);
        admission.Release.SetResult();
        Assert.Equal(42, Assert.Single(await maintenance.WaitAsync(TimeSpan.FromSeconds(10))).Id);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LateSubscriberStartsFreshAfterBatchFailureWhileAnOlderEnumeratorRetainsItsFlight()
    {
        var firstPageCalls = 0;
        var secondPageCalls = 0;
        var handler = new Handler((request, _) =>
        {
            var page = Query(request.RequestUri!)["page"];
            var generation = page == "1" ? Interlocked.Increment(ref firstPageCalls) : Volatile.Read(ref firstPageCalls);
            if (page == "2" && Interlocked.Increment(ref secondPageCalls) == 1)
                return Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(Json($$"""{"page":{{page}},"total_pages":2,"results":[{"id":{{generation * 100 + int.Parse(page)}}}]}"""));
        });
        var client = Client(handler);
        await using var older = client.StreamDiscoverMoviesAsync(Start, End, new(), CancellationToken.None).GetAsyncEnumerator();
        await using var failed = client.StreamDiscoverMoviesAsync(Start, End, new(), CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await older.MoveNextAsync());
        Assert.True(await failed.MoveNextAsync());
        await Assert.ThrowsAsync<ExternalApiException>(() => failed.MoveNextAsync().AsTask());
        Assert.Equal(1, firstPageCalls);

        await using var replacement = client.StreamDiscoverMoviesAsync(Start, End, new(), CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await replacement.MoveNextAsync());
        Assert.Equal(201, Assert.Single(replacement.Current.Results).Id);
        Assert.Equal(2, firstPageCalls);
        await older.DisposeAsync();

        // Releasing the final old subscriber must leave the replacement registered.
        await using var joinedReplacement = client.StreamDiscoverMoviesAsync(Start, End, new(), CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await joinedReplacement.MoveNextAsync());
        Assert.Equal(201, Assert.Single(joinedReplacement.Current.Results).Id);
        Assert.Equal(2, firstPageCalls);
        Assert.True(await replacement.MoveNextAsync());
        Assert.True(await joinedReplacement.MoveNextAsync());
        Assert.Equal(202, Assert.Single(joinedReplacement.Current.Results).Id);
        Assert.False(await replacement.MoveNextAsync());
        Assert.False(await joinedReplacement.MoveNextAsync());
        Assert.Equal(2, secondPageCalls);
        Assert.Equal([201, 202], (await client.DiscoverMoviesAsync(Start, End, new(), CancellationToken.None)).Select(item => item.Id));
    }

    private static async Task<object?> Detail(TmdbClient client, string endpoint, bool force = false)
        => endpoint switch
        {
            "tv" => await client.GetTvDetailsAsync(42, CancellationToken.None, force),
            "season" => await client.GetTvSeasonDetailsAsync(42, 1, CancellationToken.None, force),
            _ => await client.GetMovieDetailsAsync(42, CancellationToken.None, force)
        };

    private static TmdbClient Client(HttpMessageHandler handler, IMemoryCache? cache = null, TmdbOptions? options = null,
        ISingleFlightCoordinator? singleFlight = null) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") },
        cache ?? new MemoryCache(new MemoryCacheOptions()),
        Microsoft.Extensions.Options.Options.Create(options ?? new TmdbOptions
        {
            BearerToken = "test", PageBatchSize = 1, MaxRequestsPerSecond = 40
        }), NullLogger<TmdbClient>.Instance, singleFlight: singleFlight);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1]);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            return responder(request, cancellationToken);
        }
    }

    private sealed class CapturingAdmission : ISingleFlightCoordinator
    {
        public TaskCompletionSource Started { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public Func<ProviderWorkPriority>? Priority { get; private set; }
        public int Calls { get; private set; }
        public async Task<T> RunAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken token)
        {
            Calls++;
            Priority = ProviderExecutionContext.CapturePriorityAccessor();
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return await factory(token);
        }
    }
}
