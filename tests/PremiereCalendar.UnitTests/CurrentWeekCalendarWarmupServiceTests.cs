using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class CurrentWeekCalendarWarmupServiceTests
{
    [Fact]
    public async Task RunOnceAsync_WarmsAdaptiveDateWindowsAndSkipsUntilMinimumRefreshIsDue()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
        var premiereService = new RecordingPremiereService();
        var usageStore = new RecordingFilterUsageStore();
        var runner = CreateRunner(
            premiereService,
            usageStore,
            timeProvider,
            new CalendarWarmupOptions
            {
                MinimumRemoteRefreshMinutes = 60,
                MaximumProfilesPerWake = 3,
                TopFilterProfileCount = 0,
                MaximumRemoteWindowsPerWake = int.MaxValue
            });

        await runner.RunOnceAsync(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(15));
        await runner.RunOnceAsync(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(46));
        await runner.RunOnceAsync(CancellationToken.None);

        var expectedWindows = ExpectedWindowsForMay8();
        Assert.Equal(expectedWindows.Length * 4, premiereService.Calls.Count);
        Assert.Equal(expectedWindows, premiereService.Calls.Take(expectedWindows.Length).Select(call => (call.Start, call.End)).ToArray());
        Assert.Equal(expectedWindows, premiereService.Calls.Skip(expectedWindows.Length * 2).Take(expectedWindows.Length).Select(call => (call.Start, call.End)).ToArray());
        Assert.All(premiereService.Calls, call =>
        {
            Assert.False(call.ForceRefresh);
            Assert.Equal(call.Start, call.Filters.WeekStart);
            Assert.Equal(SeriesDateMode.NewSeriesOnly, call.Filters.SeriesFilters.SeriesDateMode);
        });
        Assert.Equal(new DateOnly(2026, 5, 8), premiereService.Calls[0].Filters.PriorityDate);
        Assert.Equal(new DateOnly(2026, 5, 11), premiereService.Calls[1].Filters.PriorityDate);
        Assert.Equal(new DateOnly(2026, 5, 18), premiereService.Calls[2].Filters.PriorityDate);
        Assert.Equal(new DateOnly(2026, 5, 25), premiereService.Calls[3].Filters.PriorityDate);
        Assert.Equal(new DateOnly(2026, 5, 3), premiereService.Calls[4].Filters.PriorityDate);
        Assert.Equal(new DateOnly(2026, 6, 1), premiereService.Calls[5].Filters.PriorityDate);
    }

    [Fact]
    public void BuildWarmupWindows_OrdersCurrentDayWeekAndCurrentAndNextMonth()
    {
        var windows = CurrentWeekCalendarWarmupRunner.BuildWarmupWindows(new DateOnly(2026, 5, 8));

        Assert.Equal(ExpectedWindowsForMay8(), windows.Select(window => (window.Start, window.End)).ToArray());
    }

    [Theory]
    [InlineData(2026, 6, 1)]
    [InlineData(2026, 12, 31)]
    [InlineData(2027, 2, 1)]
    public void BuildWarmupWindows_CoversOnlyWeeksIntersectingCurrentOrNextMonth(int year, int month, int day)
    {
        var today = new DateOnly(year, month, day);
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(2).AddDays(-1);
        var expected = new List<(DateOnly Start, DateOnly End)>();
        for (var start = CalendarFilters.StartOfWeek(first); start <= last; start = start.AddDays(7))
            expected.Add((start, start.AddDays(6)));
        var actual = CurrentWeekCalendarWarmupRunner.BuildWarmupWindows(today);
        Assert.Equal(expected, actual.Select(window => (window.Start, window.End)).OrderBy(window => window.Start));
        Assert.Equal(actual.Count, actual.Select(window => window.Start).Distinct().Count());
        Assert.All(actual, window =>
        {
            Assert.True(window.End >= first && window.Start <= last);
            Assert.InRange(window.PriorityDate, first, last);
            Assert.InRange(window.PriorityDate, window.Start, window.End);
        });
    }

    [Fact]
    public async Task RunOnceAsync_RehydratesTopFilterTemplatesForTheCurrentWeek()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
        var premiereService = new RecordingPremiereService();
        var usageStore = new RecordingFilterUsageStore
        {
            TopProfiles =
            [
                new CalendarFilterUsageProfile(
                    "used:movies:hot",
                    CalendarPageMode.Movies,
                    "hot",
                    new CalendarFilters
                    {
                        WeekStart = new DateOnly(2026, 3, 2),
                        ShowSeries = false,
                        ShowMovies = true,
                        SeriesFilters = { SeriesDateMode = SeriesDateMode.AllEpisodes },
                        PriorityDate = new DateOnly(2026, 3, 4),
                        MovieFilters =
                        {
                            SelectedSources = ["provider:8"],
                            WatchRegion = "BE"
                        }
                    },
                    UseCount: 5,
                    LastUsedUtc: timeProvider.GetUtcNow().AddMinutes(-1),
                    LastWarmedUtc: null,
                    LastItemCount: 12,
                    LastFailure: null,
                    IsDefault: false)
            ]
        };
        var runner = CreateRunner(
            premiereService,
            usageStore,
            timeProvider,
            new CalendarWarmupOptions
            {
                MaximumProfilesPerWake = 4,
                TopFilterProfileCount = 1,
                MaximumRemoteWindowsPerWake = int.MaxValue
            });

        await runner.RunOnceAsync(CancellationToken.None);

        var hotCalls = premiereService.Calls
            .Where(call => call.Filters.MovieFilters.SelectedSources.Contains("provider:8"))
            .ToArray();
        Assert.Equal(ExpectedWindowsForMay8().Length, hotCalls.Length);
        var hotCall = hotCalls[0];
        Assert.Equal(new DateOnly(2026, 5, 4), hotCall.Filters.WeekStart);
        Assert.Equal(new DateOnly(2026, 5, 8), hotCall.Filters.PriorityDate);
        Assert.False(hotCall.Filters.ShowSeries);
        Assert.True(hotCall.Filters.ShowMovies);
        Assert.Equal(SeriesDateMode.NewSeriesOnly, hotCall.Filters.SeriesFilters.SeriesDateMode);
    }

    [Fact]
    public async Task RunOnceAsync_LimitsMissingWarmupWindowsPerWake()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
        var premiereService = new RecordingPremiereService();
        var runner = CreateRunner(
            premiereService,
            new RecordingFilterUsageStore(),
            timeProvider,
            new CalendarWarmupOptions
            {
                MaximumProfilesPerWake = 5,
                TopFilterProfileCount = 0,
                MaximumRemoteWindowsPerWake = 3
            });

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, premiereService.Calls.Count);
        Assert.Equal(
            ExpectedWindowsForMay8().Take(3),
            premiereService.Calls.Select(call => (call.Start, call.End)));
    }

    [Fact]
    public async Task RunOnceAsync_SkipsWhenForegroundLoadIsActive()
    {
        var coordinator = new CalendarLoadCoordinator();
        using var foreground = coordinator.BeginForegroundLoad();
        var premiereService = new RecordingPremiereService();
        var runner = CreateRunner(
            premiereService,
            new RecordingFilterUsageStore(),
            new TestTimeProvider(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero)),
            coordinator: coordinator);

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Empty(premiereService.Calls);
    }

    [Fact]
    public async Task RunOnceAsync_PassesOwnedCachePublicationGuardToEachWindowPipeline()
    {
        var pipeline = new CapturingLoadPipeline();
        var runner = new CurrentWeekCalendarWarmupRunner(
            new RecordingPremiereService(),
            new RecordingFilterUsageStore(),
            new CalendarLoadCoordinator(),
            Microsoft.Extensions.Options.Options.Create(new CalendarWarmupOptions
            {
                MaximumProfilesPerWake = 1,
                TopFilterProfileCount = 0,
                MaximumRemoteWindowsPerWake = 2
            }),
            new TestTimeProvider(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<CurrentWeekCalendarWarmupRunner>.Instance,
            loadPipeline: pipeline);
        var callbackInvoked = false;
        var resumeState = new ProviderWorkResumeState(
            new Dictionary<string, IReadOnlyList<PremiereItem>>(StringComparer.Ordinal),
            LatestProgress: null)
        {
            PublishCacheAsync = async (publish, token) =>
            {
                callbackInvoked = true;
                await publish(token);
            }
        };

        await runner.RunOnceAsync(CancellationToken.None, resumeState);

        Assert.Equal(2, pipeline.ResumeStates.Count);
        var passedState = pipeline.ResumeStates[0];
        Assert.NotNull(passedState);
        Assert.NotSame(resumeState, passedState);
        Assert.Contains("first-window", passedState.CompletedSources.Keys);
        var nextWindowState = Assert.IsType<ProviderWorkResumeState>(pipeline.ResumeStates[1]);
        Assert.NotSame(passedState, nextWindowState);
        Assert.Empty(nextWindowState.CompletedSources);
        await passedState.PublishCacheAsync!(token => Task.CompletedTask, CancellationToken.None);
        Assert.True(callbackInvoked);
    }

    [Fact]
    public async Task RunOnceWithResultAsync_ReportsProfileFailures()
    {
        var runner = new CurrentWeekCalendarWarmupRunner(
            new ThrowingPremiereService(),
            new RecordingFilterUsageStore(),
            new CalendarLoadCoordinator(),
            Microsoft.Extensions.Options.Options.Create(new CalendarWarmupOptions
            {
                MaximumProfilesPerWake = 1,
                TopFilterProfileCount = 0,
                MaximumRemoteWindowsPerWake = 1
            }),
            new TestTimeProvider(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<CurrentWeekCalendarWarmupRunner>.Instance);

        var result = await runner.RunOnceWithResultAsync(CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Equal(1, result.WarmedProfiles);
        Assert.Equal(1, result.FailedProfiles);
    }

    [Fact]
    public async Task RunOnceAsync_AllPageTreatsWarmupFreshOnlyWhenBothSharedMediaCachesAreFresh()
    {
        var now = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        var premiereService = new RecordingPremiereService();
        var filters = new CalendarFilters { ShowSeries = true, ShowMovies = true };
        var keys = SharedMediaCacheKeys(filters);
        var maintenance = new RecordingCalendarCacheMaintenance(key => key == keys.Series || key == keys.Movies
            ? new CalendarCacheMetadata(now, 1, 8, CalendarCacheCompleteness.Complete)
            : null);
        var runner = CreateAllPageRunner(premiereService, timeProvider, maintenance, filters);

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Empty(premiereService.Calls);
        Assert.Equal(keys.Series, maintenance.RequestedKeys[0]);
        Assert.Equal(keys.Movies, maintenance.RequestedKeys[1]);
    }

    [Fact]
    public async Task RunOnceAsync_AllPageRefreshesWhenOneSharedMediaCacheIsMissing()
    {
        var now = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        var premiereService = new RecordingPremiereService();
        var filters = new CalendarFilters { ShowSeries = true, ShowMovies = true };
        var keys = SharedMediaCacheKeys(filters);
        var maintenance = new RecordingCalendarCacheMaintenance(key => key == keys.Series
            ? new CalendarCacheMetadata(now, 1, 8, CalendarCacheCompleteness.Complete)
            : null);
        var runner = CreateAllPageRunner(premiereService, timeProvider, maintenance, filters);

        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Single(premiereService.Calls);
        Assert.Contains(keys.Series, maintenance.RequestedKeys);
        Assert.Contains(keys.Movies, maintenance.RequestedKeys);
    }

    private static CurrentWeekCalendarWarmupRunner CreateRunner(
        RecordingPremiereService premiereService,
        RecordingFilterUsageStore usageStore,
        TimeProvider timeProvider,
        CalendarWarmupOptions? options = null,
        CalendarLoadCoordinator? coordinator = null,
        ICalendarCacheMaintenance? cacheMaintenance = null)
    {
        return new CurrentWeekCalendarWarmupRunner(
            premiereService,
            usageStore,
            coordinator ?? new CalendarLoadCoordinator(),
            Microsoft.Extensions.Options.Options.Create(options ?? new CalendarWarmupOptions
            {
                MinimumRemoteRefreshMinutes = 60,
                MaximumProfilesPerWake = 5,
                TopFilterProfileCount = 0
            }),
            timeProvider,
            NullLogger<CurrentWeekCalendarWarmupRunner>.Instance,
            cacheMaintenance: cacheMaintenance);
    }

    private static CurrentWeekCalendarWarmupRunner CreateAllPageRunner(
        RecordingPremiereService premiereService,
        TestTimeProvider timeProvider,
        ICalendarCacheMaintenance cacheMaintenance,
        CalendarFilters filters)
    {
        var usageStore = new RecordingFilterUsageStore
        {
            TopProfiles =
            [
                new CalendarFilterUsageProfile(
                    "used:all",
                    CalendarPageMode.All,
                    "",
                    filters,
                    UseCount: 1,
                    LastUsedUtc: timeProvider.GetUtcNow(),
                    LastWarmedUtc: null,
                    LastItemCount: 0,
                    LastFailure: null,
                    IsDefault: false)
            ],
            InitialProfiles = new Dictionary<string, CalendarFilterUsageProfile>(StringComparer.Ordinal)
            {
                ["default:series"] = new CalendarFilterUsageProfile("default:series", CalendarPageMode.Series, "", new CalendarFilters(), 0, timeProvider.GetUtcNow(), timeProvider.GetUtcNow(), 0, null, true),
                ["default:movies"] = new CalendarFilterUsageProfile("default:movies", CalendarPageMode.Movies, "", new CalendarFilters(), 0, timeProvider.GetUtcNow(), timeProvider.GetUtcNow(), 0, null, true)
            }
        };

        return CreateRunner(
            premiereService,
            usageStore,
            timeProvider,
            new CalendarWarmupOptions
            {
                MinimumRemoteRefreshMinutes = 60,
                MaximumProfilesPerWake = 3,
                TopFilterProfileCount = 1,
                MaximumRemoteWindowsPerWake = 1
            },
            cacheMaintenance: cacheMaintenance);
    }

    private static (string Series, string Movies) SharedMediaCacheKeys(CalendarFilters filters)
    {
        var seriesFilters = CalendarFilterState.Clone(filters);
        CalendarFilterState.ApplyPageMode(seriesFilters, CalendarPageMode.Series);
        CalendarFilterState.Normalize(seriesFilters);
        var movieFilters = CalendarFilterState.Clone(filters);
        CalendarFilterState.ApplyPageMode(movieFilters, CalendarPageMode.Movies);
        CalendarFilterState.Normalize(movieFilters);
        return (
            PremiereDiscoveryCriteria.FromFilters(seriesFilters).CacheKey(),
            PremiereDiscoveryCriteria.FromFilters(movieFilters).CacheKey());
    }

    private static (DateOnly Start, DateOnly End)[] ExpectedWindowsForMay8()
    {
        return
        [
            (new DateOnly(2026, 5, 4), new DateOnly(2026, 5, 10)),
            (new DateOnly(2026, 5, 11), new DateOnly(2026, 5, 17)),
            (new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 24)),
            (new DateOnly(2026, 5, 25), new DateOnly(2026, 5, 31)),
            (new DateOnly(2026, 4, 27), new DateOnly(2026, 5, 3)),
            (new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 7)),
            (new DateOnly(2026, 6, 8), new DateOnly(2026, 6, 14)),
            (new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 21)),
            (new DateOnly(2026, 6, 22), new DateOnly(2026, 6, 28)),
            (new DateOnly(2026, 6, 29), new DateOnly(2026, 7, 5))
        ];
    }

    private sealed class RecordingPremiereService : IPremiereService
    {
        public List<WarmupCall> Calls { get; } = [];

        public Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false,
            IProgress<PremiereLoadProgress>? progress = null,
            CalendarFilters? filters = null)
        {
            Calls.Add(new WarmupCall(start, end, forceRefresh, CalendarFilterState.Clone(filters ?? new CalendarFilters())));
            return Task.FromResult<IReadOnlyList<PremiereItem>>(
            [
                new PremiereItem
                {
                    CanonicalId = "movie:1",
                    Type = PremiereItemType.MovieFirstRelease,
                    MediaType = PremiereMediaType.Movie,
                    TmdbId = 1,
                    Title = "Warm",
                    PremiereDate = start
                }
            ]);
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

    private sealed class ThrowingPremiereService : IPremiereService
    {
        public Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false,
            IProgress<PremiereLoadProgress>? progress = null,
            CalendarFilters? filters = null)
        {
            throw new IOException("warmup failed");
        }

        public async IAsyncEnumerable<PremiereLoadProgress> StreamPremieresAsync(
            DateOnly start,
            DateOnly end,
            bool forceRefresh = false,
            CalendarFilters? filters = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CapturingLoadPipeline : IPremiereLoadPipeline
    {
        public List<ProviderWorkResumeState?> ResumeStates { get; } = [];

        public async IAsyncEnumerable<PremiereLoadProgress> StreamCoreAsync(
            DateOnly start,
            DateOnly end,
            bool forceRefresh,
            CalendarFilters? filters,
            ProviderWorkResumeState? resumeState,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ResumeStates.Add(resumeState);
            if (ResumeStates.Count == 1 && resumeState?.CompletedSources is IDictionary<string, IReadOnlyList<PremiereItem>> completedSources)
            {
                completedSources["first-window"] = [];
            }
            await Task.CompletedTask;
            yield return new PremiereLoadProgress("Complete", 0, 0, [], IsFinal: true);
        }
    }

    private sealed class RecordingFilterUsageStore : ICalendarFilterUsageStore
    {
        private readonly Dictionary<string, CalendarFilterUsageProfile> _profiles = new(StringComparer.Ordinal);

        public IReadOnlyList<CalendarFilterUsageProfile> TopProfiles { get; init; } = [];
        public IReadOnlyDictionary<string, CalendarFilterUsageProfile> InitialProfiles { get; init; }
            = new Dictionary<string, CalendarFilterUsageProfile>(StringComparer.Ordinal);

        public Task RecordUseAsync(
            CalendarPageMode pageMode,
            CalendarFilters filters,
            int itemCount,
            DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<CalendarFilterUsageProfile?> GetProfileAsync(string profileKey, CancellationToken cancellationToken)
        {
            _profiles.TryGetValue(profileKey, out var profile);
            if (profile is null)
            {
                InitialProfiles.TryGetValue(profileKey, out profile);
            }
            return Task.FromResult(profile);
        }

        public Task<IReadOnlyList<CalendarFilterUsageProfile>> GetTopProfilesAsync(
            int count,
            DateTimeOffset nowUtc,
            TimeSpan retention,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<CalendarFilterUsageProfile>>(TopProfiles.Take(count).ToArray());
        }

        public Task MarkWarmedAsync(
            string profileKey,
            CalendarPageMode pageMode,
            CalendarFilters filters,
            bool isDefault,
            int itemCount,
            DateTimeOffset warmedAtUtc,
            CancellationToken cancellationToken)
        {
            _profiles[profileKey] = new CalendarFilterUsageProfile(
                profileKey,
                pageMode,
                PremiereDiscoveryCriteria.FromFilters(filters).CacheKey(),
                CalendarFilterState.Clone(filters),
                UseCount: isDefault ? 0 : 1,
                LastUsedUtc: warmedAtUtc,
                LastWarmedUtc: warmedAtUtc,
                LastItemCount: itemCount,
                LastFailure: null,
                IsDefault: isDefault);
            return Task.CompletedTask;
        }

        public Task MarkWarmFailedAsync(
            string profileKey,
            CalendarPageMode pageMode,
            CalendarFilters filters,
            bool isDefault,
            string failure,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<int> CleanupAsync(
            DateTimeOffset cutoffUtc,
            IReadOnlySet<string> retainedProfileKeys,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }
    }

    private sealed class RecordingCalendarCacheMaintenance(Func<string, CalendarCacheMetadata?> getMetadata)
        : ICalendarCacheMaintenance
    {
        public List<string> RequestedKeys { get; } = [];

        public Task<CalendarCacheMetadata?> GetWeekMetadataAsync(
            DateOnly start,
            DateOnly end,
            string cacheKey,
            CancellationToken cancellationToken)
        {
            RequestedKeys.Add(cacheKey);
            return Task.FromResult(getMetadata(cacheKey));
        }

        public Task<int> CleanupAsync(DateTimeOffset nowUtc, TimeSpan retention, CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan value)
        {
            _utcNow += value;
        }
    }

    private sealed record WarmupCall(DateOnly Start, DateOnly End, bool ForceRefresh, CalendarFilters Filters);
}
