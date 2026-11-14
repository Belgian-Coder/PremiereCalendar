using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed partial class PostgresPersistenceTests
{
    [Fact]
    public async Task PostgreSql_NightlyPlanSurvivesRestartAndUsesIndependentFencedWeekJobsAndEmptyCacheCoverage()
    {
        var clock = TimeProvider.System;
        var settings = new CalendarWarmupOptions();
        using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        var monitor = services.GetRequiredService<IOptionsMonitor<CalendarWarmupOptions>>();
        var cache = new FileCalendarCache(Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions()), _environment,
            NullLogger<FileCalendarCache>.Instance);
        var workStore = new ProviderWorkStore(_database.Options, _environment);
        ProviderWorkScheduler Scheduler() => new(workStore, clock, Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions()));
        NightlyCalendarWarmupPlanner Planner(ProviderWorkScheduler scheduler) => new(
            new AppStateStore(_database.Options), scheduler, cache, monitor, clock, NullLogger<NightlyCalendarWarmupPlanner>.Instance);
        var scheduler = Scheduler();
        var initial = (await Planner(scheduler).RunOnceAsync(TestContext.Current.CancellationToken))!;
        var restarted = (await Planner(Scheduler()).RunOnceAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(initial, restarted);
        Assert.Equal(initial.Total, (await workStore.GetStateCountsAsync(TestContext.Current.CancellationToken))[ProviderWorkState.Queued.ToString()]);
        Assert.True(initial.Total >= 16);

        var leaf = (await workStore.ClaimNextAsync(false, "night-owner", clock.GetUtcNow(), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))!;
        Assert.Equal(ProviderWorkKind.CalendarWarmup, leaf.Kind);
        Assert.Equal(ProviderWorkPriority.Warmup, leaf.Priority);
        var payload = JsonSerializer.Deserialize<NightlyCalendarWarmupPayload>(leaf.PayloadJson)!;
        Assert.Equal(leaf.DedupeKey, payload.DedupeKey);
        Assert.True(await workStore.WithOwnedLeaseAsync(leaf.JobId, leaf.LeaseOwner!, clock, TimeSpan.FromSeconds(30), token => cache.SetWeekAsync(payload.Start, payload.End, payload.CacheKey, [], token), TestContext.Current.CancellationToken));
        Assert.True(await workStore.CompleteAsync(leaf.JobId, leaf.LeaseOwner!, clock.GetUtcNow(), TestContext.Current.CancellationToken));
        await new AppStateStore(_database.Options).SetValueAsync(payload.ReceiptKey, payload.ReceiptValue, TestContext.Current.CancellationToken);
        var partial = (await Planner(Scheduler()).RunOnceAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(1, partial.Fresh);
        Assert.Equal(initial.Total - 1, partial.Pending);
        Assert.False(partial.Complete);

        var occurrence = NightlyCalendarWarmupPlanner.MostRecentOccurrence(clock.GetUtcNow(), settings);
        foreach (var window in NightlyCalendarWarmupPlanner.BuildWindows(occurrence.NightDate, occurrence.CutoffUtc))
        {
            await cache.SetWeekAsync(window.Start, window.End, window.CacheKey, [], TestContext.Current.CancellationToken);
            await new AppStateStore(_database.Options).SetValueAsync(window.ReceiptKey, window.ReceiptValue, TestContext.Current.CancellationToken);
        }
        var complete = (await Planner(Scheduler()).RunOnceAsync(TestContext.Current.CancellationToken))!;
        Assert.True(complete.Complete);
        Assert.Equal(initial.Total, complete.Fresh);
        Assert.Equal(0, complete.Pending);
        var counts = await workStore.GetStateCountsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(initial.Total, counts.Values.Sum());
        Assert.NotNull(await new AppStateStore(_database.Options).GetValueAsync(NightlyCalendarWarmupPlanner.StateKey, TestContext.Current.CancellationToken));
    }
}
