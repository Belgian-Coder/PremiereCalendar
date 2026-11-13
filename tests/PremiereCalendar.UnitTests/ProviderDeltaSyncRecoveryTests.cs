using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class ProviderDeltaSyncServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeltaProducerWaitsForInitializedSchemaAndShutdownCancelsTheWait(bool recover)
    {
        var recovery = new DatabaseRecoveryState();
        var scheduler = new DeltaRecordingScheduler();
        using var service = new ProviderDeltaSyncService(new RecordingTmdbClient(), new EmptyTvmazeClient(),
            new InMemoryProviderCacheStateStore(), new FixedOptionsMonitor<ProviderDeltaSyncOptions>(new()
                { RunOnStartup = true, StartupDelaySeconds = 0 }), TimeProvider.System,
            NullLogger<ProviderDeltaSyncService>.Instance, workScheduler: scheduler, databaseState: recovery);
        await service.StartAsync(default);
        await Assert.ThrowsAsync<TimeoutException>(() => scheduler.Enqueued.Task.WaitAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(0, scheduler.Calls);
        Assert.False(service.ExecuteTask!.IsCompleted);
        if (recover)
        {
            recovery.Set(new(DatabaseSchema.CurrentVersion, DatabaseSchema.CurrentVersion, true, "initialized", null));
            await scheduler.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(recover ? 1 : 0, scheduler.Calls);
    }

    [Theory]
    [InlineData("syntax")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("version")]
    [InlineData("identity")]
    public async Task CorruptHistoryCheckpointIsRebuiltWithoutBlockingOtherFeeds(string corruption)
    {
        var states = new InMemoryProviderCacheStateStore();
        var now = DateTimeOffset.Parse("2026-05-16T12:00:00Z");
        var json = corruption switch
        {
            "syntax" => "{",
            "missing" => "{}",
            _ => JsonSerializer.Serialize(new
            {
                Start = new DateOnly(2026, 5, 15), End = new DateOnly(2026, 5, 16),
                StartedUtc = now.AddMinutes(-1), CoverageGap = false, ChangedCount = 1,
                KnownIds = corruption == "identity" ? new[] { -1 } : [1],
                Histories = corruption == "null" ? null : new Dictionary<int, DateTimeOffset?>(),
                Version = corruption == "version" ? 2 : 1
            })
        };
        await states.SaveAsync(new("tmdb", ProviderCacheScope.Global, "movie-history-session", now, null, null, 1, json), default);
        var histories = 0;
        var tmdb = new RecordingTmdbClient
        {
            MovieChanges = [new() { Id = 1 }],
            History = (_, _) => { histories++; return Task.FromResult<DateTimeOffset?>(null); }
        };
        var tvmaze = new EmptyTvmazeClient();
        await Delta(tmdb, states, new DeltaItems(), new(), tvmaze).RunOnceAsync(default);
        Assert.Equal(1, histories);
        Assert.Equal(1, tmdb.MovieCalls);
        Assert.Equal(1, tmdb.TvCalls);
        Assert.Equal(1, tvmaze.Calls);
        Assert.NotNull(await states.GetAsync("tmdb", ProviderCacheScope.Global, "movie-changes", default));
        using var replaced = JsonDocument.Parse((await states.GetAsync("tmdb", ProviderCacheScope.Global, "movie-history-session", default))!.MetadataJson!);
        Assert.Equal(1, replaced.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(JsonValueKind.Object, replaced.RootElement.GetProperty("Histories").ValueKind);
    }

    [Fact]
    public async Task TimeBudgetAfterSuccessfulHistoryProgressDefersWithoutDroppingOtherFeeds()
    {
        var calls = new Dictionary<int, int>();
        var slow = true;
        var clock = new DeltaClock(DateTimeOffset.Parse("2026-05-16T12:00:00Z"));
        var tmdb = new RecordingTmdbClient
        {
            MovieChanges = [new() { Id = 1 }, new() { Id = 2 }],
            History = async (id, token) =>
            {
                calls[id] = calls.GetValueOrDefault(id) + 1;
                if (id == 2 && slow) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }
        };
        var tvmaze = new EmptyTvmazeClient();
        var service = Delta(tmdb, new InMemoryProviderCacheStateStore(), new DeltaItems(),
            new() { ProviderBudgetSeconds = 1, ChangeHistoryConcurrency = 1 }, tvmaze, clock);
        var continuation = await Assert.ThrowsAsync<ProviderWorkContinuationException>(() => service.RunOnceAsync(default));
        Assert.Equal(1, tmdb.TvCalls);
        Assert.Equal(1, tvmaze.Calls);
        slow = false;
        clock.Now = continuation.RetryAtUtc;
        await service.RunOnceAsync(default);
        Assert.Equal(1, calls[1]);
        Assert.Equal(2, calls[2]);
    }

    [Fact]
    public async Task SuccessfulHistoryLookupsAndHealthyMediaSurviveFailedCycleRetry()
    {
        var calls = new Dictionary<int, int>();
        var fail = true;
        var tmdb = new RecordingTmdbClient
        {
            MovieChanges = [new() { Id = 1 }, new() { Id = 2 }],
            History = (id, _) =>
            {
                calls[id] = calls.GetValueOrDefault(id) + 1;
                if (id == 2 && fail) throw new HttpRequestException("temporary provider failure");
                return Task.FromResult<DateTimeOffset?>(DateTimeOffset.Parse("2026-05-16T10:00:00Z"));
            }
        };
        var states = new InMemoryProviderCacheStateStore();
        var service = Delta(tmdb, states, new DeltaItems(), new() { UseTvmazeUpdates = false, ChangeHistoryConcurrency = 1 });
        await Assert.ThrowsAsync<ExternalApiException>(() => service.RunOnceAsync(default));
        fail = false;
        await service.RunOnceAsync(default);
        Assert.Equal(1, calls[1]);
        Assert.Equal(2, calls[2]);
        Assert.Equal(1, tmdb.MovieCalls);
        Assert.Equal(1, tmdb.TvCalls);
        Assert.NotNull(await states.GetAsync("tmdb", ProviderCacheScope.Global, "movie-changes", default));
    }

    [Fact]
    public async Task DirtyKnownItemsSkipHistoryAndStillReceiveNewInvalidationFence()
    {
        var fetched = new List<int>();
        var items = new DeltaItems { Dirty = [1] };
        var tmdb = new RecordingTmdbClient
        {
            MovieChanges = [new() { Id = 1 }, new() { Id = 2 }],
            History = (id, _) =>
            {
                fetched.Add(id);
                return Task.FromResult<DateTimeOffset?>(DateTimeOffset.Parse("2026-05-16T10:00:00Z"));
            }
        };
        await Delta(tmdb, new InMemoryProviderCacheStateStore(), items,
            new() { UseTvmazeUpdates = false }).RunOnceAsync(default);
        Assert.Equal([2], fetched);
        Assert.Equal(DateTimeOffset.Parse("2026-05-16T12:00:00Z"), items.Invalidations[1]);
        Assert.Equal(DateTimeOffset.Parse("2026-05-16T10:00:00Z"), items.Invalidations[2]);
    }

    [Fact]
    public async Task HistoryItemBudgetPersistsProgressAndResumesNextCycle()
    {
        var fetched = new List<int>();
        var states = new InMemoryProviderCacheStateStore();
        var clock = new DeltaClock(DateTimeOffset.Parse("2026-05-16T12:00:00Z"));
        var tmdb = new RecordingTmdbClient
        {
            MovieChanges = [new() { Id = 1 }, new() { Id = 2 }],
            History = (id, _) => { fetched.Add(id); return Task.FromResult<DateTimeOffset?>(null); }
        };
        var service = Delta(tmdb, states, new DeltaItems(),
            new() { UseTvmazeUpdates = false, HistoryMaximumItemsPerCycle = 1 }, clock: clock);
        var continuation = await Assert.ThrowsAsync<ProviderWorkContinuationException>(() => service.RunOnceAsync(default));
        Assert.Null(await states.GetAsync("tmdb", ProviderCacheScope.Global, "movie-changes", default));
        Assert.Equal(clock.GetUtcNow().AddSeconds(60), continuation.RetryAtUtc);
        await Assert.ThrowsAsync<ProviderWorkContinuationException>(() => service.RunOnceAsync(default));
        Assert.Equal([1], fetched);
        clock.Now = continuation.RetryAtUtc;
        await service.RunOnceAsync(default);
        Assert.Equal([1, 2], fetched);
        Assert.Equal(1, tmdb.MovieCalls);
    }

    [Fact]
    public async Task StandaloneHistoryBudgetIsRecordedAsDeferredProgressWithoutFailure()
    {
        var timeline = new BackgroundJobTimelineService(new InMemoryAppStateStore(), TimeProvider.System);
        var tmdb = new RecordingTmdbClient { MovieChanges = [new() { Id = 1 }, new() { Id = 2 }] };
        var service = new ProviderDeltaSyncService(tmdb, new EmptyTvmazeClient(), new InMemoryProviderCacheStateStore(),
            new FixedOptionsMonitor<ProviderDeltaSyncOptions>(new() { HistoryMaximumItemsPerCycle = 1, UseTvmazeUpdates = false }),
            TimeProvider.System, NullLogger<ProviderDeltaSyncService>.Instance, timeline: timeline, itemEnrichmentStore: new DeltaItems());
        await service.RunOnceSafelyAsync(default);
        var events = await timeline.GetRecentAsync(default);
        Assert.Contains(events, entry => entry.Status == BackgroundJobStatus.Skipped && entry.Message.Contains("saved progress", StringComparison.Ordinal));
        Assert.DoesNotContain(events, entry => entry.Status == BackgroundJobStatus.Failed);
    }

    [Fact]
    public async Task TvmazeMarkersAndInvalidationsAreRestrictedToLocallyKnownShows()
    {
        var states = new InMemoryProviderCacheStateStore();
        var items = new DeltaItems { KnownTvmaze = [7] };
        var tvmaze = new EmptyTvmazeClient
        {
            Updates = [new(7, DateTimeOffset.Parse("2026-05-16T10:00:00Z")),
                new(9, DateTimeOffset.Parse("2026-05-16T11:00:00Z"))]
        };
        var service = Delta(new RecordingTmdbClient(), states, items,
            new() { UseTmdbChanges = false }, tvmaze);
        await service.RunOnceAsync(default);
        var itemStates = (await states.GetRecentAsync(100, default)).Where(state => state.Scope == ProviderCacheScope.Item);
        Assert.Equal("show:7", Assert.Single(itemStates).Key);
        Assert.Equal([7], items.TvmazeInvalidations.Keys);
        Assert.Equal(2, (await states.GetAsync("tvmaze", ProviderCacheScope.Global, "show-updates", default))!.ItemCount);
        await service.RunOnceAsync(default);
        Assert.Equal(1, tvmaze.Calls);
    }

    [Fact]
    public async Task UnexpectedProgrammingErrorIsNotConvertedIntoProviderRetry()
    {
        var tmdb = new RecordingTmdbClient
        {
            MovieChanges = [new() { Id = 1 }],
            History = (_, _) => throw new InvalidOperationException("code defect")
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Delta(tmdb, new InMemoryProviderCacheStateStore(),
            new DeltaItems(), new() { UseTvmazeUpdates = false }).RunOnceSafelyAsync(default));
    }

    [Fact]
    public async Task MovieStoreDeadlineDoesNotStopOtherFeedsAndStandaloneNextCycleRecovers()
    {
        var tmdb = new RecordingTmdbClient { MovieChanges = [new() { Id = 1 }] };
        var tvmaze = new EmptyTvmazeClient();
        var items = new DeltaItems { TimeoutMovieLookup = true };
        var service = Delta(tmdb, new InMemoryProviderCacheStateStore(), items, new(), tvmaze);
        await service.RunOnceSafelyAsync(default);
        Assert.Equal(1, tmdb.TvCalls);
        Assert.Equal(1, tvmaze.Calls);
        await service.RunOnceAsync(default);
        Assert.Equal(2, tmdb.MovieCalls);
        Assert.Equal(1, tmdb.TvCalls);
        Assert.Equal(1, tvmaze.Calls);
        Assert.Contains(1, items.Invalidations.Keys);
    }

    private static ProviderDeltaSyncService Delta(RecordingTmdbClient tmdb, InMemoryProviderCacheStateStore states,
        DeltaItems items, ProviderDeltaSyncOptions options, EmptyTvmazeClient? tvmaze = null, TimeProvider? clock = null)
        => new(tmdb, tvmaze ?? new EmptyTvmazeClient(), states,
            new FixedOptionsMonitor<ProviderDeltaSyncOptions>(options),
            clock ?? new FixedTimeProvider(DateTimeOffset.Parse("2026-05-16T12:00:00Z")),
            NullLogger<ProviderDeltaSyncService>.Instance, itemEnrichmentStore: items);

    private sealed class DeltaClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class DeltaRecordingScheduler : IProviderWorkScheduler
    {
        public TaskCompletionSource Enqueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public Task<ProviderWorkHandle> EnqueueAsync(ProviderWorkRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Enqueued.TrySetResult();
            return Task.FromResult(new ProviderWorkHandle("delta", true));
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

    private sealed class DeltaItems : IItemEnrichmentStore
    {
        public HashSet<int> Dirty { get; init; } = [];
        public HashSet<int> KnownTvmaze { get; init; } = [];
        public Dictionary<int, DateTimeOffset> Invalidations { get; } = [];
        public Dictionary<int, DateTimeOffset> TvmazeInvalidations { get; } = [];
        public bool TimeoutMovieLookup;
        public Task<IReadOnlySet<int>> FindKnownTmdbIdsAsync(PremiereMediaType mediaType, IReadOnlyCollection<int> candidateIds, CancellationToken cancellationToken)
        {
            if (mediaType == PremiereMediaType.Movie && TimeoutMovieLookup)
            {
                TimeoutMovieLookup = false;
                throw new TimeoutException("item-store operation deadline reached");
            }
            return Task.FromResult<IReadOnlySet<int>>(candidateIds.ToHashSet());
        }
        public Task<IReadOnlySet<int>> FindDirtyTmdbIdsAsync(PremiereMediaType mediaType, IReadOnlyCollection<int> candidateIds, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlySet<int>>(Dirty.Intersect(candidateIds).ToHashSet());
        public Task<IReadOnlySet<int>> FindKnownTvmazeIdsAsync(IReadOnlyCollection<int> candidateIds, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlySet<int>>(KnownTvmaze.Intersect(candidateIds).ToHashSet());
        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadManyAsync(string partition, IReadOnlyCollection<ItemEnrichmentIdentity> identities, bool reserveMissing, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<int> WriteManyAsync(string partition, IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<int> InvalidateTmdbAsync(PremiereMediaType mediaType, IReadOnlyCollection<int> tmdbIds, DateTimeOffset changedAtUtc, CancellationToken cancellationToken)
        {
            foreach (var id in tmdbIds) Invalidations[id] = changedAtUtc;
            return Task.FromResult(tmdbIds.Count);
        }
        public Task<int> InvalidateTvmazeAsync(IReadOnlyCollection<int> showIds, DateTimeOffset changedAtUtc, CancellationToken cancellationToken)
        {
            foreach (var id in showIds) TvmazeInvalidations[id] = changedAtUtc;
            return Task.FromResult(showIds.Count);
        }
        public Task<int> CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
