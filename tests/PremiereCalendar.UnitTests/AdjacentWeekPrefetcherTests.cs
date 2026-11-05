using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class AdjacentWeekPrefetcherTests
{
    [Fact]
    public async Task PrefetchAdjacentWeeks_QueuesConfiguredWeeksInPriorityOrder()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var service = new RecordingPremiereService();
        await using var provider = new ServiceCollection()
            .AddSingleton<IPremiereService>(service)
            .BuildServiceProvider();
        using var prefetcher = new AdjacentWeekPrefetcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            lifetime,
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions
            {
                Enabled = true,
                AdjacentWeekPrefetchEnabled = true,
                FuturePrefetchWeeks = 4,
                PastPrefetchWeeks = 2
            }),
            NullLogger<AdjacentWeekPrefetcher>.Instance);

        prefetcher.PrefetchAdjacentWeeks(new DateOnly(2026, 5, 4));

        await WaitForAsync(() => service.Calls.Count >= 6);

        Assert.Equal(
            [
                new DateOnly(2026, 5, 11),
                new DateOnly(2026, 4, 27),
                new DateOnly(2026, 5, 18),
                new DateOnly(2026, 5, 25),
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 4, 20)
            ],
            service.Calls.Select(call => call.Start).ToArray());
        Assert.All(service.Calls, call => Assert.Equal(
            SeriesDateMode.NewSeriesOnly,
            PremiereDiscoveryCriteria.FromFilters(call.Filters).Series.SeriesDateMode));
    }

    [Fact]
    public async Task PrefetchAdjacentWeeks_MigratesLegacyScopeWithoutChangingTheCallerFilters()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var service = new RecordingPremiereService();
        await using var provider = new ServiceCollection()
            .AddSingleton<IPremiereService>(service)
            .BuildServiceProvider();
        using var prefetcher = new AdjacentWeekPrefetcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            lifetime,
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions
            {
                Enabled = true,
                AdjacentWeekPrefetchEnabled = true,
                FuturePrefetchWeeks = 1,
                PastPrefetchWeeks = 0
            }),
            NullLogger<AdjacentWeekPrefetcher>.Instance);
        var filters = new CalendarFilters
        {
            ShowMovies = false,
            SeriesFilters = { SeriesDateMode = SeriesDateMode.AllEpisodes, OriginalLanguages = ["nl"] }
        };

        prefetcher.PrefetchAdjacentWeeks(new DateOnly(2026, 5, 4), filters);
        await WaitForAsync(() => service.Calls.Count >= 1);

        var prefetchedFilters = Assert.Single(service.Calls).Filters!;
        Assert.Equal(SeriesDateMode.NewSeriesOnly, prefetchedFilters.SeriesFilters.SeriesDateMode);
        Assert.Equal(["nl"], prefetchedFilters.SeriesFilters.OriginalLanguages);
        Assert.Equal(SeriesDateMode.AllEpisodes, filters.SeriesFilters.SeriesDateMode);
    }

    [Fact]
    public async Task PrefetchAdjacentWeeks_PreservesMovieOnlyFiltersForFutureAndPastWeeks()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var service = new RecordingPremiereService();
        await using var provider = new ServiceCollection()
            .AddSingleton<IPremiereService>(service)
            .BuildServiceProvider();
        using var prefetcher = new AdjacentWeekPrefetcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            lifetime,
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions
            {
                Enabled = true,
                AdjacentWeekPrefetchEnabled = true,
                FuturePrefetchWeeks = 1,
                PastPrefetchWeeks = 1
            }),
            NullLogger<AdjacentWeekPrefetcher>.Instance);

        prefetcher.PrefetchAdjacentWeeks(
            new DateOnly(2026, 5, 4),
            new CalendarFilters
            {
                ShowSeries = false,
                ShowMovies = true,
                MovieFilters =
                {
                    OriginalLanguages = ["en", "nl"],
                    RuntimeMinMinutes = 45
                }
            });

        await WaitForAsync(() => service.Calls.Count >= 2);

        Assert.Equal([new DateOnly(2026, 5, 11), new DateOnly(2026, 4, 27)], service.Calls.Select(call => call.Start).ToArray());
        Assert.All(service.Calls, call =>
        {
            Assert.NotNull(call.Filters);
            Assert.False(call.Filters!.ShowSeries);
            Assert.True(call.Filters.ShowMovies);
            Assert.Equal(["en", "nl"], call.Filters.MovieFilters.OriginalLanguages);
            Assert.Equal(45, call.Filters.MovieFilters.RuntimeMinMinutes);
        });
    }

    [Fact]
    public async Task PrefetchAdjacentWeeks_CancelsSlowPrefetchAfterConfiguredTimeout()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var service = new RecordingPremiereService
        {
            BlockFirstCall = true
        };
        await using var provider = new ServiceCollection()
            .AddSingleton<IPremiereService>(service)
            .BuildServiceProvider();
        using var prefetcher = new AdjacentWeekPrefetcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            lifetime,
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions
            {
                Enabled = true,
                AdjacentWeekPrefetchEnabled = true,
                FuturePrefetchWeeks = 1,
                PastPrefetchWeeks = 0,
                AdjacentWeekPrefetchTimeoutSeconds = 1
            }),
            NullLogger<AdjacentWeekPrefetcher>.Instance);

        prefetcher.PrefetchAdjacentWeeks(new DateOnly(2026, 5, 4));

        await WaitForAsync(() => service.CancellationObserved);

        Assert.Single(service.Calls);
    }

    [Fact]
    public async Task PrefetchAdjacentWeeks_DoesNotQueueDuplicatesWhileWeeksArePending()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var service = new RecordingPremiereService
        {
            Delay = TimeSpan.FromMilliseconds(100)
        };
        await using var provider = new ServiceCollection()
            .AddSingleton<IPremiereService>(service)
            .BuildServiceProvider();
        using var prefetcher = new AdjacentWeekPrefetcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            lifetime,
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions
            {
                Enabled = true,
                AdjacentWeekPrefetchEnabled = true,
                FuturePrefetchWeeks = 4,
                PastPrefetchWeeks = 2
            }),
            NullLogger<AdjacentWeekPrefetcher>.Instance);

        prefetcher.PrefetchAdjacentWeeks(new DateOnly(2026, 5, 4));
        prefetcher.PrefetchAdjacentWeeks(new DateOnly(2026, 5, 4));

        await WaitForAsync(() => service.Calls.Count >= 6);

        Assert.Equal(6, service.Calls.Count);
    }

    [Fact]
    public async Task PrefetchAdjacentWeeks_ReprioritizesPendingWeeksWhenWindowSlides()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var service = new RecordingPremiereService
        {
            BlockFirstCall = true
        };
        await using var provider = new ServiceCollection()
            .AddSingleton<IPremiereService>(service)
            .BuildServiceProvider();
        using var prefetcher = new AdjacentWeekPrefetcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            lifetime,
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions
            {
                Enabled = true,
                AdjacentWeekPrefetchEnabled = true,
                FuturePrefetchWeeks = 4,
                PastPrefetchWeeks = 2
            }),
            NullLogger<AdjacentWeekPrefetcher>.Instance);

        prefetcher.PrefetchAdjacentWeeks(new DateOnly(2026, 5, 4));
        await WaitForAsync(() => service.Calls.Count >= 1);

        prefetcher.PrefetchAdjacentWeeks(new DateOnly(2026, 5, 11));
        service.ReleaseFirstCall();

        await WaitForAsync(() => service.Calls.Count >= 3);

        Assert.Equal(new DateOnly(2026, 5, 11), service.Calls[0].Start);
        Assert.Equal(new DateOnly(2026, 5, 18), service.Calls[1].Start);
        Assert.Equal(new DateOnly(2026, 5, 4), service.Calls[2].Start);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, timeout.Token);
        }
    }

    [Fact]
    public async Task NavigationDropsObsoleteQueuedWeeksAndRetainsRunningPrefetch()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var service = new RecordingPremiereService { BlockFirstCall = true };
        await using var provider = new ServiceCollection().AddSingleton<IPremiereService>(service).BuildServiceProvider();
        using var prefetcher = new AdjacentWeekPrefetcher(provider.GetRequiredService<IServiceScopeFactory>(), lifetime,
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions
            {
                Enabled = true, AdjacentWeekPrefetchEnabled = true, FuturePrefetchWeeks = 2, PastPrefetchWeeks = 0
            }), NullLogger<AdjacentWeekPrefetcher>.Instance);
        prefetcher.PrefetchAdjacentWeeks(new(2026, 5, 4));
        await WaitForAsync(() => service.Calls.Count == 1);
        prefetcher.PrefetchAdjacentWeeks(new(2026, 8, 3));
        service.ReleaseFirstCall();
        await WaitForAsync(() => service.Calls.Count == 3);
        Assert.Equal([new DateOnly(2026, 5, 11), new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 17)],
            service.Calls.Select(call => call.Start).ToArray());
        Assert.False(service.CancellationObserved);
    }

    [Fact]
    public async Task CompleteFreshWeekIsCheckedBeforeDurableEnqueue()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var cache = new CompleteCalendarCache();
        var scheduler = new RecordingScheduler();
        await using var provider = new ServiceCollection().AddSingleton<ICalendarCache>(cache).BuildServiceProvider();
        using var prefetcher = new AdjacentWeekPrefetcher(provider.GetRequiredService<IServiceScopeFactory>(), lifetime,
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions
            {
                Enabled = true, AdjacentWeekPrefetchEnabled = true, FuturePrefetchWeeks = 1, PastPrefetchWeeks = 0
            }), NullLogger<AdjacentWeekPrefetcher>.Instance, workScheduler: scheduler);
        prefetcher.PrefetchAdjacentWeeks(new(2026, 5, 4));
        await cache.Read.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await prefetcher.StopAsync(CancellationToken.None);
        Assert.Empty(scheduler.Requests);
        Assert.Equal(new DateOnly(2026, 5, 11), cache.Start);
        Assert.Equal(new DateOnly(2026, 5, 17), cache.End);
    }

    [Fact]
    public async Task OverlappingNavigationRetainsInFlightWantedWeekAndUsesItsLatestPriority()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var cache = new BlockingMissingCalendarCache();
        var scheduler = new RecordingScheduler();
        await using var provider = new ServiceCollection().AddSingleton<ICalendarCache>(cache).BuildServiceProvider();
        using var prefetcher = new AdjacentWeekPrefetcher(provider.GetRequiredService<IServiceScopeFactory>(), lifetime,
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions
                { Enabled = true, AdjacentWeekPrefetchEnabled = true, FuturePrefetchWeeks = 2, PastPrefetchWeeks = 0 }),
            NullLogger<AdjacentWeekPrefetcher>.Instance, workScheduler: scheduler);
        var filters = new CalendarFilters { ShowSeries = false };
        prefetcher.PrefetchAdjacentWeeks(new(2026, 5, 4), filters);
        await cache.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // May11 remains wanted after navigation, but its in-flight dedupe key
        // prevents the newer generation from queuing a replacement request.
        prefetcher.PrefetchAdjacentWeeks(new(2026, 4, 27), filters);
        cache.Release.TrySetResult();
        await WaitForAsync(() => scheduler.Requests.Count == 2);
        await prefetcher.StopAsync(default);
        var payloads = scheduler.Requests.Select(request => System.Text.Json.JsonSerializer.Deserialize<CalendarProviderWorkPayload>(request.PayloadJson)!).ToArray();
        Assert.Equal([new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 11)], payloads.Select(payload => payload.Start).Order().ToArray());
        Assert.DoesNotContain(payloads, payload => payload.Start == new DateOnly(2026, 5, 18));
        Assert.All(payloads, payload =>
        {
            Assert.Equal(payload.Start.AddDays(6), payload.End);
            Assert.False(payload.Filters!.ShowSeries);
            Assert.False(payload.ForceRefresh);
        });
        var overlapping = Assert.Single(scheduler.Requests, request => request.DedupeKey.StartsWith("prefetch:20260511:", StringComparison.Ordinal));
        Assert.Equal((ProviderWorkPriority)((int)ProviderWorkPriority.Adjacent + 1), overlapping.Priority);
    }

    private sealed class BlockingMissingCalendarCache : ICalendarCache
    {
        private int _reads;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CalendarCacheSnapshot?> GetWeekSnapshotAsync(DateOnly start, DateOnly end, string cacheKey, CancellationToken token)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            return null;
        }
        public Task<IReadOnlyList<PremiereItem>?> GetWeekAsync(DateOnly start, DateOnly end, string cacheKey,
            CancellationToken token, bool allowExpired = false) => Task.FromResult<IReadOnlyList<PremiereItem>?>(null);
        public Task SetWeekAsync(DateOnly start, DateOnly end, string cacheKey, IReadOnlyList<PremiereItem> items, CancellationToken token)
            => Task.CompletedTask;
    }

    private sealed class CompleteCalendarCache : ICalendarCache
    {
        public TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateOnly Start { get; private set; }
        public DateOnly End { get; private set; }
        public Task<IReadOnlyList<PremiereItem>?> GetWeekAsync(DateOnly start, DateOnly end, string cacheKey,
            CancellationToken cancellationToken, bool allowExpired = false)
        {
            Start = start;
            End = end;
            Read.TrySetResult();
            return Task.FromResult<IReadOnlyList<PremiereItem>?>([]);
        }
        public Task SetWeekAsync(DateOnly start, DateOnly end, string cacheKey, IReadOnlyList<PremiereItem> items, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecordingScheduler : IProviderWorkScheduler
    {
        public List<ProviderWorkRequest> Requests { get; } = [];
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure { get; init; }
        public Task<ProviderWorkHandle> EnqueueAsync(ProviderWorkRequest request, CancellationToken cancellationToken = default)
        {
            Attempted.TrySetResult();
            if (Failure is not null) throw Failure;
            Requests.Add(request);
            return Task.FromResult(new ProviderWorkHandle("recorded", true));
        }
        public Task<ProviderWorkSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.FromResult<ProviderWorkSnapshot?>(null);
        public async IAsyncEnumerable<ProviderWorkProgress> WatchAsync(ProviderWorkHandle handle,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DurablePrefetchWaitsForInitializedSchemaAndShutdownCancelsTheWait(bool recover)
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var scheduler = new RecordingScheduler();
        var recovery = new DatabaseRecoveryState();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        using var prefetcher = new AdjacentWeekPrefetcher(provider.GetRequiredService<IServiceScopeFactory>(), lifetime,
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions
                { Enabled = true, AdjacentWeekPrefetchEnabled = true, FuturePrefetchWeeks = 1, PastPrefetchWeeks = 0 }),
            NullLogger<AdjacentWeekPrefetcher>.Instance, workScheduler: scheduler, databaseState: recovery);
        prefetcher.PrefetchAdjacentWeeks(new(2026, 5, 4));
        await Assert.ThrowsAsync<TimeoutException>(() => scheduler.Attempted.Task.WaitAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Empty(scheduler.Requests);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        if (recover)
        {
            recovery.Set(new(DatabaseSchema.CurrentVersion, DatabaseSchema.CurrentVersion, true, "initialized", null, null));
            await scheduler.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        await prefetcher.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(recover ? 1 : 0, scheduler.Requests.Count);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task BestEffortPrefetchFailureDoesNotStopForegroundHost()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        var scheduler = new RecordingScheduler { Failure = new InvalidOperationException("observed schema or programming failure") };
        await using var provider = new ServiceCollection().BuildServiceProvider();
        using var prefetcher = new AdjacentWeekPrefetcher(provider.GetRequiredService<IServiceScopeFactory>(), lifetime,
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions
                { Enabled = true, AdjacentWeekPrefetchEnabled = true, FuturePrefetchWeeks = 1, PastPrefetchWeeks = 0 }),
            NullLogger<AdjacentWeekPrefetcher>.Instance, workScheduler: scheduler);
        prefetcher.PrefetchAdjacentWeeks(new(2026, 5, 4));
        await scheduler.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await prefetcher.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    private sealed class RecordingPremiereService : IPremiereService
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _releaseFirstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public List<(DateOnly Start, DateOnly End, CalendarFilters? Filters)> Calls { get; } = [];

        public TimeSpan Delay { get; init; }
        public bool BlockFirstCall { get; init; }
        public bool CancellationObserved { get; private set; }

        public async Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false,
            IProgress<PremiereLoadProgress>? progress = null,
            CalendarFilters? filters = null)
        {
            lock (_gate)
            {
                Calls.Add((start, end, filters));
            }

            if (Interlocked.Increment(ref _callCount) == 1)
            {
                if (BlockFirstCall)
                {
                    try
                    {
                        await _releaseFirstCall.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        CancellationObserved = true;
                        throw;
                    }
                }
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return [];
        }

        public void ReleaseFirstCall()
        {
            _releaseFirstCall.TrySetResult();
        }

        public async IAsyncEnumerable<PremiereLoadProgress> StreamPremieresAsync(
            DateOnly start,
            DateOnly end,
            bool forceRefresh = false,
            CalendarFilters? filters = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var items = await GetPremieresAsync(start, end, cancellationToken, forceRefresh, filters: filters);
            yield return new PremiereLoadProgress("Complete", items.Count, items.Count, items, IsFinal: true);
        }
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public FakeHostApplicationLifetime()
        {
            _started.Cancel();
        }

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
            _stopped.Cancel();
        }

        public void Dispose()
        {
            StopApplication();
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
