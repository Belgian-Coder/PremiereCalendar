using System.Data.Common;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public enum ProviderWorkKind
{
    CalendarForeground,
    AdjacentWeekPrefetch,
    CalendarWarmup,
    ProviderDeltaSync,
    ImdbDatasetRefresh
}

public enum ProviderWorkPriority
{
    Foreground = 0,
    ActiveWeek = 100,
    Adjacent = 200,
    Warmup = 300,
    Maintenance = 400
}

public enum ProviderWorkState
{
    Queued,
    Running,
    RetryWaiting,
    Completed,
    Failed,
    Cancelled
}

public sealed record ProviderWorkRequest(
    ProviderWorkKind Kind,
    string DedupeKey,
    ProviderWorkPriority Priority,
    string PayloadJson);

public sealed record ProviderWorkHandle(string JobId, bool Created);

public sealed record ProviderWorkProgress(
    string JobId,
    ProviderWorkState State,
    string Message,
    int? CompletedWork = null,
    int? TotalWork = null,
    PremiereLoadProgress? CalendarProgress = null,
    string? Error = null);

public sealed record CalendarProviderWorkPayload(
    DateOnly Start,
    DateOnly End,
    bool ForceRefresh,
    CalendarFilters? Filters);

public sealed record ProviderWorkResumeState(
    IReadOnlyDictionary<string, IReadOnlyList<PremiereItem>> CompletedSources,
    PremiereLoadProgress? LatestProgress)
{
    // The callback may publish local cache files only. Provider/network work and
    // other database connections must stay outside its ownership transaction.
    internal Func<Func<CancellationToken, Task>, CancellationToken, Task>? PublishCacheAsync { get; init; }
}

internal sealed record ProviderWorkCheckpoint(
    string Message,
    int? CompletedWork,
    int? TotalWork,
    PremiereLoadProgress? CalendarProgress,
    IReadOnlyDictionary<string, IReadOnlyList<PremiereItem>> CompletedSources)
{
    public int Version { get; init; } = 3;
    public IReadOnlyDictionary<string, DateTimeOffset> SourceCompletedUtc { get; init; }
        = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, ProviderWorkProgress> SourceSummaries { get; init; }
        = new Dictionary<string, ProviderWorkProgress>(StringComparer.Ordinal);
}

public interface IProviderWorkScheduler
{
    Task<ProviderWorkHandle> EnqueueAsync(ProviderWorkRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ProviderWorkProgress> WatchAsync(
        ProviderWorkHandle handle,
        CancellationToken cancellationToken = default);

    Task<ProviderWorkSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default);
}

public sealed record ProviderWorkSnapshot(
    string JobId,
    ProviderWorkKind Kind,
    string DedupeKey,
    ProviderWorkPriority Priority,
    string PayloadJson,
    string? CheckpointJson,
    ProviderWorkState State,
    int AttemptCount,
    DateTimeOffset EnqueuedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    DateTimeOffset? NextAttemptUtc,
    string? LastError,
    string? LeaseOwner = null);

internal sealed class ProviderWorkLeaseLostException(string jobId)
    : OperationCanceledException($"Provider work claim {jobId} is no longer owned by this worker.");

public sealed class ProviderWorkScheduler : IProviderWorkScheduler
{
    private static readonly JsonSerializerOptions CheckpointJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly ProviderWorkStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ProviderSchedulerOptions _options;
    private readonly CalendarCacheOptions _cacheOptions;
    private readonly PremiereTelemetry _telemetry;
    private readonly ConcurrentDictionary<string, JobBroadcast> _broadcasts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExecutionCheckpoint> _executions = new(StringComparer.Ordinal);
    private readonly object _broadcastGate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _preemptionGate = new();
    private CancellationTokenSource? _activeBackgroundCancellation;
    private readonly Random _jitter = new();

    public ProviderWorkScheduler(
        ProviderWorkStore store,
        TimeProvider timeProvider,
        IOptions<ProviderSchedulerOptions> options,
        PremiereTelemetry? telemetry = null,
        IOptions<CalendarCacheOptions>? cacheOptions = null)
    {
        _store = store;
        _timeProvider = timeProvider;
        _options = options.Value;
        _cacheOptions = cacheOptions?.Value ?? new CalendarCacheOptions();
        _telemetry = telemetry ?? new PremiereTelemetry();
    }

