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
