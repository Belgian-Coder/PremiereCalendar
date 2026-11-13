using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class BackgroundProducerStartupGateTests
{
    [Theory]
    [InlineData("warmup", true)]
    [InlineData("warmup", false)]
    [InlineData("imdb", true)]
    [InlineData("imdb", false)]
    [InlineData("backfill", true)]
    [InlineData("backfill", false)]
    public async Task ProducerDefersDatabaseWorkUntilInitializationAndStopsWhileWaiting(string kind, bool recover)
    {
        var recovery = new DatabaseRecoveryState();
        var probe = new ProducerProbe();
        await using var services = new ServiceCollection().BuildServiceProvider();
        // Unused dependencies deliberately fail if the already-complete backfill
        // marker or durable scheduling branch unexpectedly starts provider work.
        using var service = kind switch
        {
            "warmup" => (BackgroundService)new CurrentWeekCalendarWarmupService(
                services.GetRequiredService<IServiceScopeFactory>(),
                new Monitor<CalendarWarmupOptions>(new() { RunOnStartup = true, StartupDelaySeconds = 0 }),
                new Monitor<CacheMaintenanceOptions>(new() { Enabled = false }), TimeProvider.System,
                NullLogger<CurrentWeekCalendarWarmupService>.Instance, workScheduler: probe, databaseState: recovery),
            "imdb" => new ImdbDatasetRefreshService(null!, null!,
                new Monitor<ImdbDatasetOptions>(new() { RefreshOnStartup = true, StartupDelaySeconds = 0 }), TimeProvider.System,
                NullLogger<ImdbDatasetRefreshService>.Instance, workScheduler: probe, databaseState: recovery),
            _ => new ItemEnrichmentCacheBackfillService(new ItemEnrichmentCacheCoordinator(null!,
                    Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
                    Microsoft.Extensions.Options.Options.Create(new TmdbOptions()), NullLogger<ItemEnrichmentCacheCoordinator>.Instance),
                null!, null!, new CompletedBackfillMarker(probe.Touch),
                Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
                Microsoft.Extensions.Options.Options.Create(new CalendarWarmupOptions()), new FastStartupClock(),
                NullLogger<ItemEnrichmentCacheBackfillService>.Instance, new PremiereTelemetry(), recovery)
        };
        await service.StartAsync(default);
        await Assert.ThrowsAsync<TimeoutException>(() => probe.Touched.Task.WaitAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(0, probe.Calls);
        Assert.False(service.ExecuteTask!.IsCompleted);
        if (recover)
        {
            recovery.Set(new(DatabaseSchema.CurrentVersion, DatabaseSchema.CurrentVersion, true, "initialized", null));
            await probe.Touched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(recover ? 1 : 0, probe.Calls);
    }

    private sealed class FastStartupClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => TimeProvider.System.CreateTimer(callback, state,
                dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.FromMilliseconds(5), period);
    }

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class ProducerProbe : IProviderWorkScheduler
    {
        public TaskCompletionSource Touched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public void Touch() { Interlocked.Increment(ref Calls); Touched.TrySetResult(); }
        public Task<ProviderWorkHandle> EnqueueAsync(ProviderWorkRequest request, CancellationToken cancellationToken = default)
        {
            Touch();
            return Task.FromResult(new ProviderWorkHandle("scheduled", true));
        }
        public Task<ProviderWorkSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Startup scheduling must not inspect provider jobs.");
        public async IAsyncEnumerable<ProviderWorkProgress> WatchAsync(ProviderWorkHandle handle,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CompletedBackfillMarker(Action read) : IAppStateStore
    {
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
        {
            read();
            return Task.FromResult<string?>("completed before restart");
        }
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken) => throw new InvalidOperationException("Completed marker must be retained.");
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken) => throw new InvalidOperationException("Completed marker must be retained.");
        public Task<IReadOnlyDictionary<string, string>> GetValuesByPrefixAsync(string prefix, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Backfill should stop at its completed marker.");
    }
}
