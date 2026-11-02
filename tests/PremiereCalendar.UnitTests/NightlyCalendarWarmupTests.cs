using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Hosting;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class NightlyCalendarWarmupTests
{
    [Fact]
    public void ConfigurationBindsNightScheduleAndRespectsExplicitCalendarFreshnessOverride()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CalendarWarmup:NightlyLocalTime"] = "03:15:00",
            ["CalendarWarmup:NightlyTimeZone"] = "Europe/London",
            ["CalendarCache:WeekCacheHours"] = "6"
        }).Build();
        using var services = new ServiceCollection().AddPremiereOptions(config).BuildServiceProvider();
        var settings = services.GetRequiredService<IOptions<CalendarWarmupOptions>>().Value;
        Assert.True(settings.NightlyEnabled);
        Assert.Equal(new TimeOnly(3, 15), settings.NightlyLocalTime);
        Assert.Equal("Europe/London", settings.NightlyTimeZone);
        Assert.Equal(6, services.GetRequiredService<IOptions<CalendarCacheOptions>>().Value.WeekCacheHours);
        Assert.Equal(30, new CalendarCacheOptions().WeekCacheHours);
    }

    [Theory]
    [InlineData(2026, 12, 29, "2026-11-30", "2027-01-31")]
    [InlineData(2026, 2, 1, "2026-01-26", "2026-04-05")]
    [InlineData(2026, 11, 2, "2026-10-26", "2027-01-03")]
    public void PlanCoversExactlyTwoCalendarMonthsWithFullBoundaryWeeksAndFairDefaultProfiles(int year, int month, int day, string expectedStart, string expectedEnd)
    {
        var date = new DateOnly(year, month, day);
        var windows = NightlyCalendarWarmupPlanner.BuildWindows(date, DateTimeOffset.UtcNow);
        var weeks = windows.Where(window => window.Profile == "Series").ToArray();
        Assert.Equal(DateOnly.Parse(expectedStart), weeks.Min(window => window.Start));
        Assert.Equal(DateOnly.Parse(expectedEnd), weeks.Max(window => window.End));
        Assert.Equal(CalendarFilters.StartOfWeek(date), weeks[0].Start);
        Assert.Equal(windows.Count, windows.Select(window => window.DedupeKey).Distinct().Count());
        var monthStart = new DateOnly(year, month, 1);
        var nextMonth = monthStart.AddMonths(1);
        var last = monthStart.AddMonths(2).AddDays(-1);
        for (var cursor = monthStart; cursor <= last; cursor = cursor.AddDays(1))
            Assert.Single(weeks, window => window.Start <= cursor && cursor <= window.End);
        Assert.True(weeks.Skip(1).Select(window => window.Start < nextMonth ? 0 : 1).SequenceEqual(
            weeks.Skip(1).Select(window => window.Start < nextMonth ? 0 : 1).Order()));
        for (var i = 0; i < windows.Count; i += 2)
        {
            var series = windows[i];
            var movies = windows[i + 1];
            Assert.Equal("Series", series.Profile);
            Assert.Equal("Movies", movies.Profile);
            Assert.Equal(series.Start, movies.Start);
            Assert.Equal(series.Start.AddDays(6), series.End);
            Assert.Equal(DayOfWeek.Monday, series.Start.DayOfWeek);
            Assert.True(series.Filters.ShowSeries);
            Assert.False(series.Filters.ShowMovies);
            Assert.True(movies.Filters.ShowMovies);
            Assert.False(movies.Filters.ShowSeries);
            Assert.Equal(["en", "nl"], series.Filters.SeriesFilters.OriginalLanguages);
            Assert.Equal(["en", "nl"], movies.Filters.MovieFilters.OriginalLanguages);
            Assert.Equal(0, series.Filters.SeriesFilters.RuntimeMinMinutes);
            Assert.Equal(45, movies.Filters.MovieFilters.RuntimeMinMinutes);
            Assert.Equal(SeriesDateMode.NewSeriesOnly, series.Filters.SeriesFilters.SeriesDateMode);
        }
    }

    [Theory]
    [InlineData("2026-11-02T00:59:00Z", "2026-11-01", "2026-11-01T01:00:00Z", "2026-11-02T01:00:00Z")]
    [InlineData("2026-11-02T01:00:00Z", "2026-11-02", "2026-11-02T01:00:00Z", "2026-11-03T01:00:00Z")]
    [InlineData("2026-03-29T01:00:00Z", "2026-03-29", "2026-03-29T01:00:00Z", "2026-03-30T00:00:00Z")]
    [InlineData("2026-10-25T00:00:00Z", "2026-10-25", "2026-10-25T00:00:00Z", "2026-10-26T01:00:00Z")]
    [InlineData("2026-10-25T01:30:00Z", "2026-10-25", "2026-10-25T00:00:00Z", "2026-10-26T01:00:00Z")]
    public void ScheduleUsesBrusselsTwoAmWithMissedNightCatchupAndOneDstOccurrence(string now, string night, string cutoff, string next)
    {
        var settings = new CalendarWarmupOptions();
        var result = NightlyCalendarWarmupPlanner.MostRecentOccurrence(DateTimeOffset.Parse(now), settings);
        Assert.Equal(DateOnly.Parse(night), result.NightDate);
        Assert.Equal(DateTimeOffset.Parse(cutoff), result.CutoffUtc);
        Assert.Equal(DateTimeOffset.Parse(next), NightlyCalendarWarmupPlanner.NextOccurrenceUtc(DateTimeOffset.Parse(now), settings));
    }

    [Fact]
    public async Task RestartKeepsEveryLeafAndReportsFailedAndUnfinishedCoverageWithoutResubmission()
    {
        var fixture = new Fixture();
        var initial = (await fixture.Planner().RunOnceAsync(default))!;
        Assert.Equal(initial.Total, fixture.Scheduler.Requests.Count);
        Assert.Equal(initial.Total, initial.Pending);
        Assert.False(initial.Complete);
        Assert.All(fixture.Scheduler.Requests, request =>
        {
            Assert.Equal(ProviderWorkKind.CalendarWarmup, request.Kind);
            Assert.Equal(ProviderWorkPriority.Warmup, request.Priority);
        });
        var windows = fixture.Windows;
        fixture.Cache.Entries[windows[0].DedupeKey] = new(windows[0].RefreshNotBeforeUtc, 0, 3, CalendarCacheCompleteness.Complete);
        fixture.Store.Values[windows[0].ReceiptKey] = windows[0].ReceiptValue;
        fixture.Scheduler.States["job-2"] = ProviderWorkState.Failed;
        fixture.Scheduler.States["job-3"] = ProviderWorkState.Completed; // No corresponding complete envelope.
        fixture.Scheduler.States["job-4"] = ProviderWorkState.RetryWaiting;
        var restarted = (await fixture.Planner().RunOnceAsync(default))!;
        Assert.Equal(1, restarted.Fresh);
        Assert.Equal(2, restarted.Failed);
        Assert.Equal(initial.Total - 3, restarted.Pending);
        Assert.False(restarted.Complete);
        Assert.Equal(initial.Total, fixture.Scheduler.Requests.Count);
        Assert.Contains("\"Failed\":2", fixture.Store.Values[NightlyCalendarWarmupPlanner.StateKey]);
    }

    [Fact]
    public async Task InterruptedHandleWriteAttachesToAlreadyQueuedLeafAfterRestart()
    {
        var fixture = new Fixture();
        fixture.Store.FailFirstHandleWrite = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Planner().RunOnceAsync(default));
        Assert.Single(fixture.Scheduler.Requests);
        var restarted = (await fixture.Planner().RunOnceAsync(default))!;
        Assert.Equal(restarted.Total, restarted.Pending);
        Assert.Equal(restarted.Total, fixture.Scheduler.Requests.Count);
        Assert.Contains("job-1", fixture.Store.Values[NightlyCalendarWarmupPlanner.StateKey]);
    }

    [Fact]
    public async Task CompleteEmptyCohortSurvivesRestartAndNextNightStartsNewRefresh()
    {
        var fixture = new Fixture();
        foreach (var window in fixture.Windows)
        {
            fixture.Cache.Entries[window.DedupeKey] = new(window.RefreshNotBeforeUtc, 0, 3, CalendarCacheCompleteness.Complete);
            fixture.Store.Values[window.ReceiptKey] = window.ReceiptValue;
        }
        var first = (await fixture.Planner().RunOnceAsync(default))!;
        var restarted = (await fixture.Planner().RunOnceAsync(default))!;
        Assert.True(first.Complete);
        Assert.True(restarted.Complete);
        Assert.Empty(fixture.Scheduler.Requests);
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        var tomorrow = (await fixture.Planner().RunOnceAsync(default))!;
        Assert.False(tomorrow.Complete);
        Assert.Equal(tomorrow.Total, fixture.Scheduler.Requests.Count);
    }

    [Theory]
    [InlineData(CalendarCacheCompleteness.Partial, 1)]
    [InlineData(CalendarCacheCompleteness.Complete, -1)]
    public async Task OldOrPartialEnvelopeDoesNotCountAsNightlyCoverage(CalendarCacheCompleteness completeness, int offsetSeconds)
    {
        var fixture = new Fixture();
        foreach (var window in fixture.Windows)
            fixture.Cache.Entries[window.DedupeKey] = new(window.RefreshNotBeforeUtc.AddSeconds(offsetSeconds), 20, 3, completeness);
        var result = (await fixture.Planner().RunOnceAsync(default))!;
        Assert.Equal(0, result.Fresh);
        Assert.Equal(result.Total, result.Pending);
    }

    [Fact]
    public async Task LeafPreservesCheckpointsAndPublicationFenceWhileRefreshingOnlySnapshot()
    {
        var fixture = new Fixture();
        var payload = fixture.Windows[0];
        var completed = new Dictionary<string, IReadOnlyList<PremiereItem>> { ["completed-source"] = [] };
        var publications = 0;
        var pipeline = new RecordingPipeline(async resume =>
        {
            Assert.Same(completed, resume!.CompletedSources);
            Assert.True(resume.RefreshCalendarSnapshot);
            await resume.PublishCacheAsync!(token =>
            {
                fixture.Cache.Entries[payload.DedupeKey] = new(payload.RefreshNotBeforeUtc, 0, 3, CalendarCacheCompleteness.Complete);
                return Task.CompletedTask;
            }, default);
        });
        var runner = fixture.Runner(pipeline);
        var updates = new List<PremiereLoadProgress>();
        var resume = new ProviderWorkResumeState(completed, null)
        {
            PublishCacheAsync = async (publish, token) => { publications++; await publish(token); }
        };
        await runner.RunAsync(payload, "leaf-job", resume, progress => { updates.Add(progress); return Task.CompletedTask; }, default);
        Assert.False(pipeline.ForceRefresh);
        Assert.Equal(1, publications);
        Assert.True(Assert.Single(updates).IsFinal);
        await runner.RunAsync(payload, "duplicate-leaf", resume, _ => Task.CompletedTask, default);
        Assert.Equal(1, pipeline.Calls);
    }

    [Fact]
    public async Task PartialLeafFailsInsteadOfClaimingCompleteCoverage()
    {
        var fixture = new Fixture();
        var pipeline = new RecordingPipeline(_ => Task.CompletedTask)
        {
            Final = new PremiereLoadProgress("Partial", 0, 0, [], IsFinal: true) { HasSourceErrors = true }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runner(pipeline).RunAsync(fixture.Windows[0], "partial",
            new ProviderWorkResumeState(new Dictionary<string, IReadOnlyList<PremiereItem>>(), null), _ => Task.CompletedTask, default));
    }

    [Fact]
    public async Task FreshOrdinaryCacheDoesNotProveThatNightlyDiscoveryCompleted()
    {
        var fixture = new Fixture();
        foreach (var window in fixture.Windows)
            fixture.Cache.Entries[window.DedupeKey] = new(fixture.Clock.Now, 0, 3, CalendarCacheCompleteness.Complete);
        var result = (await fixture.Planner().RunOnceAsync(default))!;
        Assert.Equal(0, result.Fresh);
        Assert.Equal(result.Total, result.Pending);
    }

    [Fact]
    public async Task ChangedScheduleOnSameDateGetsNewJobsAndReceiptIdentity()
    {
        var fixture = new Fixture();
        var oldWindows = fixture.Windows;
        var first = (await fixture.Planner().RunOnceAsync(default))!;
        fixture.Options.NightlyLocalTime = new TimeOnly(3, 0);
        var newWindows = fixture.Windows;
        var replacement = (await fixture.Planner().RunOnceAsync(default))!;
        Assert.Equal(first.NightDate, replacement.NightDate);
        Assert.Equal(first.Total + replacement.Total, fixture.Scheduler.Requests.Count);
        Assert.NotEqual(oldWindows[0].DedupeKey, newWindows[0].DedupeKey);
        Assert.NotEqual(oldWindows[0].ReceiptKey, newWindows[0].ReceiptKey);
    }

    [Fact]
    public async Task OldNightCheckpointIsSupersededBeforeDiscoveryAndCannotSatisfyNextNight()
    {
        var fixture = new Fixture();
        var oldWindow = fixture.Windows[0];
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        var pipeline = new RecordingPipeline(_ => Task.CompletedTask);
        var result = await fixture.Runner(pipeline).RunAsync(oldWindow, "old-checkpoint",
            new ProviderWorkResumeState(new Dictionary<string, IReadOnlyList<PremiereItem>> { ["old-source"] = [] }, null), _ => Task.CompletedTask, default);
        Assert.Equal(NightlyCalendarWarmupOutcome.Superseded, result);
        Assert.Equal(0, pipeline.Calls);
        Assert.DoesNotContain(oldWindow.ReceiptKey, fixture.Store.Values.Keys);
        var next = (await fixture.Planner().RunOnceAsync(default))!;
        Assert.Equal(0, next.Fresh);
        Assert.Equal(next.Total, next.Pending);
    }

    [Fact]
    public async Task CrossingNightBoundaryWhileWaitingForPublicationFenceCannotWriteOldSnapshotOrReceipt()
    {
        var fixture = new Fixture();
        var payload = fixture.Windows[0];
        var wrote = false;
        var pipeline = new RecordingPipeline(resume => resume!.PublishCacheAsync!(_ =>
        {
            wrote = true;
            return Task.CompletedTask;
        }, default));
        var resume = new ProviderWorkResumeState(new Dictionary<string, IReadOnlyList<PremiereItem>>(), null)
        {
            PublishCacheAsync = async (publish, token) =>
            {
                fixture.Clock.Now = fixture.Clock.Now.AddDays(1); // The lease/publication gate was busy until the next night.
                await publish(token);
            }
        };
        var result = await fixture.Runner(pipeline).RunAsync(payload, "crossing-cutoff", resume, _ => Task.CompletedTask, default);
        Assert.Equal(NightlyCalendarWarmupOutcome.Superseded, result);
        Assert.False(wrote);
        Assert.Empty(fixture.Store.Values);
    }

    [Fact]
    public async Task SuppressedPublicationFailureCannotMintReceiptFromUnrelatedFreshCache()
    {
        var fixture = new Fixture();
        var payload = fixture.Windows[0];
        fixture.Cache.Entries[payload.DedupeKey] = new(fixture.Clock.Now, 10, 3, CalendarCacheCompleteness.Complete);
        var pipeline = new RecordingPipeline(async resume =>
        {
            try { await resume!.PublishCacheAsync!(_ => throw new IOException("disk write failed"), default); }
            catch (IOException) { } // PremiereService preserves its existing publication-failure fallback.
        });
        var resume = new ProviderWorkResumeState(new Dictionary<string, IReadOnlyList<PremiereItem>>(), null)
        { PublishCacheAsync = (publish, token) => publish(token) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runner(pipeline).RunAsync(payload, "failed-write", resume, _ => Task.CompletedTask, default));
        Assert.DoesNotContain(payload.ReceiptKey, fixture.Store.Values.Keys);
    }

    [Fact]
    public async Task InvalidNightlyZoneKeepsPeriodicWarmupRunning()
    {
        var scheduler = new RecordingScheduler();
        var settings = new ValueMonitor<CalendarWarmupOptions>(new CalendarWarmupOptions
        { NightlyTimeZone = "Missing/NightlyZone", StartupDelaySeconds = 0, WakeIntervalMinutes = 1 });
        await using var services = new ServiceCollection().BuildServiceProvider();
        using var service = new CurrentWeekCalendarWarmupService(services.GetRequiredService<IServiceScopeFactory>(), settings,
            new ValueMonitor<CacheMaintenanceOptions>(new CacheMaintenanceOptions { Enabled = false }),
            new FastWakeTimeProvider(), NullLogger<CurrentWeekCalendarWarmupService>.Instance, workScheduler: scheduler);
        await service.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (scheduler.EnqueueCalls < 2) await Task.Delay(10, timeout.Token);
            Assert.False(service.ExecuteTask!.IsCompleted);
        }
        finally { await service.StopAsync(default); }
    }

    private sealed class Fixture
    {
        public readonly MutableClock Clock = new();
        public readonly MemoryStateStore Store = new();
        public readonly RecordingScheduler Scheduler = new();
        public readonly MemoryCache Cache = new();
        private readonly ValueMonitor<CalendarWarmupOptions> Settings = new(new CalendarWarmupOptions());
        public CalendarWarmupOptions Options => Settings.CurrentValue;
        public IReadOnlyList<NightlyCalendarWarmupPayload> Windows
        {
            get
            {
                var occurrence = NightlyCalendarWarmupPlanner.MostRecentOccurrence(Clock.Now, Settings.CurrentValue);
                return NightlyCalendarWarmupPlanner.BuildWindows(occurrence.NightDate, occurrence.CutoffUtc);
            }
        }
        public NightlyCalendarWarmupPlanner Planner() => new(Store, Scheduler, Cache, Settings, Clock, NullLogger<NightlyCalendarWarmupPlanner>.Instance);
        public NightlyCalendarWarmupRunner Runner(IPremiereLoadPipeline pipeline) => new(pipeline, Cache, Clock, NullLogger<NightlyCalendarWarmupRunner>.Instance, new PremiereTelemetry(), Settings, Store);
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ValueMonitor<T>(T settings) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue => settings;
        public T Get(string? name) => settings;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FastWakeTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => TimeProvider.System.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.FromMilliseconds(5), period);
    }

    private sealed class MemoryStateStore : IAppStateStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool FailFirstHandleWrite;
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken)
        {
            if (FailFirstHandleWrite && value.Contains("job-1", StringComparison.Ordinal))
            {
                FailFirstHandleWrite = false;
                throw new IOException("Interrupted handle persistence");
            }
            Values[key] = value;
            return Task.CompletedTask;
        }
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken) { Values.Remove(key); return Task.CompletedTask; }
        public Task<IReadOnlyDictionary<string, string>> GetValuesByPrefixAsync(string prefix, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(Values.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary());
    }

    private sealed class MemoryCache : ICalendarCacheMaintenance
    {
        public Dictionary<string, CalendarCacheMetadata> Entries { get; } = [];
        public Task<CalendarCacheMetadata?> GetWeekMetadataAsync(DateOnly start, DateOnly end, string cacheKey, CancellationToken cancellationToken)
        {
            // Match the persisted window identity, ignoring its night date.
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cacheKey)));
            var suffix = $":{hash}:{start:yyyyMMdd}:{end:yyyyMMdd}";
            return Task.FromResult<CalendarCacheMetadata?>(Entries.LastOrDefault(entry => entry.Key.EndsWith(suffix, StringComparison.Ordinal)).Value);
        }
        public Task<int> CleanupAsync(DateTimeOffset nowUtc, TimeSpan retention, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class RecordingScheduler : IProviderWorkScheduler
    {
        public List<ProviderWorkRequest> Requests { get; } = [];
        public Dictionary<string, ProviderWorkState> States { get; } = [];
        public int EnqueueCalls;
        public Task<ProviderWorkHandle> EnqueueAsync(ProviderWorkRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref EnqueueCalls);
            var existing = Requests.FindIndex(value => value.DedupeKey == request.DedupeKey);
            if (existing >= 0) return Task.FromResult(new ProviderWorkHandle($"job-{existing + 1}", false));
            Requests.Add(request);
            var id = $"job-{Requests.Count}";
            States[id] = ProviderWorkState.Queued;
            return Task.FromResult(new ProviderWorkHandle(id, true));
        }
        public Task<ProviderWorkSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.FromResult<ProviderWorkSnapshot?>(new(jobId, ProviderWorkKind.CalendarWarmup, "", ProviderWorkPriority.Warmup, "{}", null,
                States[jobId], 0, default, null, null, null, null));
        public async IAsyncEnumerable<ProviderWorkProgress> WatchAsync(ProviderWorkHandle handle, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingPipeline(Func<ProviderWorkResumeState?, Task> execute) : IPremiereLoadPipeline
    {
        public int Calls;
        public bool ForceRefresh;
        public PremiereLoadProgress Final = new("Complete", 0, 0, [], IsFinal: true);
        public async IAsyncEnumerable<PremiereLoadProgress> StreamCoreAsync(DateOnly start, DateOnly end, bool forceRefresh, CalendarFilters? filters,
            ProviderWorkResumeState? resumeState, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            ForceRefresh = forceRefresh;
            await execute(resumeState);
            yield return Final;
        }
    }
}