    public async Task<ProviderWorkHandle> EnqueueAsync(
        ProviderWorkRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DedupeKey))
        {
            throw new ArgumentException("Provider work requires a deduplication key.", nameof(request));
        }

        var result = await _store.EnqueueAsync(request, _timeProvider.GetUtcNow(), cancellationToken);
        if (request.Priority <= ProviderWorkPriority.ActiveWeek)
        {
            lock (_preemptionGate) _activeBackgroundCancellation?.Cancel();
        }

        if (result.Created) _signal.Release();
        if (result.Created) await RefreshCountsAsync(cancellationToken);
        return result;
    }

    public async IAsyncEnumerable<ProviderWorkProgress> WatchAsync(
        ProviderWorkHandle handle,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        JobBroadcast broadcast;
        ChannelReader<ProviderWorkProgress> reader;
        lock (_broadcastGate)
        {
            broadcast = _broadcasts.GetOrAdd(handle.JobId, _ => new JobBroadcast(_timeProvider));
            reader = broadcast.Subscribe();
        }
        try
        {
            await _store.SetForegroundInterestAsync(handle.JobId, true, cancellationToken);
            var snapshot = await _store.GetAsync(handle.JobId, cancellationToken);
            if (snapshot is null) yield break;
            broadcast.Seed(snapshot);
            _signal.Release();
            await foreach (var update in reader.ReadAllAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                if (IsTerminal(update.State)) yield break;
            }
        }
        finally
        {
            bool lastSubscriber;
            lock (_broadcastGate)
            {
                broadcast.Unsubscribe(reader);
                lastSubscriber = broadcast.SubscriberCount == 0;
                if (lastSubscriber) _broadcasts.TryRemove(handle.JobId, out _);
            }
            if (lastSubscriber)
            {
                await RefreshQueuedForegroundInterestAsync(handle.JobId);
            }
        }
    }

    public Task<ProviderWorkSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default)
        => _store.GetAsync(jobId, cancellationToken);

    internal async Task<ProviderWorkSnapshot?> ClaimNextAsync(bool foregroundOnly, string leaseOwner, CancellationToken cancellationToken)
    {
        var job = await _store.ClaimNextAsync(
            foregroundOnly,
            leaseOwner,
            _timeProvider.GetUtcNow(),
            TimeSpan.FromSeconds(Math.Clamp(_options.LeaseSeconds, 30, 900)),
            cancellationToken);
        if (job is not null)
        {
            _executions[job.LeaseOwner!] = new ExecutionCheckpoint(ReadResumeCheckpoint(job));
            _telemetry.RecordJobWait(job.Kind.ToString(), _timeProvider.GetUtcNow() - job.EnqueuedUtc, !string.IsNullOrWhiteSpace(job.CheckpointJson));
            await RefreshCountsAsync(cancellationToken);
        }
        return job;
    }

    internal CancellationTokenSource BeginExecution(ProviderWorkSnapshot job, CancellationToken stoppingToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        GetExecution(job).Cancellation = linked;
        if (job.Priority > ProviderWorkPriority.ActiveWeek)
        {
            lock (_preemptionGate) _activeBackgroundCancellation = linked;
        }
        return linked;
    }

    internal void EndExecution(ProviderWorkSnapshot job, CancellationTokenSource execution)
    {
        if (job.Priority > ProviderWorkPriority.ActiveWeek)
        {
            lock (_preemptionGate)
            {
                if (ReferenceEquals(_activeBackgroundCancellation, execution)) _activeBackgroundCancellation = null;
            }
        }
        execution.Dispose();
        if (job.LeaseOwner is { } owner) _executions.TryRemove(owner, out _);
    }

    internal async Task PublishAsync(
        ProviderWorkSnapshot job,
        ProviderWorkProgress progress,
        CancellationToken cancellationToken)
    {
        var execution = GetExecution(job);
        await execution.Gate.WaitAsync(cancellationToken);
        try
        {
            var calendar = progress.CalendarProgress;
            if (calendar is { Phase: "complete", IsFinal: false, HasSourceErrors: false, CheckpointKey: { Length: > 0 } key })
            {
                execution.CompletedSources[key] = calendar.SourceItems;
                execution.SourceCompletedUtc[key] = _timeProvider.GetUtcNow();
            }
            else if (calendar is { HasSourceErrors: true, CheckpointKey: { Length: > 0 } failedKey })
            {
                execution.CompletedSources.Remove(failedKey);
                execution.SourceCompletedUtc.Remove(failedKey);
            }
            if (calendar is not null && IsSourceSummary(calendar))
            {
                execution.SourceSummaries[SummaryKey(calendar)] = progress;
            }
            execution.Latest = progress;
            execution.Dirty = true;
            var now = _timeProvider.GetUtcNow();
            if (calendar is { IsFinal: true } || calendar is not null && IsSourceSummary(calendar)
                || execution.LastPersistedUtc is null || now - execution.LastPersistedUtc >= TimeSpan.FromSeconds(2))
            {
                await FlushAsync(job, execution, cancellationToken);
            }
        }
        finally { execution.Gate.Release(); }
        Publish(job.JobId, progress);
    }

    internal void ReplayPersistedProgress(ProviderWorkSnapshot job, PremiereLoadProgress persisted)
        => Publish(job.JobId, new ProviderWorkProgress(job.JobId, ProviderWorkState.Running,
            "Resumed persisted provider progress.", persisted.CompletedWork, persisted.TotalWork,
            persisted with { IsFinal = false, Phase = "cache", IsStaleCache = true }));

    internal async Task CompleteAsync(ProviderWorkSnapshot job, CancellationToken cancellationToken)
    {
        if (!await FlushAsync(job, cancellationToken)) return;
        var now = _timeProvider.GetUtcNow();
        if (GetExecution(job).Latest?.CalendarProgress?.RetryAfterUtc is { } retryAfter && retryAfter > now)
        {
            if (!await _store.RequeueAsync(job.JobId, RequireOwner(job), now, retryAfter, false,
                    "Provider work is waiting for provider recovery.", cancellationToken, retryWaiting: true)) return;
            await RefreshQueuedForegroundInterestAsync(job.JobId);
            await RefreshCountsAsync(cancellationToken);
            _telemetry.RecordJob(job.Kind.ToString(), "deferred", Elapsed(job));
            Publish(job.JobId, new ProviderWorkProgress(job.JobId, ProviderWorkState.RetryWaiting,
                "Provider work will resume after provider recovery."));
            return;
        }
        if (!await _store.CompleteAsync(job.JobId, RequireOwner(job), now, cancellationToken)) return;
        await RefreshCountsAsync(cancellationToken);
        _telemetry.RecordJob(job.Kind.ToString(), "completed", Elapsed(job));
        Publish(job.JobId,
            new ProviderWorkProgress(job.JobId, ProviderWorkState.Completed, "Provider work completed."));
    }

    internal async Task PreemptAsync(ProviderWorkSnapshot job, CancellationToken cancellationToken)
    {
        if (!await FlushAsync(job, cancellationToken)) return;
        var now = _timeProvider.GetUtcNow();
        if (!await _store.RequeueAsync(job.JobId, RequireOwner(job), now, now, incrementAttempt: false, null, cancellationToken)) return;
        await RefreshQueuedForegroundInterestAsync(job.JobId);
        await RefreshCountsAsync(cancellationToken);
        _telemetry.RecordJob(job.Kind.ToString(), "preempted", Elapsed(job));
        Publish(job.JobId,
            new ProviderWorkProgress(job.JobId, ProviderWorkState.Queued, "Provider work was checkpointed for foreground activity."));
        _signal.Release();
    }

    internal async Task FailAsync(ProviderWorkSnapshot job, Exception error, CancellationToken cancellationToken)
    {
        var sanitized = SanitizeFailure(error);
        if (!await FlushAsync(job, cancellationToken)) return;
        var currentTime = _timeProvider.GetUtcNow();
        if (RetryDeadline(error, currentTime) is { } retryDeadline)
        {
            if (!await _store.RequeueAsync(job.JobId, RequireOwner(job), currentTime, retryDeadline, false,
                    sanitized, cancellationToken, retryWaiting: true)) return;
            await RefreshQueuedForegroundInterestAsync(job.JobId);
            await RefreshCountsAsync(cancellationToken);
            Publish(job.JobId, new ProviderWorkProgress(job.JobId, ProviderWorkState.RetryWaiting,
                "Provider work will resume after provider recovery.", Error: sanitized));
            return;
        }
        if (job.AttemptCount + 1 < Math.Clamp(_options.MaximumAttempts, 1, 20))
        {
            var exponentialMilliseconds = Math.Min(
                Math.Clamp(_options.RetryMaximumSeconds, 1, 300) * 1_000d,
                Math.Clamp(_options.RetryBaseMilliseconds, 50, 30_000) * Math.Pow(2, job.AttemptCount));
            var delay = TimeSpan.FromMilliseconds(exponentialMilliseconds * (0.75 + (_jitter.NextDouble() * 0.5)));
            var now = _timeProvider.GetUtcNow();
            if (!await _store.RequeueAsync(job.JobId, RequireOwner(job), now, now.Add(delay), true, sanitized, cancellationToken)) return;
            await RefreshQueuedForegroundInterestAsync(job.JobId);
            await RefreshCountsAsync(cancellationToken);
            _telemetry.RecordJob(job.Kind.ToString(), "retry", Elapsed(job));
            Publish(job.JobId,
                new ProviderWorkProgress(job.JobId, ProviderWorkState.RetryWaiting, "Provider work will retry.", Error: sanitized));
            _signal.Release();
            return;
        }

        if (!await _store.FailAsync(job.JobId, RequireOwner(job), sanitized, _timeProvider.GetUtcNow(), cancellationToken)) return;
        await RefreshCountsAsync(cancellationToken);
        _telemetry.RecordJob(job.Kind.ToString(), "failed", Elapsed(job));
        Publish(job.JobId,
            new ProviderWorkProgress(job.JobId, ProviderWorkState.Failed, "Provider work failed.", Error: sanitized));
    }

    internal async Task WaitForSignalAsync(CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await _store.RecoverExpiredLeasesAsync(_timeProvider.GetUtcNow(), cancellationToken);
        await RefreshCountsAsync(cancellationToken);
    }

    internal Task CleanupAsync(CancellationToken cancellationToken)
        => _store.DeleteCompletedBeforeAsync(
            _timeProvider.GetUtcNow().AddDays(-Math.Clamp(_options.CompletedRetentionDays, 1, 90)),
            cancellationToken);

    internal int BroadcastCount => _broadcasts.Count;
    internal TimeSpan LeaseDuration => TimeSpan.FromSeconds(Math.Clamp(_options.LeaseSeconds, 30, 900));

    internal async Task PublishOwnedCacheAsync(
        ProviderWorkSnapshot job, Func<CancellationToken, Task> publish, CancellationToken cancellationToken)
    {
        if (await _store.WithOwnedLeaseAsync(job.JobId, RequireOwner(job), _timeProvider,
                LeaseDuration, publish, cancellationToken)) return;
        GetExecution(job).Cancellation?.Cancel();
        throw new ProviderWorkLeaseLostException(job.JobId);
    }

    internal async Task MaintainLeaseAsync(ProviderWorkSnapshot job, CancellationTokenSource execution)
    {
        try
        {
            while (!execution.IsCancellationRequested)
            {
                await Task.Delay(LeaseDuration / 3, _timeProvider, execution.Token);
                var now = _timeProvider.GetUtcNow();
                if (!await _store.RenewLeaseAsync(job.JobId, RequireOwner(job), now, now.Add(LeaseDuration), execution.Token))
                {
                    execution.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
        catch (DbException exception)
        {
            _telemetry.RecordDatabaseException(exception);
            execution.Cancel();
        }
    }

    private ExecutionCheckpoint GetExecution(ProviderWorkSnapshot job)
        => _executions.GetOrAdd(RequireOwner(job), _ => new ExecutionCheckpoint(ReadResumeCheckpoint(job)));

    internal ProviderWorkCheckpoint? ReadResumeCheckpoint(ProviderWorkSnapshot job)
    {
        var checkpoint = TryReadCheckpoint(job.CheckpointJson);
        if (checkpoint is null) return null;
        var now = _timeProvider.GetUtcNow();
        var maximumAge = TimeSpan.FromHours(Math.Max(1, _cacheOptions.WeekCacheHours));
        var completed = checkpoint.CompletedSources.Where(pair =>
                checkpoint.SourceCompletedUtc.TryGetValue(pair.Key, out var completedUtc)
                && completedUtc <= now && now - completedUtc < maximumAge)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return checkpoint with
        {
            CompletedSources = completed,
            SourceCompletedUtc = checkpoint.SourceCompletedUtc.Where(pair => completed.ContainsKey(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
    }

    private static string RequireOwner(ProviderWorkSnapshot job)
        => job.LeaseOwner ?? throw new InvalidOperationException("Provider work has no claim token.");

    private async Task<bool> FlushAsync(ProviderWorkSnapshot job, CancellationToken cancellationToken)
    {
        var execution = GetExecution(job);
        await execution.Gate.WaitAsync(cancellationToken);
        try { await FlushAsync(job, execution, cancellationToken); return true; }
        catch (ProviderWorkLeaseLostException) { return false; }
        finally { execution.Gate.Release(); }
    }

    private async Task FlushAsync(ProviderWorkSnapshot job, ExecutionCheckpoint execution, CancellationToken cancellationToken)
    {
        if (!execution.Dirty || execution.Latest is not { } latest) return;
        var checkpoint = JsonSerializer.Serialize(new ProviderWorkCheckpoint(
            latest.Message, latest.CompletedWork, latest.TotalWork,
            latest.CalendarProgress is { } calendar ? calendar with { SourceItems = [] } : null,
            execution.CompletedSources)
        {
            SourceCompletedUtc = execution.SourceCompletedUtc,
            // The latest calendar stores the canonical Items snapshot, and the
            // successful source map stores resumable items. Summaries need only
            // their counters and outcome; copying those arrays into every summary
            // makes checkpoints grow quadratically with the number of sources.
            SourceSummaries = execution.SourceSummaries.ToDictionary(pair => pair.Key,
                pair => pair.Value with
                {
                    CalendarProgress = pair.Value.CalendarProgress is { } summary
                        ? summary with { Items = [], SourceItems = [] } : null
                }, StringComparer.Ordinal)
        }, CheckpointJsonOptions);
        if (!await _store.UpdateProgressAsync(job.JobId, RequireOwner(job), checkpoint, _timeProvider.GetUtcNow(), cancellationToken))
        {
            execution.Cancellation?.Cancel();
            throw new ProviderWorkLeaseLostException(job.JobId);
        }
        execution.Dirty = false;
        execution.LastPersistedUtc = _timeProvider.GetUtcNow();
    }

    private void Publish(string jobId, ProviderWorkProgress progress)
    {
        lock (_broadcastGate)
        {
            if (_broadcasts.TryGetValue(jobId, out var broadcast)) broadcast.Publish(progress);
        }
    }

    private async Task RefreshQueuedForegroundInterestAsync(string jobId)
    {
        try
        {
            while (true)
            {
                bool hasWatcher;
                lock (_broadcastGate)
                    hasWatcher = _broadcasts.TryGetValue(jobId, out var broadcast) && broadcast.SubscriberCount > 0;
                await _store.SetForegroundInterestAsync(jobId, hasWatcher, CancellationToken.None);
                lock (_broadcastGate)
                {
                    var current = _broadcasts.TryGetValue(jobId, out var broadcast) && broadcast.SubscriberCount > 0;
                    if (current == hasWatcher) return;
                }
            }
        }
        catch (Exception exception) when (exception is DbException or IOException)
        {
            // Requeue and view cleanup have already succeeded. Interest changes
            // are best effort and must not replace the delivered outcome.
            _telemetry.RecordDatabaseException(exception);
        }
    }

    private static bool IsSourceSummary(PremiereLoadProgress progress)
        => !progress.IsFinal && (progress.Phase == "complete" || progress.HasSourceErrors && progress.Phase != "cache");

    private static string SummaryKey(PremiereLoadProgress progress)
        => $"{progress.CheckpointKey ?? progress.SourceName}:{(progress.HasSourceErrors ? "failure" : "complete")}";

    private sealed class ExecutionCheckpoint(ProviderWorkCheckpoint? checkpoint)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, IReadOnlyList<PremiereItem>> CompletedSources { get; } = checkpoint?.CompletedSources is { } sources
            ? new(sources, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> SourceCompletedUtc { get; } = checkpoint?.SourceCompletedUtc is { } times
            ? new(times, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        public Dictionary<string, ProviderWorkProgress> SourceSummaries { get; } = checkpoint?.SourceSummaries is { } summaries
            ? new(summaries, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        public ProviderWorkProgress? Latest { get; set; } = checkpoint is null ? null : new(
            string.Empty, ProviderWorkState.Running, checkpoint.Message, checkpoint.CompletedWork, checkpoint.TotalWork, checkpoint.CalendarProgress);
        public bool Dirty { get; set; }
        public DateTimeOffset? LastPersistedUtc { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
    }

    private static bool IsTerminal(ProviderWorkState state)
        => state is ProviderWorkState.Completed or ProviderWorkState.Failed or ProviderWorkState.Cancelled;

    private static bool TryReadPersistedProgress(ProviderWorkSnapshot snapshot, DateTimeOffset now, out ProviderWorkProgress progress)
    {
        progress = default!;
        if (string.IsNullOrWhiteSpace(snapshot.CheckpointJson)) return false;
        try
        {
            var checkpoint = TryReadCheckpoint(snapshot.CheckpointJson);
            if (checkpoint is null) return false;
            progress = new ProviderWorkProgress(
                snapshot.JobId,
                snapshot.State,
                checkpoint.Message,
                checkpoint.CompletedWork,
                checkpoint.TotalWork,
                NormalizeDeferredProgress(checkpoint.CalendarProgress, snapshot, now));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static ProviderWorkCheckpoint? TryReadCheckpoint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var checkpoint = JsonSerializer.Deserialize<ProviderWorkCheckpoint>(json);
            if (checkpoint is null) return null;
            // Version 2 distinguishes successful outcomes but has no source age.
            // It remains useful for display; only dated version 3 sources can skip
            // fetching on a later claim.
            if (document.RootElement.TryGetProperty("Version", out var version) && version.TryGetInt32(out var value) && value is 2 or 3)
            {
                var canonicalItems = checkpoint.CalendarProgress?.Items ?? [];
                PremiereLoadProgress Restore(PremiereLoadProgress progress) => progress with
                {
                    SourceItems = progress.IsFinal ? progress.Items
                        : progress.CheckpointKey is { } key && checkpoint.CompletedSources.TryGetValue(key, out var items)
                            ? items : progress.SourceItems
                };
                return checkpoint with
                {
                    CalendarProgress = checkpoint.CalendarProgress is { } latest ? Restore(latest) : null,
                    SourceSummaries = checkpoint.SourceSummaries.ToDictionary(pair => pair.Key,
                        pair => pair.Value with
                        {
                            CalendarProgress = pair.Value.CalendarProgress is { } summary
                                ? Restore(summary with { Items = canonicalItems, TotalItemCount = canonicalItems.Count }) : null
                        }, StringComparer.Ordinal),
                    CompletedSources = value == 3
                        ? checkpoint.CompletedSources.Where(pair => checkpoint.SourceCompletedUtc.ContainsKey(pair.Key))
                            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                        : new Dictionary<string, IReadOnlyList<PremiereItem>>(StringComparer.Ordinal),
                    SourceCompletedUtc = value == 3 ? checkpoint.SourceCompletedUtc
                        : new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
                };
            }
            return checkpoint with
                {
                    CompletedSources = new Dictionary<string, IReadOnlyList<PremiereItem>>(StringComparer.Ordinal),
                    SourceCompletedUtc = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal),
                    SourceSummaries = new Dictionary<string, ProviderWorkProgress>(StringComparer.Ordinal)
                };
        }
        catch (JsonException) { return null; }
    }

    private static PremiereLoadProgress? NormalizeDeferredProgress(
        PremiereLoadProgress? progress, ProviderWorkSnapshot snapshot, DateTimeOffset now)
        => progress is { IsFinal: true, RetryAfterUtc: { } deadline }
            && (snapshot.State is ProviderWorkState.Running or ProviderWorkState.Queued
                || snapshot.State == ProviderWorkState.RetryWaiting && deadline <= now)
                ? progress with { IsFinal = false, Phase = "cache", IsStaleCache = true }
                : progress;

    private TimeSpan Elapsed(ProviderWorkSnapshot job) => job.StartedUtc is { } started
        ? _timeProvider.GetUtcNow() - started
        : TimeSpan.Zero;

    private async Task RefreshCountsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var counts = await _store.GetStateCountsAsync(cancellationToken);
            var queued = GetCount(counts, ProviderWorkState.Queued) + GetCount(counts, ProviderWorkState.RetryWaiting);
            _telemetry.SetSchedulerCounts(queued, GetCount(counts, ProviderWorkState.Running));
        }
        catch (Exception exception) when (exception is DbException or OperationCanceledException)
        {
            _telemetry.RecordDatabaseException(exception);
        }
    }

    private static int GetCount(IReadOnlyDictionary<string, int> counts, ProviderWorkState state)
        => counts.TryGetValue(state.ToString(), out var count) ? count : 0;

    private static string SanitizeFailure(Exception error) => error switch
    {
        OperationCanceledException => "Provider work was canceled.",
        HttpRequestException => "A provider request failed.",
        DbException => "Provider work persistence failed.",
        _ => string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message
    };

    private static DateTimeOffset? RetryDeadline(Exception error, DateTimeOffset now)
    {
        DateTimeOffset? direct = error switch
        {
            ProviderCircuitOpenException circuit => circuit.RetryAtUtc,
            ProviderRetryAfterException retry => retry.RetryAtUtc,
            _ => null
        };
        var children = error is AggregateException aggregate ? aggregate.InnerExceptions
            : error.InnerException is { } inner ? [inner] : Enumerable.Empty<Exception>();
        return children.Select(child => RetryDeadline(child, now)).Append(direct)
            .Where(value => value > now).Min();
    }

    internal sealed class JobBroadcast(TimeProvider? timeProvider = null)
    {
        private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
        private readonly object _gate = new();
        private readonly HashSet<Subscription> _subscribers = [];
        private readonly Dictionary<string, (long Sequence, ProviderWorkProgress Progress)> _retained = new(StringComparer.Ordinal);
        private long _sequence;
        private bool _terminal;
        public int SubscriberCount { get { lock (_gate) return _subscribers.Count; } }

        public ChannelReader<ProviderWorkProgress> Subscribe()
        {
            lock (_gate)
            {
                var subscriber = new Subscription(this);
                _subscribers.Add(subscriber);
                foreach (var (key, progress) in _retained) subscriber.Pending[key] = progress;
                subscriber.Notify();
                return subscriber;
            }
        }

        public void Unsubscribe(ChannelReader<ProviderWorkProgress> reader)
        {
            lock (_gate)
            {
                if (reader is Subscription subscriber)
                {
                    _subscribers.Remove(subscriber);
                    subscriber.Detached = true;
                    subscriber.Notify();
                }
            }
        }

        public void Seed(ProviderWorkSnapshot snapshot)
        {
            lock (_gate)
            {
                var checkpoint = TryReadCheckpoint(snapshot.CheckpointJson);
                var now = _timeProvider.GetUtcNow();
                foreach (var pair in _retained.ToArray())
                {
                    var normalized = NormalizeDeferredProgress(pair.Value.Progress.CalendarProgress, snapshot, now);
                    if (!ReferenceEquals(normalized, pair.Value.Progress.CalendarProgress))
                        _retained[pair.Key] = (pair.Value.Sequence, pair.Value.Progress with { CalendarProgress = normalized });
                }
                foreach (var subscriber in _subscribers)
                {
                    foreach (var pair in subscriber.Pending.ToArray())
                    {
                        var normalized = NormalizeDeferredProgress(pair.Value.Progress.CalendarProgress, snapshot, now);
                        if (!ReferenceEquals(normalized, pair.Value.Progress.CalendarProgress))
                            subscriber.Pending[pair.Key] = (pair.Value.Sequence, pair.Value.Progress with { CalendarProgress = normalized });
                    }
                }
                // Subscription precedes the database read. Preserve a racing live
                // canonical snapshot, but still recover earlier durable summaries.
                if (_sequence != 0)
                {
                    if (checkpoint is not null)
                    {
                        long replaySequence = -checkpoint.SourceSummaries.Count - 1;
                        foreach (var summary in checkpoint.SourceSummaries.Values)
                        {
                            if (summary.CalendarProgress is not { } calendar) continue;
                            var key = "summary:" + SummaryKey(calendar);
                            if (_retained.ContainsKey(key)) continue;
                            var value = (replaySequence++, summary with { JobId = snapshot.JobId, State = ProviderWorkState.Running });
                            _retained[key] = value;
                            foreach (var subscriber in _subscribers)
                            {
                                subscriber.Pending[key] = value;
                                subscriber.Notify();
                            }
                        }
                        if (!_retained.ContainsKey("canonical")
                            && TryReadPersistedProgress(snapshot, now, out var persisted)
                            && persisted.CalendarProgress is not null)
                        {
                            // A terminal notification can race the initial durable
                            // read. Insert its already persisted calendar ahead of
                            // that notification even though live publishing ended.
                            var value = (replaySequence, persisted with { State = ProviderWorkState.Running });
                            _retained["canonical"] = value;
                            foreach (var subscriber in _subscribers)
                            {
                                subscriber.Pending["canonical"] = value;
                                subscriber.Notify();
                            }
                        }
                    }
                    if (IsTerminal(snapshot.State) && !_terminal)
                        Publish(new ProviderWorkProgress(snapshot.JobId, snapshot.State,
                            "Provider work stopped.", Error: snapshot.LastError));
                    return;
                }
                if (checkpoint is not null)
                {
                    foreach (var summary in checkpoint.SourceSummaries.Values)
                        Publish(summary with { JobId = snapshot.JobId, State = ProviderWorkState.Running });
                    if (TryReadPersistedProgress(snapshot, now, out var progress))
                        Publish(progress with { State = ProviderWorkState.Running });
                }
                if (IsTerminal(snapshot.State))
                    Publish(new ProviderWorkProgress(snapshot.JobId, snapshot.State,
                        snapshot.State == ProviderWorkState.Completed ? "Provider work completed." : "Provider work stopped.", Error: snapshot.LastError));
                else if (checkpoint is null)
                    Publish(new ProviderWorkProgress(snapshot.JobId, snapshot.State, "Provider work is queued."));
            }
        }

        public void Publish(ProviderWorkProgress update)
        {
            lock (_gate)
            {
                if (_terminal) return;
                var value = (++_sequence, update);
                var calendar = update.CalendarProgress;
                var key = calendar is null ? "state"
                    : IsSourceSummary(calendar) ? "summary:" + SummaryKey(calendar)
                    : calendar.IsFinal ? "final" : "source:" + (calendar.CheckpointKey ?? calendar.SourceName);
                if (calendar is not null)
                {
                    // A loading update is useful only until its source finishes. The
                    // latest full Items snapshot remains available independently.
                    if (IsSourceSummary(calendar)) _retained.Remove("source:" + (calendar.CheckpointKey ?? calendar.SourceName));
                    _retained["canonical"] = value;
                }
                _retained[key] = value;
                _terminal = IsTerminal(update.State);
                foreach (var subscriber in _subscribers)
                {
                    if (calendar is not null && IsSourceSummary(calendar))
                        subscriber.Pending.Remove("source:" + (calendar.CheckpointKey ?? calendar.SourceName));
                    subscriber.Pending[key] = value;
                    if (calendar is not null) subscriber.Pending["canonical"] = value;
                    subscriber.Notify();
                }
            }
        }

        // A subscriber holds one update per source or summary, rather than one per
        // streamed page. Its one-slot wakeup channel never blocks another watcher.
        private sealed class Subscription(JobBroadcast owner) : ChannelReader<ProviderWorkProgress>
        {
            private readonly Channel<bool> _notifications = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });
            public Dictionary<string, (long Sequence, ProviderWorkProgress Progress)> Pending { get; } = new(StringComparer.Ordinal);
            public bool Detached { get; set; }
            public void Notify() => _notifications.Writer.TryWrite(true);

            public override bool TryRead(out ProviderWorkProgress item)
            {
                lock (owner._gate)
                {
                    if (Pending.Count == 0) { item = default!; return false; }
                    var next = Pending.Values.MinBy(value => value.Sequence);
                    foreach (var key in Pending.Where(pair => pair.Value.Sequence == next.Sequence).Select(pair => pair.Key).ToArray())
                        Pending.Remove(key);
                    item = next.Progress;
                    return true;
                }
            }

            public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (owner._gate)
                    {
                        if (Pending.Count > 0) return true;
                        if (Detached || owner._terminal) return false;
                    }
                    await _notifications.Reader.ReadAsync(cancellationToken);
                }
            }
        }
    }
}

public sealed class ProviderWorkSchedulerHostedService(
    ProviderWorkScheduler scheduler,
    IServiceScopeFactory scopeFactory,
    DatabaseRecoveryState databaseState,
    IOptions<ProviderSchedulerOptions> options,
    ILogger<ProviderWorkSchedulerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        if (!databaseState.Snapshot.IsHealthy) return;
        await scheduler.RecoverAsync(stoppingToken);
        await scheduler.CleanupAsync(stoppingToken);
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        await Task.WhenAll(
            RunWorkerAsync(owner + ":foreground", foregroundOnly: true, stoppingToken),
            RunWorkerAsync(owner + ":general", foregroundOnly: false, stoppingToken),
            MaintainQueueAsync(stoppingToken));
    }

    private async Task MaintainQueueAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var nextCleanup = DateTimeOffset.UtcNow.AddDays(1);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await scheduler.RecoverAsync(stoppingToken);
                if (DateTimeOffset.UtcNow >= nextCleanup)
                {
                    await scheduler.CleanupAsync(stoppingToken);
                    nextCleanup = DateTimeOffset.UtcNow.AddDays(1);
                }
            }
            catch (Exception exception) when (DatabaseConnectionFactory.IsTransient(exception))
            {
                logger.LogWarning(exception, "Provider work recovery will retry after database contention.");
            }
        }
    }

    private async Task RunWorkerAsync(string owner, bool foregroundOnly, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await scheduler.ClaimNextAsync(foregroundOnly, owner, stoppingToken);
                if (job is null)
                {
                    await scheduler.WaitForSignalAsync(stoppingToken);
                    continue;
                }

                var execution = scheduler.BeginExecution(job, stoppingToken);
                var heartbeat = scheduler.MaintainLeaseAsync(job, execution);
                try
                {
                    await ExecuteJobAsync(job, execution.Token);
                    await scheduler.CompleteAsync(job, stoppingToken);
                }
                catch (ProviderWorkLeaseLostException)
                {
                    logger.LogInformation("Provider work {ProviderWorkJobId} stopped after losing its lease.", job.JobId);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && execution.IsCancellationRequested)
                {
                    await scheduler.PreemptAsync(job, CancellationToken.None);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    await scheduler.PreemptAsync(job, CancellationToken.None);
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Provider work {ProviderWorkJobId} failed.", job.JobId);
                    await scheduler.FailAsync(job, ex, CancellationToken.None);
                }
                finally
                {
                    await execution.CancelAsync();
                    await heartbeat;
                    scheduler.EndExecution(job, execution);
                }
            }
            catch (Exception ex) when (stoppingToken.IsCancellationRequested)
            {
                // SQLite can report a final lock error instead of cancellation while the host is
                // stopping. Do not turn an otherwise normal Windows Service stop into a crash.
                logger.LogDebug(ex, "Provider worker {ProviderWorkOwner} stopped during shutdown.", owner);
                return;
            }
            catch (Exception ex) when (DatabaseConnectionFactory.IsTransient(ex))
            {
                // A competing short write must delay this worker, not stop the
                // host through BackgroundServiceExceptionBehavior.StopHost.
                logger.LogWarning(ex, "Provider worker {ProviderWorkOwner} encountered transient SQLite contention.", owner);
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
        }
    }

    private async Task ExecuteJobAsync(ProviderWorkSnapshot job, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        switch (job.Kind)
        {
            case ProviderWorkKind.CalendarForeground:
            case ProviderWorkKind.AdjacentWeekPrefetch:
            {
                var payload = JsonSerializer.Deserialize<CalendarProviderWorkPayload>(job.PayloadJson)
                    ?? throw new InvalidOperationException("Calendar provider work payload is invalid.");
                var pipeline = scope.ServiceProvider.GetRequiredService<IPremiereLoadPipeline>();
                var checkpoint = scheduler.ReadResumeCheckpoint(job);
                var resume = new ProviderWorkResumeState(
                    checkpoint?.CompletedSources ?? new Dictionary<string, IReadOnlyList<PremiereItem>>(StringComparer.Ordinal),
                    checkpoint?.CalendarProgress)
                {
                    PublishCacheAsync = (publish, token) => scheduler.PublishOwnedCacheAsync(job, publish, token)
                };
                if (checkpoint?.CalendarProgress is { IsFinal: false } persisted)
                {
                    scheduler.ReplayPersistedProgress(job, persisted);
                }
                await foreach (var progress in pipeline.StreamCoreAsync(
                                   payload.Start,
                                   payload.End,
                                   payload.ForceRefresh,
                                   payload.Filters,
                                   resume,
                                   cancellationToken))
                {
                    await scheduler.PublishAsync(job, new ProviderWorkProgress(
                        job.JobId,
                        ProviderWorkState.Running,
                        progress.ProgressText ?? progress.SourceName,
                        progress.CompletedWork,
                        progress.TotalWork,
                        progress), cancellationToken);
                }
                break;
            }
            case ProviderWorkKind.CalendarWarmup:
                await scope.ServiceProvider.GetRequiredService<CurrentWeekCalendarWarmupRunner>().RunOnceAsync(
                    cancellationToken,
                    new ProviderWorkResumeState(new Dictionary<string, IReadOnlyList<PremiereItem>>(StringComparer.Ordinal), null)
                    {
                        PublishCacheAsync = (publish, token) => scheduler.PublishOwnedCacheAsync(job, publish, token)
                    });
                break;
            case ProviderWorkKind.ProviderDeltaSync:
                await scope.ServiceProvider.GetRequiredService<ProviderDeltaSyncService>().RunOnceAsync(cancellationToken);
                break;
            case ProviderWorkKind.ImdbDatasetRefresh:
                await scope.ServiceProvider.GetRequiredService<ImdbDatasetRefreshService>().ImportIfDueAsync(cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(job.Kind), job.Kind, "Unsupported provider work kind.");
        }
    }
}

