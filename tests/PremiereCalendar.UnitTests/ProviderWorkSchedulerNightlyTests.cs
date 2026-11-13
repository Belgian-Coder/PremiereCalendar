using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class ProviderWorkSchedulerTests
{
    [Fact]
    public async Task FreshNightlyCohortCompletesDespiteAnOlderDeferredCheckpointWithoutProviderWork()
    {
        var now = DateTimeOffset.UtcNow;
        var occurrence = NightlyCalendarWarmupPlanner.MostRecentOccurrence(now, new CalendarWarmupOptions());
        var payload = NightlyCalendarWarmupPlanner.BuildWindows(occurrence.NightDate, occurrence.CutoffUtc)[0];
        var handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarWarmup, payload.DedupeKey,
            ProviderWorkPriority.Warmup, JsonSerializer.Serialize(payload)));
        var claimed = (await _scheduler.ClaimNextAsync(false, "prior-night-owner", default))!;
        await _scheduler.PublishAsync(claimed, new ProviderWorkProgress(handle.JobId, ProviderWorkState.Running, "Provider deferred",
            CalendarProgress: new PremiereLoadProgress("Partial", 0, 0, [], IsFinal: true)
            { HasSourceErrors = true, RetryAfterUtc = now.AddHours(1) }), default);
        await _scheduler.PreemptAsync(claimed, default);
        var cache = new NightlyLeafCache { Metadata = new(now, 0, 3, CalendarCacheCompleteness.Complete) };
        var state = new NightlySchedulerStateStore();
        state.Values[payload.ReceiptKey] = payload.ReceiptValue;
        var pipeline = new NightlyHandoffPipeline(cache, payload.RefreshNotBeforeUtc);
        await using var services = new ServiceCollection().AddOptions()
            .AddSingleton<IAppStateStore>(state)
            .AddSingleton<IPremiereLoadPipeline>(pipeline)
            .AddSingleton<ICalendarCacheMaintenance>(cache)
            .AddSingleton(TimeProvider.System)
            .AddSingleton(new PremiereTelemetry())
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<NightlyCalendarWarmupRunner>>(NullLogger<NightlyCalendarWarmupRunner>.Instance)
            .AddScoped<NightlyCalendarWarmupRunner>()
            .BuildServiceProvider();
        var recovery = new DatabaseRecoveryState();
        recovery.Set(new(DatabaseSchema.CurrentVersion, DatabaseSchema.CurrentVersion, true, "ready", null));
        using var worker = new ProviderWorkSchedulerHostedService(_scheduler, services.GetRequiredService<IServiceScopeFactory>(),
            recovery, Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { LeaseSeconds = 30 }),
            NullLogger<ProviderWorkSchedulerHostedService>.Instance);
        await worker.StartAsync(default);
        try
        {
            await WaitUntilAsync(async () => (await _scheduler.GetAsync(handle.JobId))!.State == ProviderWorkState.Completed);
            Assert.Equal(0, pipeline.SourceFetches);
            Assert.Equal(0, pipeline.Publications);
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task NightlyLeafDispatchCheckpointsPreemptionResumesSameWindowAndFencesItsPublication()
    {
        var now = DateTimeOffset.UtcNow;
        var date = DateOnly.FromDateTime(now.Date);
        var occurrence = NightlyCalendarWarmupPlanner.MostRecentOccurrence(now, new CalendarWarmupOptions());
        var payload = NightlyCalendarWarmupPlanner.BuildWindows(occurrence.NightDate, occurrence.CutoffUtc)[0];
        var cache = new NightlyLeafCache();
        var state = new NightlySchedulerStateStore();
        var pipeline = new NightlyHandoffPipeline(cache, payload.RefreshNotBeforeUtc);
        var nightly = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarWarmup, payload.DedupeKey,
            ProviderWorkPriority.Warmup, JsonSerializer.Serialize(payload)));
        await using var services = new ServiceCollection().AddOptions()
            .AddSingleton<IAppStateStore>(state)
            .AddSingleton<IPremiereLoadPipeline>(pipeline)
            .AddSingleton<ICalendarCacheMaintenance>(cache)
            .AddSingleton(TimeProvider.System)
            .AddSingleton(new PremiereTelemetry())
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<NightlyCalendarWarmupRunner>>(NullLogger<NightlyCalendarWarmupRunner>.Instance)
            .AddScoped<NightlyCalendarWarmupRunner>()
            .BuildServiceProvider();
        var recovery = new DatabaseRecoveryState();
        recovery.Set(new(DatabaseSchema.CurrentVersion, DatabaseSchema.CurrentVersion, true, "ready", null));
        using var worker = new ProviderWorkSchedulerHostedService(_scheduler, services.GetRequiredService<IServiceScopeFactory>(),
            recovery, Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { LeaseSeconds = 30 }),
            NullLogger<ProviderWorkSchedulerHostedService>.Instance);
        await worker.StartAsync(default);
        try
        {
            await pipeline.SourceCheckpointed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var foreground = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "nightly-foreground-preemption",
                ProviderWorkPriority.Foreground, JsonSerializer.Serialize(new CalendarProviderWorkPayload(date, date, false, payload.Filters))));
            await pipeline.ForegroundEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pipeline.NightResumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("night-source", pipeline.Resume!.CompletedSources.Keys);
            Assert.True(pipeline.Resume.RefreshCalendarSnapshot);
            Assert.NotNull(pipeline.Resume.PublishCacheAsync);
            Assert.Equal(payload.Start, pipeline.LastNightStart);
            Assert.Equal(payload.End, pipeline.LastNightEnd);
            pipeline.Release.TrySetResult();
            await WaitUntilAsync(async () => (await _scheduler.GetAsync(nightly.JobId))!.State == ProviderWorkState.Completed
                && (await _scheduler.GetAsync(foreground.JobId))!.State == ProviderWorkState.Completed);
            Assert.Equal(1, pipeline.SourceFetches);
            Assert.Equal(1, pipeline.Publications);
            Assert.Equal(0, (await _scheduler.GetAsync(nightly.JobId))!.AttemptCount);
            Assert.NotNull(cache.Metadata);
            Assert.Equal(payload.ReceiptValue, state.Values[payload.ReceiptKey]);
        }
        finally { await worker.StopAsync(default); }
    }

    private sealed class NightlyLeafCache : ICalendarCacheMaintenance
    {
        public CalendarCacheMetadata? Metadata;
        public Task<CalendarCacheMetadata?> GetWeekMetadataAsync(DateOnly start, DateOnly end, string cacheKey, CancellationToken cancellationToken) => Task.FromResult(Metadata);
        public Task<int> CleanupAsync(DateTimeOffset nowUtc, TimeSpan retention, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class NightlySchedulerStateStore : IAppStateStore
    {
        public readonly Dictionary<string, string> Values = [];
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken) { Values[key] = value; return Task.CompletedTask; }
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken) { Values.Remove(key); return Task.CompletedTask; }
        public Task<IReadOnlyDictionary<string, string>> GetValuesByPrefixAsync(string prefix, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(Values.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary());
    }

    private sealed class NightlyHandoffPipeline(NightlyLeafCache cache, DateTimeOffset cutoff) : IPremiereLoadPipeline
    {
        public readonly TaskCompletionSource SourceCheckpointed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ForegroundEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource NightResumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderWorkResumeState? Resume;
        public DateOnly LastNightStart;
        public DateOnly LastNightEnd;
        public int SourceFetches;
        public int Publications;

        public async IAsyncEnumerable<PremiereLoadProgress> StreamCoreAsync(DateOnly start, DateOnly end, bool forceRefresh,
            CalendarFilters? filters, ProviderWorkResumeState? resumeState, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Assert.False(forceRefresh);
            if (resumeState?.RefreshCalendarSnapshot != true)
            {
                ForegroundEntered.TrySetResult();
                yield return new("Complete", 0, 0, [], IsFinal: true);
                yield break;
            }
            LastNightStart = start;
            LastNightEnd = end;
            if (!resumeState.CompletedSources.ContainsKey("night-source"))
            {
                SourceFetches++;
                yield return new("Night source", 1, 1, []) { Phase = "complete", CheckpointKey = "night-source", SourceItems = [] };
                SourceCheckpointed.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            Resume = resumeState;
            NightResumed.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await resumeState.PublishCacheAsync!(_ =>
            {
                Publications++;
                cache.Metadata = new(cutoff, 0, 3, CalendarCacheCompleteness.Complete);
                return Task.CompletedTask;
            }, cancellationToken);
            yield return new("Complete", 0, 0, [], IsFinal: true);
        }
    }
}
