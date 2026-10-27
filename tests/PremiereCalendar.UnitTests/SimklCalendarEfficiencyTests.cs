using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class SimklCalendarEfficiencyTests
{
    private static readonly DateOnly Start = new(2020, 8, 31);
    private static readonly DateOnly End = new(2020, 9, 6);
    private static readonly string[] AllPaths =
    [
        "/calendar/tv.json", "/calendar/movie_release.json",
        "/calendar/2020/8/tv.json", "/calendar/2020/8/movie_release.json",
        "/calendar/2020/9/tv.json", "/calendar/2020/9/movie_release.json"
    ];

    [Fact]
    public async Task OldWeekKeepsCurrentFeedsAndEveryIntersectingArchiveMonth()
    {
        var handler = new CalendarHandler((_, _) => Task.FromResult(Json("[]")));
        var client = CreateClient(handler);

        await client.GetCalendarAsync(Start, End, CancellationToken.None);

        Assert.Equal(AllPaths.Order(), handler.Paths.Order());
    }

    [Fact]
    public async Task MovieOnlySkipsTvBeforeHttpAndPreservesCurrentAndArchiveMovieFeeds()
    {
        var handler = new CalendarHandler((_, _) => Task.FromResult(Json("[]")));
        var client = CreateClient(handler);

        await client.GetCalendarAsync(Start, End, includeSeries: false, includeMovies: true, CancellationToken.None);

        Assert.Equal(AllPaths.Where(path => path.EndsWith("movie_release.json")).Order(), handler.Paths.Order());
        await client.GetCalendarAsync(Start, End, includeSeries: false, includeMovies: false, CancellationToken.None);
        Assert.Equal(3, handler.Paths.Count);
    }

    [Fact]
    public async Task ConcurrentCalendarCallsNeverExceedTwoFeedRequestsAndDoNotWaitForFeedOrderToRefillSlots()
    {
        var firstFeed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterFeed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CalendarHandler(async (path, token) =>
        {
            if (path == AllPaths[0]) await firstFeed.Task.WaitAsync(token);
            if (path == AllPaths[2]) laterFeed.TrySetResult();
            return Json("[]");
        });
        var client = CreateClient(handler);
        var first = client.GetCalendarAsync(Start, End, CancellationToken.None, forceRefresh: true);
        var second = client.GetCalendarAsync(Start, End, CancellationToken.None, forceRefresh: true);

        // The free slot starts an archive feed while the first feed is still pending.
        await laterFeed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(first.IsCompleted);
        firstFeed.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, handler.MaximumActive);
        Assert.Equal(12, handler.Paths.Count);
        Assert.Equal(0, handler.Active);
    }

    [Fact]
    public async Task ConcurrentFeedResultsKeepPerMediaFeedOrderAndFirstDuplicate()
    {
        var archiveRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CalendarHandler(async (path, token) =>
        {
            if (path == AllPaths[0]) await archiveRead.Task.WaitAsync(token);
            if (path == AllPaths[2]) archiveRead.SetResult();
            return path switch
            {
                "/calendar/tv.json" => Json(Payload(("A", 1, "/tv/1/current"))),
                "/calendar/movie_release.json" => Json(Payload(("B", 2, null))),
                "/calendar/2020/8/tv.json" => Json(Payload(("A", 1, "/tv/1/archive"), ("C", 3, null))),
                "/calendar/2020/8/movie_release.json" => Json(Payload(("D", 4, null))),
                "/calendar/2020/9/tv.json" => Json(Payload(("E", 5, null))),
                _ => Json(Payload(("F", 6, null)))
            };
        });
        var client = CreateClient(handler);

        var items = await client.GetCalendarAsync(Start, End, CancellationToken.None);

        Assert.Equal(["A", "C", "E"], items.Where(item => item.Type == SimklCalendarItemType.Tv).Select(item => item.Title));
        Assert.Equal(["B", "D", "F"], items.Where(item => item.Type == SimklCalendarItemType.MovieRelease).Select(item => item.Title));
        Assert.Equal("https://simkl.com/tv/1/current", items.Single(item => item.Title == "A").Url);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("json")]
    [InlineData("timeout")]
    [InlineData("transport")]
    public async Task RequiredFeedFailureIsExplicitAndIsNotCachedAsEmpty(string failure)
    {
        var handler = new CalendarHandler((path, _) => path == AllPaths[0]
            ? failure switch
            {
                "http" => Task.FromResult(Json("[]", HttpStatusCode.ServiceUnavailable)),
                "json" => Task.FromResult(Json("{broken")),
                "timeout" => Task.FromException<HttpResponseMessage>(new TaskCanceledException("credential-sensitive timeout text")),
                _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("credential-sensitive transport text"))
            }
            : Task.FromResult(Json("[]")));
        var client = CreateClient(handler);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var exception = await Record.ExceptionAsync(() => client.GetCalendarAsync(Start, End, CancellationToken.None));
            Assert.NotNull(exception);
            Assert.IsNotAssignableFrom<OperationCanceledException>(exception);
            Assert.DoesNotContain("credential-sensitive", exception.ToString());
            if (failure == "http") Assert.Equal(HttpStatusCode.ServiceUnavailable, Assert.IsType<HttpRequestException>(exception).StatusCode);
            if (failure == "json") Assert.IsType<InvalidDataException>(exception);
            if (failure == "timeout") Assert.IsType<TimeoutException>(exception);
        }

        Assert.Equal(2, handler.Paths.Count(path => path == AllPaths[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingPreservesSuccessfulLaterFeedBeforeReportingEarlierFailure(bool invalidJson)
    {
        var handler = new CalendarHandler((path, _) => Task.FromResult(path == AllPaths[0]
            ? invalidJson ? Json("{invalid") : Json("[]", HttpStatusCode.BadGateway)
            : Json(path == AllPaths[1] ? Payload(("Retained movie", 8, null)) : "[]")));
        IMediaFilteredStreamingPremiereDiscoveryProvider provider = new SimklCalendarDiscoveryProvider(CreateClient(handler));
        var candidates = new List<ExternalPremiereCandidate>();

        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach (var batch in provider.StreamCandidatesAsync(Start, End, true, true)) candidates.AddRange(batch);
        });

        if (invalidJson) Assert.IsType<InvalidDataException>(error);
        else Assert.IsType<HttpRequestException>(error);
        Assert.Equal("Retained movie", Assert.Single(candidates).Title);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TypedRetryDeadlineSurvivesListAndStreamPaths(bool streaming, bool circuitOpen)
    {
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(5);
        HttpRequestException expected = circuitOpen
            ? new ProviderCircuitOpenException("Simkl", retryAt)
            : new ProviderRetryAfterException("Simkl", retryAt);
        var handler = new CalendarHandler((path, _) => path == AllPaths[0]
            ? Task.FromException<HttpResponseMessage>(expected) : Task.FromResult(Json("[]")));
        var client = CreateClient(handler);

        var error = await Record.ExceptionAsync(async () =>
        {
            if (streaming)
                await foreach (var _ in client.StreamCalendarAsync(Start, End, true, true)) { }
            else
                await client.GetCalendarAsync(Start, End, CancellationToken.None);
        });

        Assert.Same(expected, error);
        Assert.Equal(retryAt, circuitOpen
            ? Assert.IsType<ProviderCircuitOpenException>(error).RetryAtUtc
            : Assert.IsType<ProviderRetryAfterException>(error).RetryAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryFeedFailureIsRetainedIncludingLaterRetryDeadlines(bool streaming)
    {
        var circuit = new ProviderCircuitOpenException("Simkl", DateTimeOffset.UtcNow.AddMinutes(3));
        var retryAfter = new ProviderRetryAfterException("Simkl", DateTimeOffset.UtcNow.AddMinutes(5));
        var handler = new CalendarHandler((path, _) => path switch
        {
            "/calendar/tv.json" => Task.FromResult(Json("[]", HttpStatusCode.BadGateway)),
            "/calendar/movie_release.json" => Task.FromException<HttpResponseMessage>(circuit),
            "/calendar/2020/8/tv.json" => Task.FromException<HttpResponseMessage>(retryAfter),
            _ => Task.FromResult(Json(path == AllPaths[3] ? Payload(("Preserved movie", 28, null)) : "[]"))
        });
        var client = CreateClient(handler);
        var streamed = new List<SimklCalendarItem>();

        var error = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            if (streaming)
                await foreach (var batch in client.StreamCalendarAsync(Start, End, true, true)) streamed.AddRange(batch);
            else
                await client.GetCalendarAsync(Start, End, CancellationToken.None);
        });

        Assert.Equal(3, error.InnerExceptions.Count);
        Assert.IsType<HttpRequestException>(error.InnerExceptions[0]);
        Assert.Same(circuit, error.InnerExceptions[1]);
        Assert.Same(retryAfter, error.InnerExceptions[2]);
        if (streaming) Assert.Equal("Preserved movie", Assert.Single(streamed).Title);
    }

    [Fact]
    public async Task FirstCompletedFeedIsVisibleBeforeRemainingFeedsFinishAndDisposalCancelsRequests()
    {
        var blockedFeed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CalendarHandler(async (path, token) =>
        {
            if (path == AllPaths[0]) return Json(Payload(("Early show", 9, null)));
            blockedFeed.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("[]");
        });
        var client = CreateClient(handler);
        var enumerator = client.StreamCalendarAsync(Start, End, true, true).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Early show", Assert.Single(enumerator.Current).Title);
        await blockedFeed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, handler.Active);
        Assert.Equal(2, handler.MaximumActive);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellationAndReleasesAllFeedSlots()
    {
        var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var block = true;
        var handler = new CalendarHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == 2) twoStarted.SetResult();
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("[]");
        });
        var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource();
        var request = client.GetCalendarAsync(Start, End, cancellation.Token);
        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(0, handler.Active);
        block = false;

        Assert.Empty(await client.GetCalendarAsync(Start, End, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, handler.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingFeedFailureCannotMaskCallerCancellationDuringCleanup(bool unexpectedFailure)
    {
        var laterFeedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CalendarHandler(async (path, token) =>
        {
            // This archive failure stays behind its pending current TV feed until cleanup.
            if (path == AllPaths[2])
            {
                if (unexpectedFailure) throw new InvalidOperationException("Simulated unexpected pending task failure.");
                return Json("{invalid");
            }
            if (path == AllPaths[1]) return Json("[]");
            if (path == AllPaths[3]) laterFeedStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("[]");
        });
        var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource();
        var request = client.GetCalendarAsync(Start, End, cancellation.Token);
        await laterFeedStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        Assert.Equal(0, handler.Active);
    }

    [Fact]
    public void SimklRequestsPreservingCompletedFeedBatchBoundaries()
    {
        var provider = new SimklCalendarDiscoveryProvider(new LegacyClient());
        Assert.True(provider.PreserveBatchBoundaries);
    }

    [Fact]
    public async Task RequestTimeoutAlsoBoundsCalendarBodyAfterSuccessfulHeaders()
    {
        var body = new BlockedReadStream();
        var handler = new CalendarHandler((path, _) => Task.FromResult(path == AllPaths[0]
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }
            : Json("[]")));
        var client = new SimklClient(new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(200) },
            Microsoft.Extensions.Options.Options.Create(new SimklOptions { ClientId = "test-client-id" }), new StateStore());

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetCalendarAsync(Start, End, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(body.ReadStarted);
        Assert.True(body.WasDisposed);
    }

    [Fact]
    public async Task LegacyClientDefaultAdaptersFilterMediaAndForwardForceRefresh()
    {
        var legacy = new LegacyClient();
        IMediaFilteredStreamingPremiereDiscoveryProvider provider = new SimklCalendarDiscoveryProvider(legacy);
        var candidates = new List<ExternalPremiereCandidate>();

        await foreach (var batch in provider.StreamCandidatesAsync(Start, End, false, true, forceRefresh: true)) candidates.AddRange(batch);

        Assert.Equal(PremiereMediaType.Movie, Assert.Single(candidates).MediaType);
        Assert.True(legacy.ForceRefresh);
        Assert.Equal(1, legacy.Calls);
        await foreach (var _ in provider.StreamCandidatesAsync(Start, End, false, false)) Assert.Fail("No media types were selected.");
        Assert.Equal(1, legacy.Calls);
    }

    [Fact]
    public async Task FeedDiagnosticsSeparateHeaderAndBodyFailuresWithoutRequestOrProviderText()
    {
        var directory = Path.Combine(Path.GetTempPath(), "premiere-simkl-diagnostics-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var store = new PerformanceDiagnosticStore(new TelemetryOptions(), directory))
            {
                var telemetry = new PremiereTelemetry(store);
                var handler = new CalendarHandler((path, _) => Task.FromResult(path == AllPaths[0]
                    ? Json("private-provider-payload-invalid-json") : Json("[]")));
                var client = new SimklClient(new HttpClient(handler),
                    Microsoft.Extensions.Options.Options.Create(new SimklOptions
                    {
                        ClientId = "private-client-credential", CalendarBaseUrl = "https://private-host.invalid/"
                    }), new StateStore(), telemetry: telemetry);
                using (telemetry.UseDiagnosticContext(telemetry.CreateDiagnosticContext(source: "simkl")))
                    await Assert.ThrowsAsync<InvalidDataException>(() => client.GetCalendarAsync(Start, End, CancellationToken.None));
                telemetry.FlushDiagnostics();
            }

            var raw = string.Join("\n", Directory.GetFiles(Path.Combine(directory, "App_Data", "logs", "performance"))
                .Select(File.ReadAllText));
            Assert.Contains("calendar_headers", raw);
            Assert.Contains("calendar_body", raw);
            Assert.Contains("invalid_json", raw);
            Assert.Contains("current_tv", raw);
            Assert.Contains("archive_movie", raw);
            Assert.DoesNotContain("private-", raw);
            Assert.DoesNotContain("client_id", raw);
            Assert.DoesNotContain("https://", raw);
            Assert.DoesNotContain("tv.json", raw);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static SimklClient CreateClient(CalendarHandler handler)
        => new(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new SimklOptions { ClientId = "test-client-id" }), new StateStore());

    private static HttpResponseMessage Json(string payload, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

    private static string Payload(params (string Title, int Id, string? Url)[] items)
        => JsonSerializer.Serialize(items.Select(item => new { title = item.Title, date = "2020-09-01T00:00:00Z", url = item.Url, ids = new { simkl_id = item.Id } }));

    private sealed class CalendarHandler(Func<string, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private int _active;
        private int _maximumActive;
        public ConcurrentQueue<string> Paths { get; } = new();
        public int Active => Volatile.Read(ref _active);
        public int MaximumActive => Volatile.Read(ref _maximumActive);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Enqueue(request.RequestUri!.AbsolutePath);
            var active = Interlocked.Increment(ref _active);
            int maximum;
            do { maximum = Volatile.Read(ref _maximumActive); }
            while (active > maximum && Interlocked.CompareExchange(ref _maximumActive, active, maximum) != maximum);
            try { return await responder(request.RequestUri.AbsolutePath, cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class StateStore : ISimklSyncStateStore
    {
        public Task<SimklSyncState> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new SimklSyncState());
        public Task SaveAsync(SimklSyncState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BlockedReadStream : Stream
    {
        public bool ReadStarted { get; private set; }
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LegacyClient : ISimklClient
    {
        public int Calls { get; private set; }
        public bool ForceRefresh { get; private set; }
        public Task<SimklSyncResult> SyncLibraryAsync(CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotSupportedException();
        public Task<SimklPinCodeResult> RequestPinCodeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SimklPinStatusResult> CheckPinCodeAsync(string userCode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SimklCalendarItem>> GetCalendarAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Calls++;
            ForceRefresh = forceRefresh;
            return Task.FromResult<IReadOnlyList<SimklCalendarItem>>([Item(SimklCalendarItemType.Tv), Item(SimklCalendarItemType.MovieRelease)]);
        }

        private static SimklCalendarItem Item(SimklCalendarItemType type)
            => new(type, "Legacy item", new DateTimeOffset(2020, 9, 1, 0, 0, 0, TimeSpan.Zero), null, null, new(1, null, null, null), null, null);
    }
}

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task RealSimklMoviesSurviveSourceDeadlineWhileEarlierTvFeedIsStillPending()
    {
        var handler = new HeadOfLineSimklHandler();
        var client = new SimklClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) },
            Microsoft.Extensions.Options.Options.Create(new SimklOptions
            {
                ClientId = "test-client-id", RequestTimeoutSeconds = 60
            }), new HeadOfLineSimklStateStore());
        var cache = new PartialSnapshotCache();
        var service = CreateService(new FakeTmdbClient(), discoveryProviders: [new SimklCalendarDiscoveryProvider(client)],
            calendarCache: cache, sourceTimeoutSeconds: 1);
        var updates = new List<PremiereLoadProgress>();

        await foreach (var update in service.StreamPremieresAsync(new(2026, 8, 31), new(2026, 9, 6),
            filters: new() { ShowSeries = true, ShowMovies = true })) updates.Add(update);

        Assert.True(handler.TvCleanupObserved);
        Assert.Equal(0, handler.Active);
        Assert.InRange(handler.MaximumActive, 1, 2);
        Assert.Contains("/calendar/tv.json", handler.Paths);
        Assert.Contains("/calendar/movie_release.json", handler.Paths);
        Assert.Contains(updates, update => !update.IsFinal && update.Items.Count == 31);
        Assert.True(updates[^1].HasSourceErrors);
        Assert.Contains("Simkl", updates[^1].FailedSourceNames);
        Assert.Equal(31, updates[^1].Items.Count);
        Assert.Equal(31, cache.Items!.Count);
        Assert.True(cache.PartialWrites > 0);
        Assert.All(cache.Items, item => Assert.Equal(PremiereMediaType.Movie, item.MediaType));
    }

    private sealed class HeadOfLineSimklHandler : HttpMessageHandler
    {
        private int _active;
        private int _maximumActive;
        public bool TvCleanupObserved { get; private set; }
        public int Active => Volatile.Read(ref _active);
        public int MaximumActive => Volatile.Read(ref _maximumActive);
        public ConcurrentQueue<string> Paths { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Enqueue(path);
            var active = Interlocked.Increment(ref _active);
            int maximum;
            do { maximum = Volatile.Read(ref _maximumActive); }
            while (active > maximum && Interlocked.CompareExchange(ref _maximumActive, active, maximum) != maximum);
            try
            {
                if (path == "/calendar/tv.json")
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                    finally { TvCleanupObserved = true; }
                }
                var payload = path == "/calendar/movie_release.json"
                    ? JsonSerializer.Serialize(Enumerable.Range(1, 31).Select(id => new
                    {
                        title = $"Movie {id}", date = "2026-08-31T00:00:00Z", ids = new { simkl_id = id, tmdb = id }
                    }))
                    : "[]";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class HeadOfLineSimklStateStore : ISimklSyncStateStore
    {
        public Task<SimklSyncState> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new SimklSyncState());
        public Task SaveAsync(SimklSyncState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