public sealed class ProviderWorkStore(
    IOptions<AppDatabaseOptions> options,
    IWebHostEnvironment environment,
    IOptions<CalendarCacheOptions>? cacheOptions = null)
{
    private readonly AppDatabaseOptions _options = options.Value;
    private readonly CalendarCacheOptions _cacheOptions = cacheOptions?.Value ?? new CalendarCacheOptions();

    public async Task<IReadOnlyDictionary<string, int>> GetStateCountsAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State, COUNT(*) FROM ProviderWorkJobs GROUP BY State";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken)) counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    public async Task<ProviderWorkHandle> EnqueueAsync(
        ProviderWorkRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);

        while (true)
        {
            var id = Guid.NewGuid().ToString("N");
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ProviderWorkJobs (
                    JobId, Kind, DedupeKey, Priority, PayloadJson, State, AttemptCount, EnqueuedUtc)
                VALUES (@id, @kind, @dedupeKey, @priority, @payload, 'Queued', 0, @now)
                ON CONFLICT (DedupeKey) WHERE State IN ('Queued', 'Running', 'RetryWaiting') DO NOTHING
                """;
            DatabaseParameters.Add(insert, "@id", id);
            DatabaseParameters.Add(insert, "@kind", request.Kind.ToString());
            DatabaseParameters.Add(insert, "@dedupeKey", request.DedupeKey);
            DatabaseParameters.Add(insert, "@priority", (int)request.Priority);
            DatabaseParameters.Add(insert, "@payload", request.PayloadJson);
            DatabaseParameters.Add(insert, "@now", Format(now));
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ProviderWorkHandle(id, true);
            }
            await using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT JobId FROM ProviderWorkJobs
                WHERE DedupeKey = @dedupeKey AND State IN ('Queued', 'Running', 'RetryWaiting')
                LIMIT 1
                """;
            DatabaseParameters.Add(existing, "@dedupeKey", request.DedupeKey);
            var existingId = Convert.ToString(await existing.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(existingId))
            {
                await transaction.CommitAsync(cancellationToken);
                return new ProviderWorkHandle(existingId, false);
            }
            // Under PostgreSQL READ COMMITTED the conflicting active job can finish
            // between the insert and the reselect. Retry the insert in that case.
        }
    }

    public async Task<ProviderWorkSnapshot?> ClaimNextAsync(
        bool foregroundOnly,
        string owner,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        owner = $"{owner}:{Guid.NewGuid():N}";
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = $"""
            SELECT JobId, Kind, DedupeKey, Priority, PayloadJson, CheckpointJson, State,
                   AttemptCount, EnqueuedUtc, StartedUtc, CompletedUtc, NextAttemptUtc, LastError, LeaseOwner
            FROM ProviderWorkJobs
            WHERE State IN ('Queued', 'RetryWaiting')
              AND (NextAttemptUtc IS NULL OR NextAttemptUtc <= @now)
              {(foregroundOnly ? "AND Priority <= 100" : string.Empty)}
            ORDER BY Priority, EnqueuedUtc
            LIMIT 1
            {(DatabaseConnectionFactory.IsPostgreSql(connection) ? "FOR UPDATE SKIP LOCKED" : string.Empty)}
            """;
        DatabaseParameters.Add(select, "@now", Format(now));
        ProviderWorkSnapshot? snapshot = null;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken)) snapshot = Read(reader);
        }
        if (snapshot is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE ProviderWorkJobs
            SET State = 'Running', StartedUtc = COALESCE(StartedUtc, @now),
                LeaseOwner = @owner, LeaseExpiresUtc = @leaseExpires
            WHERE JobId = @id AND State IN ('Queued', 'RetryWaiting')
            """;
        DatabaseParameters.Add(update, "@now", Format(now));
        DatabaseParameters.Add(update, "@owner", owner);
        DatabaseParameters.Add(update, "@leaseExpires", Format(now.Add(lease)));
        DatabaseParameters.Add(update, "@id", snapshot.JobId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        await using (var leaseCommand = connection.CreateCommand())
        {
            leaseCommand.Transaction = transaction;
            leaseCommand.CommandText = """
                INSERT INTO ProviderWorkLeases (JobId, LeaseOwner, LeaseExpiresUtc, UpdatedUtc)
                VALUES (@id, @owner, @expires, @now)
                ON CONFLICT(JobId) DO UPDATE SET LeaseOwner = excluded.LeaseOwner,
                    LeaseExpiresUtc = excluded.LeaseExpiresUtc, UpdatedUtc = excluded.UpdatedUtc
                """;
            DatabaseParameters.Add(leaseCommand, "@id", snapshot.JobId);
            DatabaseParameters.Add(leaseCommand, "@owner", owner);
            DatabaseParameters.Add(leaseCommand, "@expires", Format(now.Add(lease)));
            DatabaseParameters.Add(leaseCommand, "@now", Format(now));
            await leaseCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return snapshot with { State = ProviderWorkState.Running, StartedUtc = snapshot.StartedUtc ?? now, LeaseOwner = owner };
    }

    public async Task<ProviderWorkSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT JobId, Kind, DedupeKey, Priority, PayloadJson, CheckpointJson, State,
                   AttemptCount, EnqueuedUtc, StartedUtc, CompletedUtc, NextAttemptUtc, LastError, LeaseOwner
            FROM ProviderWorkJobs WHERE JobId = @id
            """;
        DatabaseParameters.Add(command, "@id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public Task<bool> UpdateProgressAsync(string jobId, string owner, string checkpointJson, DateTimeOffset now, CancellationToken cancellationToken)
        => ExecuteOwnedAsync("CheckpointJson = @value, ProgressJson = @value", false,
            jobId, owner, checkpointJson, now, now, cancellationToken);

    public Task<bool> RenewLeaseAsync(string jobId, string owner, DateTimeOffset now, DateTimeOffset expires, CancellationToken cancellationToken)
        => ExecuteOwnedAsync("LeaseExpiresUtc = @deadline", false,
            jobId, owner, null, now, expires, cancellationToken, renewLease: true);

    public Task<bool> CompleteAsync(string jobId, string owner, DateTimeOffset now, CancellationToken cancellationToken)
        => ExecuteOwnedAsync("""
            State = 'Completed', CompletedUtc = @now, LeaseOwner = NULL, LeaseExpiresUtc = NULL,
            NextAttemptUtc = NULL, LastError = NULL
            """, true, jobId, owner, null, now, now, cancellationToken);

    public Task<bool> FailAsync(string jobId, string owner, string error, DateTimeOffset now, CancellationToken cancellationToken)
        => ExecuteOwnedAsync("""
            State = 'Failed', CompletedUtc = @now, LastError = @value,
            AttemptCount = AttemptCount + 1, LeaseOwner = NULL, LeaseExpiresUtc = NULL
            """, true, jobId, owner, error, now, now, cancellationToken);

    public Task<bool> RequeueAsync(
        string jobId, string owner, DateTimeOffset now, DateTimeOffset nextAttempt,
        bool incrementAttempt, string? error, CancellationToken cancellationToken, bool retryWaiting = false)
        => ExecuteOwnedAsync($"""
            State = '{(incrementAttempt || retryWaiting ? "RetryWaiting" : "Queued")}',
            NextAttemptUtc = @deadline, LastError = @value,
            AttemptCount = AttemptCount + {(incrementAttempt ? "1" : "0")},
            LeaseOwner = NULL, LeaseExpiresUtc = NULL
            """, true, jobId, owner, error, now, nextAttempt, cancellationToken);

    public async Task<bool> WithOwnedLeaseAsync(
        string jobId, string owner, TimeProvider timeProvider, TimeSpan lease,
        Func<CancellationToken, Task> publish, CancellationToken cancellationToken)
    {
        var directory = Path.IsPathRooted(_cacheOptions.Directory) ? _cacheOptions.Directory
            : Path.Combine(environment.ContentRootPath, _cacheOptions.Directory);
        var published = false;
        await CalendarCachePublicationLock.RunAsync(directory, async token =>
        {
            published = await WithOwnedLeaseCoreAsync(jobId, owner, timeProvider, lease, publish, token);
        }, cancellationToken);
        return published;
    }

    private async Task<bool> WithOwnedLeaseCoreAsync(
        string jobId, string owner, TimeProvider timeProvider, TimeSpan lease,
        Func<CancellationToken, Task> publish, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // SQLite's immediate transaction reserves the writer before checking the
        // claim. PostgreSQL instead holds this job's row lock through publication.
        await using var transaction = connection is Microsoft.Data.Sqlite.SqliteConnection sqlite
            ? sqlite.BeginTransaction(deferred: false)
            : (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var claimLock = connection.CreateCommand())
        {
            claimLock.Transaction = transaction;
            claimLock.CommandText = "SELECT JobId FROM ProviderWorkJobs WHERE JobId = @id"
                + (DatabaseConnectionFactory.IsPostgreSql(connection) ? " FOR UPDATE" : string.Empty);
            DatabaseParameters.Add(claimLock, "@id", jobId);
            if (await claimLock.ExecuteScalarAsync(cancellationToken) is null) return false;
        }

        // Read the clock after acquiring the lock; waiting for another publisher
        // must not make an already expired claim appear valid.
        var now = timeProvider.GetUtcNow();
        var expires = now.Add(lease);
        await using (var renewJob = connection.CreateCommand())
        {
            renewJob.Transaction = transaction;
            renewJob.CommandText = """
                UPDATE ProviderWorkJobs SET LeaseExpiresUtc = @expires
                WHERE JobId = @id AND State = 'Running' AND LeaseOwner = @owner AND LeaseExpiresUtc > @now
                    AND EXISTS (SELECT 1 FROM ProviderWorkLeases lease
                        WHERE lease.JobId = ProviderWorkJobs.JobId AND lease.LeaseOwner = @owner AND lease.LeaseExpiresUtc > @now)
                """;
            DatabaseParameters.Add(renewJob, "@id", jobId);
            DatabaseParameters.Add(renewJob, "@owner", owner);
            DatabaseParameters.Add(renewJob, "@now", Format(now));
            DatabaseParameters.Add(renewJob, "@expires", Format(expires));
            if (await renewJob.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        }
        await using (var renewLease = connection.CreateCommand())
        {
            renewLease.Transaction = transaction;
            renewLease.CommandText = """
                UPDATE ProviderWorkLeases SET LeaseExpiresUtc = @expires, UpdatedUtc = @now
                WHERE JobId = @id AND LeaseOwner = @owner
                """;
            DatabaseParameters.Add(renewLease, "@id", jobId);
            DatabaseParameters.Add(renewLease, "@owner", owner);
            DatabaseParameters.Add(renewLease, "@now", Format(now));
            DatabaseParameters.Add(renewLease, "@expires", Format(expires));
            if (await renewLease.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await publish(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task SetForegroundInterestAsync(string jobId, bool hasWatcher, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ProviderWorkJobs SET Priority = @priority
            WHERE JobId = @id AND Kind = 'CalendarForeground' AND State IN ('Queued', 'RetryWaiting')
            """;
        DatabaseParameters.Add(command, "@id", jobId);
        DatabaseParameters.Add(command, "@priority", (int)(hasWatcher ? ProviderWorkPriority.Foreground : ProviderWorkPriority.Adjacent));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ProviderWorkJobs SET State = 'Queued', LeaseOwner = NULL, LeaseExpiresUtc = NULL,
                NextAttemptUtc = @now
            WHERE State = 'Running' AND (
                LeaseExpiresUtc IS NULL OR LeaseExpiresUtc <= @now
                OR NOT EXISTS (SELECT 1 FROM ProviderWorkLeases lease
                    WHERE lease.JobId = ProviderWorkJobs.JobId AND lease.LeaseOwner = ProviderWorkJobs.LeaseOwner AND lease.LeaseExpiresUtc > @now));
            DELETE FROM ProviderWorkLeases WHERE NOT EXISTS (
                SELECT 1 FROM ProviderWorkJobs job WHERE job.JobId = ProviderWorkLeases.JobId
                    AND job.State = 'Running' AND job.LeaseOwner = ProviderWorkLeases.LeaseOwner);
            """;
        DatabaseParameters.Add(command, "@now", Format(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteCompletedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM ProviderWorkJobs
            WHERE State IN ('Completed', 'Failed', 'Cancelled') AND CompletedUtc < @cutoff
            """;
        DatabaseParameters.Add(command, "@cutoff", Format(cutoff));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> ExecuteOwnedAsync(
        string assignments, bool releaseLease, string jobId, string owner, string? value,
        DateTimeOffset now, DateTimeOffset deadline, CancellationToken cancellationToken, bool renewLease = false)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE ProviderWorkJobs SET {assignments}
            WHERE JobId = @id AND State = 'Running' AND LeaseOwner = @owner AND LeaseExpiresUtc > @now
                AND EXISTS (SELECT 1 FROM ProviderWorkLeases lease
                    WHERE lease.JobId = ProviderWorkJobs.JobId AND lease.LeaseOwner = @owner AND lease.LeaseExpiresUtc > @now)
            """;
        DatabaseParameters.Add(command, "@id", jobId);
        DatabaseParameters.Add(command, "@owner", owner);
        DatabaseParameters.Add(command, "@now", Format(now));
        DatabaseParameters.Add(command, "@deadline", Format(deadline));
        DatabaseParameters.Add(command, "@value", System.Data.DbType.String).Value = (object?)value ?? DBNull.Value;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await using var leaseCommand = connection.CreateCommand();
        leaseCommand.Transaction = transaction;
        leaseCommand.CommandText = releaseLease
            ? "DELETE FROM ProviderWorkLeases WHERE JobId = @id AND LeaseOwner = @owner"
            : renewLease
                ? "UPDATE ProviderWorkLeases SET LeaseExpiresUtc = @deadline, UpdatedUtc = @now WHERE JobId = @id AND LeaseOwner = @owner"
                : "UPDATE ProviderWorkLeases SET UpdatedUtc = @now WHERE JobId = @id AND LeaseOwner = @owner";
        DatabaseParameters.Add(leaseCommand, "@id", jobId);
        DatabaseParameters.Add(leaseCommand, "@owner", owner);
        DatabaseParameters.Add(leaseCommand, "@now", Format(now));
        DatabaseParameters.Add(leaseCommand, "@deadline", Format(deadline));
        if (await leaseCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private DbConnection CreateConnection()
    {
        return DatabaseConnectionFactory.Create(_options, environment.ContentRootPath);
    }

    private static ProviderWorkSnapshot Read(DbDataReader reader) => new(
        reader.GetString(0),
        Enum.Parse<ProviderWorkKind>(reader.GetString(1), ignoreCase: true),
        reader.GetString(2),
        (ProviderWorkPriority)reader.GetInt32(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        Enum.Parse<ProviderWorkState>(reader.GetString(6), ignoreCase: true),
        reader.GetInt32(7),
        Parse(reader.GetString(8)),
        reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
        reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
        reader.IsDBNull(11) ? null : Parse(reader.GetString(11)),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13));

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

public interface IPremiereLoadPipeline
{
    IAsyncEnumerable<PremiereLoadProgress> StreamCoreAsync(
        DateOnly start,
        DateOnly end,
        bool forceRefresh,
        CalendarFilters? filters,
        ProviderWorkResumeState? resumeState,
        CancellationToken cancellationToken);
}
