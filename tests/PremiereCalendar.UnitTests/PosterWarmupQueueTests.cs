using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class PosterWarmupQueueTests
{
    [Fact]
    public async Task Enqueue_RequestsTheWebpCardVariantInOrderAndSkipsBlankAndRepeatedUrls()
    {
        var cache = new RecordingImageCache();
        await using var provider = CreateProvider(cache);
        using var queue = CreateQueue(provider, enabled: true);

        queue.Enqueue(["https://image.tmdb.org/a.jpg", "", "  ", "https://image.tmdb.org/b.jpg", "https://image.tmdb.org/a.jpg", "https://image.tmdb.org/c.jpg"]);

        await cache.WaitForCallsAsync(3);
        await Task.Delay(50);
        Assert.Equal(
            ["https://image.tmdb.org/a.jpg", "https://image.tmdb.org/b.jpg", "https://image.tmdb.org/c.jpg"],
            cache.Calls.Select(call => call.Url));
        Assert.All(cache.Calls, call =>
        {
            Assert.False(call.ForceRefresh);
            Assert.Equal(CachedImageUrl.PosterWidth, call.Width);
            Assert.Equal(ImageCacheFormat.Webp, call.Format);
        });
    }

    [Fact]
    public async Task DefaultQueueRunsTwoWorkersSoOneSlowPosterDoesNotStallTheBatch()
    {
        Assert.Equal(2, PosterWarmupQueue.DefaultWorkerCount);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new RecordingImageCache { GateFirstCall = release };
        await using var provider = CreateProvider(cache);
        using var queue = CreateQueue(provider, enabled: true, workerCount: PosterWarmupQueue.DefaultWorkerCount);

        queue.Enqueue(["https://image.tmdb.org/a.jpg", "https://image.tmdb.org/b.jpg", "https://image.tmdb.org/c.jpg"]);

        // The first poster is still in flight, yet the others are already being warmed by the second worker.
        await cache.WaitForCallsAsync(3);
        release.SetResult();
        await Task.Delay(50);
        Assert.Equal(3, cache.Calls.Select(call => call.Url).Distinct().Count());
    }

    [Fact]
    public async Task Enqueue_DoesNothingWhenTheImageCacheIsDisabled()
    {
        var cache = new RecordingImageCache();
        await using var provider = CreateProvider(cache);
        using var queue = CreateQueue(provider, enabled: false);

        queue.Enqueue(["https://image.tmdb.org/a.jpg"]);
        await Task.Delay(150);

        Assert.Empty(cache.Calls);
    }

    [Fact]
    public async Task Enqueue_LatestBatchReplacesPendingWorkAndTheItemInFlightFinishes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new RecordingImageCache { GateFirstCall = gate };
        await using var provider = CreateProvider(cache);
        using var queue = CreateQueue(provider, enabled: true);

        queue.Enqueue(["https://image.tmdb.org/a1.jpg", "https://image.tmdb.org/a2.jpg", "https://image.tmdb.org/a3.jpg"]);
        await cache.WaitForCallsAsync(1);
        queue.Enqueue(["https://image.tmdb.org/b1.jpg", "https://image.tmdb.org/b2.jpg"]);
        gate.SetResult();

        await cache.WaitForCallsAsync(3);
        await Task.Delay(100);
        Assert.Equal(
            ["https://image.tmdb.org/a1.jpg", "https://image.tmdb.org/b1.jpg", "https://image.tmdb.org/b2.jpg"],
            cache.Calls.Select(call => call.Url));
    }

    [Fact]
    public async Task Enqueue_SkipsUrlsAlreadyWarmedByThisProcess()
    {
        var cache = new RecordingImageCache();
        await using var provider = CreateProvider(cache);
        using var queue = CreateQueue(provider, enabled: true);

        queue.Enqueue(["https://image.tmdb.org/a.jpg", "https://image.tmdb.org/b.jpg"]);
        await cache.WaitForCallsAsync(2);
        queue.Enqueue(["https://image.tmdb.org/a.jpg", "https://image.tmdb.org/b.jpg", "https://image.tmdb.org/c.jpg"]);

        await cache.WaitForCallsAsync(3);
        await Task.Delay(100);
        Assert.Equal(
            ["https://image.tmdb.org/a.jpg", "https://image.tmdb.org/b.jpg", "https://image.tmdb.org/c.jpg"],
            cache.Calls.Select(call => call.Url));
    }

    [Fact]
    public async Task Enqueue_RecentlyWarmedSetIsBoundedAndEvictsTheOldestUrls()
    {
        var cache = new RecordingImageCache();
        await using var provider = CreateProvider(cache);
        using var queue = CreateQueue(provider, enabled: true);
        var total = PosterWarmupQueue.RecentlyWarmedCapacity + 500;

        for (var offset = 0; offset < total; offset += 500)
        {
            queue.Enqueue(Enumerable.Range(offset, 500).Select(index => $"https://image.tmdb.org/{index}.jpg"));
            await cache.WaitForCallsAsync(offset + 500);
        }

        queue.Enqueue(["https://image.tmdb.org/0.jpg", $"https://image.tmdb.org/{total - 1}.jpg"]);
        await cache.WaitForCallsAsync(total + 1);
        await Task.Delay(100);

        Assert.Equal(total + 1, cache.Calls.Count);
        Assert.Equal("https://image.tmdb.org/0.jpg", cache.Calls[^1].Url);
    }

    [Fact]
    public async Task Enqueue_WaitsForTheStartDelayAndANewerBatchRestartsIt()
    {
        var delays = new DelayRecorder();
        var cache = new RecordingImageCache();
        await using var provider = CreateProvider(cache);
        using var queue = CreateQueue(provider, enabled: true, startDelay: TimeSpan.FromMilliseconds(750), delay: delays.DelayAsync);

        queue.Enqueue(["https://image.tmdb.org/a.jpg"]);
        await delays.WaitForCallsAsync(1);
        Assert.Equal(TimeSpan.FromMilliseconds(750), delays.Calls[0].Duration);
        await Task.Delay(50);
        Assert.Empty(cache.Calls);

        queue.Enqueue(["https://image.tmdb.org/b.jpg"]);
        await delays.WaitForCallsAsync(2);
        Assert.True(delays.Calls[0].Token.IsCancellationRequested);
        Assert.Empty(cache.Calls);

        delays.Release(1);
        await cache.WaitForCallsAsync(1);
        await Task.Delay(50);
        Assert.Equal(["https://image.tmdb.org/b.jpg"], cache.Calls.Select(call => call.Url));
    }

    [Fact]
    public async Task Enqueue_IsolatesCacheFailuresAndContinuesWithTheNextPoster()
    {
        var cache = new RecordingImageCache { FailUrl = "https://image.tmdb.org/bad.jpg" };
        await using var provider = CreateProvider(cache);
        using var queue = CreateQueue(provider, enabled: true);

        queue.Enqueue(["https://image.tmdb.org/bad.jpg", "https://image.tmdb.org/good.jpg"]);

        await cache.WaitForCallsAsync(2);
        Assert.Equal(["https://image.tmdb.org/bad.jpg", "https://image.tmdb.org/good.jpg"], cache.Calls.Select(call => call.Url));
    }

    [Fact]
    public async Task Enqueue_TruncatesABatchBeyondTheCapacityWithoutBlocking()
    {
        var cache = new RecordingImageCache();
        await using var provider = CreateProvider(cache);
        using var queue = CreateQueue(provider, enabled: true, capacity: 2);

        var enqueue = Task.Run(() => queue.Enqueue(Enumerable.Range(0, 50).Select(index => $"https://image.tmdb.org/{index}.jpg")));
        await enqueue.WaitAsync(TimeSpan.FromSeconds(2));

        await cache.WaitForCallsAsync(2);
        await Task.Delay(100);
        Assert.Equal(["https://image.tmdb.org/0.jpg", "https://image.tmdb.org/1.jpg"], cache.Calls.Select(call => call.Url));
    }

    [Fact]
    public async Task Enqueue_AfterDisposalNeverThrows()
    {
        var cache = new RecordingImageCache();
        await using var provider = CreateProvider(cache);
        var queue = CreateQueue(provider, enabled: true);
        queue.Dispose();

        queue.Enqueue(["https://image.tmdb.org/a.jpg"]);
        queue.Enqueue(null!);
        await Task.Delay(50);

        Assert.Empty(cache.Calls);
    }

    [Fact]
    public async Task Enqueue_RewarmsAPosterOnceTheRecentlyWarmedEntryHasExpired()
    {
        var time = new ManualTimeProvider();
        var cache = new RecordingImageCache();
        await using var provider = CreateProvider(cache);
        using var queue = CreateQueue(provider, enabled: true, time: time);

        queue.Enqueue(["https://image.tmdb.org/a.jpg"]);
        await cache.WaitForCallsAsync(1);
        queue.Enqueue(["https://image.tmdb.org/a.jpg"]);
        await Task.Delay(100);
        Assert.Single(cache.Calls);

        time.Advance(PosterWarmupQueue.RecentlyWarmedLifetime + TimeSpan.FromSeconds(1));
        queue.Enqueue(["https://image.tmdb.org/a.jpg"]);

        await cache.WaitForCallsAsync(2);
    }

    [Fact]
    public async Task Worker_SurvivesAnUnexpectedExceptionOutsideTheCacheCall()
    {
        var failures = 0;
        var cache = new RecordingImageCache();
        await using var provider = CreateProvider(cache);
        using var queue = CreateQueue(provider, enabled: true, delay: (_, _) =>
        {
            if (Interlocked.Increment(ref failures) == 1)
            {
                throw new InvalidOperationException("Unexpected scheduler failure.");
            }

            return Task.CompletedTask;
        });

        queue.Enqueue(["https://image.tmdb.org/lost.jpg"]);
        await Task.Delay(150);
        queue.Enqueue(["https://image.tmdb.org/b.jpg"]);

        await cache.WaitForCallsAsync(1);
        Assert.Equal("https://image.tmdb.org/b.jpg", cache.Calls[0].Url);
    }

    [Fact]
    public async Task Worker_StopsDrainingPendingPostersAfterShutdown()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new RecordingImageCache { GateFirstCall = gate };
        await using var provider = CreateProvider(cache);
        var queue = CreateQueue(provider, enabled: true);

        queue.Enqueue(["https://image.tmdb.org/a1.jpg", "https://image.tmdb.org/a2.jpg", "https://image.tmdb.org/a3.jpg"]);
        await cache.WaitForCallsAsync(1);

        queue.Dispose();
        await Task.Delay(100);

        Assert.Single(cache.Calls);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 5, 4, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static PosterWarmupQueue CreateQueue(
        ServiceProvider provider,
        bool enabled,
        int capacity = PosterWarmupQueue.DefaultCapacity,
        TimeSpan? startDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? time = null,
        int workerCount = 1)
    {
        return new PosterWarmupQueue(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new ImageCacheOptions { Enabled = enabled }),
            NullLogger<PosterWarmupQueue>.Instance,
            capacity,
            startDelay ?? TimeSpan.Zero,
            delay ?? ((_, _) => Task.CompletedTask),
            time ?? TimeProvider.System,
            workerCount);
    }

    private static ServiceProvider CreateProvider(RecordingImageCache cache)
    {
        return new ServiceCollection().AddTransient<IImageCache>(_ => cache).BuildServiceProvider();
    }

    private sealed class DelayRecorder
    {
        private readonly object _gate = new();
        private readonly List<(TimeSpan Duration, CancellationToken Token, TaskCompletionSource Gate)> _calls = [];

        public IReadOnlyList<(TimeSpan Duration, CancellationToken Token, TaskCompletionSource Gate)> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToArray();
                }
            }
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _calls.Add((duration, token, gate));
            }

            return gate.Task.WaitAsync(token);
        }

        public void Release(int callNumber) => Calls[callNumber].Gate.TrySetResult();

        public async Task WaitForCallsAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Calls.Count < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"Expected {count} delays but saw {Calls.Count}.");
                await Task.Delay(10);
            }
        }
    }

    private sealed class RecordingImageCache : IImageCache
    {
        private readonly object _gate = new();
        private readonly List<ImageCall> _calls = [];

        public TaskCompletionSource? GateFirstCall { get; init; }

        public string? FailUrl { get; init; }

        public IReadOnlyList<ImageCall> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToArray();
                }
            }
        }

        public async Task WaitForCallsAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Calls.Count < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"Expected {count} image cache calls but saw {Calls.Count}.");
                await Task.Delay(10);
            }
        }

        public async Task<CachedImage> GetOrAddAsync(
            string sourceUrl,
            bool forceRefresh,
            CancellationToken cancellationToken,
            int? width = null,
            ImageCacheFormat format = ImageCacheFormat.Original)
        {
            bool first;
            lock (_gate)
            {
                _calls.Add(new ImageCall(sourceUrl, forceRefresh, width, format));
                first = _calls.Count == 1;
            }

            if (first && GateFirstCall is not null)
            {
                await GateFirstCall.Task.WaitAsync(cancellationToken);
            }

            if (sourceUrl == FailUrl)
            {
                throw new InvalidOperationException("Simulated image failure.");
            }

            return new CachedImage("x", "image/webp", DateTimeOffset.UtcNow, "key", TimeSpan.FromDays(1));
        }
    }

    private sealed record ImageCall(string Url, bool ForceRefresh, int? Width, ImageCacheFormat Format);
}
