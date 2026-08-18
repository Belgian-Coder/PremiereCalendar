using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
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
    PremiereLoadProgress? LatestProgress);

internal sealed record ProviderWorkCheckpoint(
    string Message,
    int? CompletedWork,
    int? TotalWork,
    PremiereLoadProgress? CalendarProgress,
    IReadOnlyDictionary<string, IReadOnlyList<PremiereItem>> CompletedSources);

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
    string? LastError);

public sealed class ProviderWorkScheduler : IProviderWorkScheduler
{
    private readonly ProviderWorkStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ProviderSchedulerOptions _options;
    private readonly PremiereTelemetry _telemetry;
    private readonly ConcurrentDictionary<string, JobBroadcast> _broadcasts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _preemptionGate = new();
    private CancellationTokenSource? _activeBackgroundCancellation;
    private readonly Random _jitter = new();

    public ProviderWorkScheduler(
        ProviderWorkStore store,
        TimeProvider timeProvider,
        IOptions<ProviderSchedulerOptions> options,
        PremiereTelemetry? telemetry = null)
    {
        _store = store;
        _timeProvider = timeProvider;
        _options = options.Value;
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
        _broadcasts.GetOrAdd(result.JobId, static _ => new JobBroadcast());
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
        var broadcast = _broadcasts.GetOrAdd(handle.JobId, static _ => new JobBroadcast());
        var reader = broadcast.Subscribe();
        try
        {
            if (!reader.TryRead(out var firstUpdate))
            {
                var snapshot = await _store.GetAsync(handle.JobId, cancellationToken);
                if (!reader.TryRead(out firstUpdate))
                {
                    if (snapshot is null)
                    {
                        yield break;
                    }

                    if (TryReadPersistedProgress(snapshot, out var persistedProgress))
                    {
                        yield return persistedProgress;
                    }

                    if (IsTerminal(snapshot.State))
                    {
                        yield return new ProviderWorkProgress(
                            snapshot.JobId,
                            snapshot.State,
                            snapshot.State == ProviderWorkState.Completed ? "Provider work completed." : "Provider work stopped.",
                            Error: snapshot.LastError);
                        yield break;
                    }
                }
            }

            if (firstUpdate is not null)
            {
                yield return firstUpdate;
                if (IsTerminal(firstUpdate.State))
                {
                    yield break;
                }
            }

            await foreach (var update in reader.ReadAllAsync(cancellationToken))
            {
                yield return update;
                if (IsTerminal(update.State)) yield break;
            }
        }
        finally
        {
            broadcast.Unsubscribe(reader);
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
            _telemetry.RecordJobWait(job.Kind.ToString(), _timeProvider.GetUtcNow() - job.EnqueuedUtc, !string.IsNullOrWhiteSpace(job.CheckpointJson));
            await RefreshCountsAsync(cancellationToken);
        }
        return job;
    }

    internal CancellationTokenSource BeginExecution(ProviderWorkSnapshot job, CancellationToken stoppingToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
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
    }

    internal async Task PublishAsync(
        ProviderWorkSnapshot job,
        ProviderWorkProgress progress,
        CancellationToken cancellationToken)
    {
        _broadcasts.GetOrAdd(job.JobId, static _ => new JobBroadcast()).Publish(progress);
        var previous = TryReadCheckpoint(job.CheckpointJson);
        var completedSources = previous?.CompletedSources is { } sources
            ? new Dictionary<string, IReadOnlyList<PremiereItem>>(sources, StringComparer.Ordinal)
            : new Dictionary<string, IReadOnlyList<PremiereItem>>(StringComparer.Ordinal);
        if (progress.CalendarProgress is { Phase: "complete", IsFinal: false, CheckpointKey: { Length: > 0 } key } calendar)
        {
            completedSources[key] = calendar.SourceItems;
        }
        var checkpoint = JsonSerializer.Serialize(new ProviderWorkCheckpoint(
            progress.Message,
            progress.CompletedWork,
            progress.TotalWork,
            progress.CalendarProgress,
            completedSources));
        await _store.UpdateProgressAsync(
            job.JobId,
            checkpoint,
            _timeProvider.GetUtcNow().AddSeconds(Math.Clamp(_options.LeaseSeconds, 30, 900)),
            cancellationToken);
    }

    internal async Task CompleteAsync(ProviderWorkSnapshot job, CancellationToken cancellationToken)
    {
        await _store.CompleteAsync(job.JobId, _timeProvider.GetUtcNow(), cancellationToken);
        await RefreshCountsAsync(cancellationToken);
        _telemetry.RecordJob(job.Kind.ToString(), "completed", Elapsed(job));
        _broadcasts.GetOrAdd(job.JobId, static _ => new JobBroadcast()).Publish(
            new ProviderWorkProgress(job.JobId, ProviderWorkState.Completed, "Provider work completed."));
    }

    internal async Task PreemptAsync(ProviderWorkSnapshot job, CancellationToken cancellationToken)
    {
        await _store.RequeueAsync(job.JobId, _timeProvider.GetUtcNow(), incrementAttempt: false, null, cancellationToken);
        await RefreshCountsAsync(cancellationToken);
        _telemetry.RecordJob(job.Kind.ToString(), "preempted", Elapsed(job));
        _broadcasts.GetOrAdd(job.JobId, static _ => new JobBroadcast()).Publish(
            new ProviderWorkProgress(job.JobId, ProviderWorkState.Queued, "Provider work was checkpointed for foreground activity."));
        _signal.Release();
    }

    internal async Task FailAsync(ProviderWorkSnapshot job, Exception error, CancellationToken cancellationToken)
    {
        var sanitized = SanitizeFailure(error);
        if (job.AttemptCount + 1 < Math.Clamp(_options.MaximumAttempts, 1, 20))
        {
            var exponentialMilliseconds = Math.Min(
                Math.Clamp(_options.RetryMaximumSeconds, 1, 300) * 1_000d,
                Math.Clamp(_options.RetryBaseMilliseconds, 50, 30_000) * Math.Pow(2, job.AttemptCount));
            var delay = TimeSpan.FromMilliseconds(exponentialMilliseconds * (0.75 + (_jitter.NextDouble() * 0.5)));
            await _store.RequeueAsync(job.JobId, _timeProvider.GetUtcNow().Add(delay), true, sanitized, cancellationToken);
            await RefreshCountsAsync(cancellationToken);
            _telemetry.RecordJob(job.Kind.ToString(), "retry", Elapsed(job));
            _broadcasts.GetOrAdd(job.JobId, static _ => new JobBroadcast()).Publish(
                new ProviderWorkProgress(job.JobId, ProviderWorkState.RetryWaiting, "Provider work will retry.", Error: sanitized));
            _signal.Release();
            return;
        }

        await _store.FailAsync(job.JobId, sanitized, _timeProvider.GetUtcNow(), cancellationToken);
        await RefreshCountsAsync(cancellationToken);
        _telemetry.RecordJob(job.Kind.ToString(), "failed", Elapsed(job));
        _broadcasts.GetOrAdd(job.JobId, static _ => new JobBroadcast()).Publish(
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

    private static bool IsTerminal(ProviderWorkState state)
        => state is ProviderWorkState.Completed or ProviderWorkState.Failed or ProviderWorkState.Cancelled;

    private static bool TryReadPersistedProgress(ProviderWorkSnapshot snapshot, out ProviderWorkProgress progress)
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
                checkpoint.CalendarProgress);
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
        try { return JsonSerializer.Deserialize<ProviderWorkCheckpoint>(json); }
        catch (JsonException) { return null; }
    }

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
        catch (Exception exception) when (exception is SqliteException or OperationCanceledException)
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
        SqliteException => "Provider work persistence failed.",
        _ => string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message
    };

    internal sealed class JobBroadcast
    {
        private readonly object _gate = new();
        private readonly HashSet<ChannelReader<ProviderWorkProgress>> _readers = [];
        private readonly Dictionary<ChannelReader<ProviderWorkProgress>, ChannelWriter<ProviderWorkProgress>> _writers = [];

        public ProviderWorkProgress? Latest { get; private set; }
        public ProviderWorkProgress? LatestCalendar { get; private set; }

        public ChannelReader<ProviderWorkProgress> Subscribe()
        {
            var channel = Channel.CreateUnbounded<ProviderWorkProgress>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            lock (_gate)
            {
                _readers.Add(channel.Reader);
                _writers[channel.Reader] = channel.Writer;
                if (LatestCalendar is { } latestCalendar)
                {
                    channel.Writer.TryWrite(latestCalendar);
                }

                if (Latest is { } latest && !ReferenceEquals(latest, LatestCalendar))
                {
                    channel.Writer.TryWrite(latest);
                }

                if (Latest is { State: var state } && IsTerminal(state))
                {
                    channel.Writer.TryComplete();
                }
            }
            return channel.Reader;
        }

        public void Unsubscribe(ChannelReader<ProviderWorkProgress> reader)
        {
            lock (_gate)
            {
                if (_writers.Remove(reader, out var writer)) writer.TryComplete();
                _readers.Remove(reader);
            }
        }

        public void Publish(ProviderWorkProgress update)
        {
            lock (_gate)
            {
                Latest = update;
                if (update.CalendarProgress is not null) LatestCalendar = update;
                foreach (var writer in _writers.Values) writer.TryWrite(update);
                if (IsTerminal(update.State))
                {
                    foreach (var writer in _writers.Values) writer.TryComplete();
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
            RunWorkerAsync(owner + ":general", foregroundOnly: false, stoppingToken));
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
                try
                {
                    await ExecuteJobAsync(job, execution.Token);
                    await scheduler.CompleteAsync(job, stoppingToken);
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
                var checkpoint = ProviderWorkScheduler.TryReadCheckpoint(job.CheckpointJson);
                var resume = checkpoint is null
                    ? null
                    : new ProviderWorkResumeState(checkpoint.CompletedSources, checkpoint.CalendarProgress);
                if (checkpoint?.CalendarProgress is { } persisted)
                {
                    await scheduler.PublishAsync(job, new ProviderWorkProgress(
                        job.JobId,
                        ProviderWorkState.Running,
                        "Resumed persisted provider progress.",
                        persisted.CompletedWork,
                        persisted.TotalWork,
                        persisted), cancellationToken);
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
                await scope.ServiceProvider.GetRequiredService<CurrentWeekCalendarWarmupRunner>().RunOnceAsync(cancellationToken);
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
    IWebHostEnvironment environment)
{
    private readonly AppDatabaseOptions _options = options.Value;

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
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT JobId FROM ProviderWorkJobs
                WHERE DedupeKey = $dedupeKey AND State IN ('Queued', 'Running', 'RetryWaiting')
                LIMIT 1
                """;
            existing.Parameters.AddWithValue("$dedupeKey", request.DedupeKey);
            var existingId = Convert.ToString(await existing.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(existingId))
            {
                await transaction.CommitAsync(cancellationToken);
                return new ProviderWorkHandle(existingId, false);
            }
        }

        var id = Guid.NewGuid().ToString("N");
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ProviderWorkJobs (
                    JobId, Kind, DedupeKey, Priority, PayloadJson, State, AttemptCount, EnqueuedUtc)
                VALUES ($id, $kind, $dedupeKey, $priority, $payload, 'Queued', 0, $now)
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$kind", request.Kind.ToString());
            insert.Parameters.AddWithValue("$dedupeKey", request.DedupeKey);
            insert.Parameters.AddWithValue("$priority", (int)request.Priority);
            insert.Parameters.AddWithValue("$payload", request.PayloadJson);
            insert.Parameters.AddWithValue("$now", Format(now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ProviderWorkHandle(id, true);
    }

    public async Task<ProviderWorkSnapshot?> ClaimNextAsync(
        bool foregroundOnly,
        string owner,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = $"""
            SELECT JobId, Kind, DedupeKey, Priority, PayloadJson, CheckpointJson, State,
                   AttemptCount, EnqueuedUtc, StartedUtc, CompletedUtc, NextAttemptUtc, LastError
            FROM ProviderWorkJobs
            WHERE State IN ('Queued', 'RetryWaiting')
              AND (NextAttemptUtc IS NULL OR NextAttemptUtc <= $now)
              {(foregroundOnly ? "AND Priority <= 100" : string.Empty)}
            ORDER BY Priority, EnqueuedUtc
            LIMIT 1
            """;
        select.Parameters.AddWithValue("$now", Format(now));
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
            SET State = 'Running', StartedUtc = COALESCE(StartedUtc, $now),
                LeaseOwner = $owner, LeaseExpiresUtc = $leaseExpires
            WHERE JobId = $id AND State IN ('Queued', 'RetryWaiting')
            """;
        update.Parameters.AddWithValue("$now", Format(now));
        update.Parameters.AddWithValue("$owner", owner);
        update.Parameters.AddWithValue("$leaseExpires", Format(now.Add(lease)));
        update.Parameters.AddWithValue("$id", snapshot.JobId);
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
                VALUES ($id, $owner, $expires, $now)
                ON CONFLICT(JobId) DO UPDATE SET LeaseOwner = excluded.LeaseOwner,
                    LeaseExpiresUtc = excluded.LeaseExpiresUtc, UpdatedUtc = excluded.UpdatedUtc
                """;
            leaseCommand.Parameters.AddWithValue("$id", snapshot.JobId);
            leaseCommand.Parameters.AddWithValue("$owner", owner);
            leaseCommand.Parameters.AddWithValue("$expires", Format(now.Add(lease)));
            leaseCommand.Parameters.AddWithValue("$now", Format(now));
            await leaseCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return snapshot with { State = ProviderWorkState.Running, StartedUtc = snapshot.StartedUtc ?? now };
    }

    public async Task<ProviderWorkSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT JobId, Kind, DedupeKey, Priority, PayloadJson, CheckpointJson, State,
                   AttemptCount, EnqueuedUtc, StartedUtc, CompletedUtc, NextAttemptUtc, LastError
            FROM ProviderWorkJobs WHERE JobId = $id
            """;
        command.Parameters.AddWithValue("$id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public Task UpdateProgressAsync(string jobId, string checkpointJson, DateTimeOffset now, CancellationToken cancellationToken)
        => ExecuteAsync("""
            UPDATE ProviderWorkJobs
            SET CheckpointJson = $value, ProgressJson = $value, LeaseExpiresUtc = $now
            WHERE JobId = $id AND State = 'Running';
            UPDATE ProviderWorkLeases SET LeaseExpiresUtc = $now, UpdatedUtc = $now WHERE JobId = $id;
            """, jobId, checkpointJson, now, cancellationToken);

    public Task CompleteAsync(string jobId, DateTimeOffset now, CancellationToken cancellationToken)
        => ExecuteAsync("""
            UPDATE ProviderWorkJobs SET State = 'Completed', CompletedUtc = $now,
                LeaseOwner = NULL, LeaseExpiresUtc = NULL, NextAttemptUtc = NULL, LastError = NULL
            WHERE JobId = $id;
            DELETE FROM ProviderWorkLeases WHERE JobId = $id;
            """, jobId, null, now, cancellationToken);

    public Task FailAsync(string jobId, string error, DateTimeOffset now, CancellationToken cancellationToken)
        => ExecuteAsync("""
            UPDATE ProviderWorkJobs SET State = 'Failed', CompletedUtc = $now, LastError = $value,
                AttemptCount = AttemptCount + 1, LeaseOwner = NULL, LeaseExpiresUtc = NULL
            WHERE JobId = $id;
            DELETE FROM ProviderWorkLeases WHERE JobId = $id;
            """, jobId, error, now, cancellationToken);

    public Task RequeueAsync(
        string jobId,
        DateTimeOffset nextAttempt,
        bool incrementAttempt,
        string? error,
        CancellationToken cancellationToken)
        => ExecuteAsync($"""
            UPDATE ProviderWorkJobs SET State = '{(incrementAttempt ? "RetryWaiting" : "Queued")}',
                NextAttemptUtc = $now, LastError = $value,
                AttemptCount = AttemptCount + {(incrementAttempt ? "1" : "0")},
                LeaseOwner = NULL, LeaseExpiresUtc = NULL
            WHERE JobId = $id;
            DELETE FROM ProviderWorkLeases WHERE JobId = $id;
            """, jobId, error, nextAttempt, cancellationToken);

    public async Task RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ProviderWorkJobs SET State = 'Queued', LeaseOwner = NULL, LeaseExpiresUtc = NULL,
                NextAttemptUtc = $now
            WHERE State = 'Running' AND (
                NOT EXISTS (SELECT 1 FROM ProviderWorkLeases lease WHERE lease.JobId = ProviderWorkJobs.JobId)
                OR EXISTS (SELECT 1 FROM ProviderWorkLeases lease WHERE lease.JobId = ProviderWorkJobs.JobId AND lease.LeaseExpiresUtc < $now));
            DELETE FROM ProviderWorkLeases WHERE LeaseExpiresUtc < $now;
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteCompletedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM ProviderWorkJobs
            WHERE State IN ('Completed', 'Failed', 'Cancelled') AND CompletedUtc < $cutoff
            """;
        command.Parameters.AddWithValue("$cutoff", Format(cutoff));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteAsync(
        string commandText,
        string jobId,
        string? value,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection CreateConnection()
    {
        var path = SqliteDatabasePath.Resolve(_options.Path, environment.ContentRootPath);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        return SqliteConnectionFactory.Create(builder.ToString());
    }

    private static ProviderWorkSnapshot Read(SqliteDataReader reader) => new(
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
        reader.IsDBNull(12) ? null : reader.GetString(12));

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
