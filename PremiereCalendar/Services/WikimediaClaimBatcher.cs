using System.Diagnostics;

namespace PremiereCalendar.Services;

/// <summary>Coalesces the existing single-entity API without retaining completed results.</summary>
internal sealed class WikimediaClaimBatcher(
    Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, WikimediaEntityClaims>>> fetch,
    PremiereTelemetry? telemetry,
    Action? beforeAbandonedBatchCancellation = null)
{
    private const int MaximumEntities = 50;
    private const int MaximumConcurrentBatches = 2;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _capacity = new(1024, 1024);
    private readonly Dictionary<(string Id, long Generation), EntityWork> _work = [];
    private readonly List<EntityWork> _pending = [];
    private TaskCompletionSource _changed = NewSignal();
    private bool _draining;
    private int _foregroundBurst;

    internal async Task<WikimediaEntityClaims> GetAsync(string id, long generation, CancellationToken cancellationToken)
    {
        var queuedAt = Stopwatch.GetTimestamp();
        try { await _capacity.WaitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            telemetry?.RecordDiagnostic("provider.wikimedia.queue.completed", details: new()
            {
                Provider = "wikimedia", Stage = "entity_queue", Count = 1, Outcome = "cancelled",
                DurationMilliseconds = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds
            });
            throw;
        }
        EntityWork? work = null;
        Waiter? waiter = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startDrain = false;
            lock (_gate)
            {
                var key = (id, generation);
                if (!_work.TryGetValue(key, out work) || work.Batch?.Closed == true)
                {
                    work = new EntityWork(id, generation);
                    _work[key] = work;
                    _pending.Add(work);
                }
                waiter = new(ProviderExecutionContext.CapturePriorityAccessor(), telemetry?.CaptureDiagnosticContext(), cancellationToken, queuedAt);
                work.Waiters.Add(waiter);
                work.Batch?.Participants.Add(waiter);
                var changed = _changed;
                _changed = NewSignal();
                changed.TrySetResult();
                if (!_draining) startDrain = _draining = true;
            }
            if (startDrain) _ = DrainAsync();
            return await work.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Batch? cancel = null;
            if (work is not null && waiter is not null)
            {
                lock (_gate)
                {
                    work.Waiters.Remove(waiter);
                    if (work.Waiters.Count == 0 && !work.Completion.Task.IsCompleted)
                    {
                        RemoveWork(work);
                        _pending.Remove(work);
                        if (work.Batch is { Closed: false } batch && !batch.Work.Any(HasLiveWaiters))
                        {
                            batch.Closed = true;
                            foreach (var abandoned in batch.Work) RemoveWork(abandoned);
                            cancel = batch;
                        }
                    }
                }
            }
            if (work is not null && waiter is not null)
            {
                var batch = work.Batch;
                var waitDuration = batch is { StartedAt: > 0 }
                    ? Stopwatch.GetElapsedTime(waiter.QueuedAt, Math.Max(batch.StartedAt, waiter.AdmittedAt)).TotalMilliseconds
                    : Stopwatch.GetElapsedTime(waiter.QueuedAt).TotalMilliseconds;
                telemetry?.RecordDiagnostic("provider.wikimedia.queue.completed",
                    (waiter.Context ?? new()) with { RequestId = batch?.RequestId }, new()
                    {
                        Provider = "wikimedia", Stage = "entity_queue", Count = 1,
                        Outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "ready",
                        DurationMilliseconds = waitDuration
                    });
            }
            if (cancel is not null)
            {
                beforeAbandonedBatchCancellation?.Invoke();
                cancel.Cancel();
            }
            _capacity.Release();
        }
    }

    private async Task DrainAsync()
    {
        // The queue stops as soon as the final batch completes.
        await Task.Delay(TimeSpan.FromMilliseconds(10));
        List<Task> running = [];
        while (true)
        {
            Batch? batch = null;
            Task changed;
            Task? coalescing = null;
            lock (_gate)
            {
                changed = _changed.Task;
                running.RemoveAll(task => task.IsCompleted);
                _pending.RemoveAll(work => !HasLiveWaiters(work));
                if (running.Count < MaximumConcurrentBatches && _pending.Count > 0)
                {
                    var remaining = TimeSpan.FromMilliseconds(10) - Stopwatch.GetElapsedTime(_pending[0].QueuedAt);
                    if (remaining > TimeSpan.Zero) coalescing = Task.Delay(remaining);
                    else
                    {
                        var ids = SelectEntityIds();
                        var selected = ids.SelectMany(id => _pending.Where(work => work.Id == id)).ToArray();
                        _pending.RemoveAll(work => ids.Contains(work.Id));
                        batch = new Batch(selected);
                        foreach (var work in selected) work.Batch = batch;
                    }
                }
                else if (_pending.Count == 0 && running.Count == 0)
                {
                    _draining = false;
                    _foregroundBurst = 0;
                    return;
                }
            }
            if (batch is not null) running.Add(ProcessBatchAsync(batch));
            else
            {
                var signals = running.Append(changed);
                if (coalescing is not null) signals = signals.Append(coalescing);
                await Task.WhenAny(signals);
            }
        }
    }

    private async Task ProcessBatchAsync(Batch batch)
    {
        var started = Stopwatch.GetTimestamp();
        var ids = batch.Work.Select(work => work.Id).Distinct(StringComparer.Ordinal).ToArray();
        Waiter[] waiters;
        lock (_gate)
        {
            batch.StartedAt = started;
            waiters = batch.Work.SelectMany(work => work.Waiters).Where(waiter => !waiter.Token.IsCancellationRequested).ToArray();
        }
        var requestId = batch.RequestId;
        var primaryContext = waiters.FirstOrDefault()?.Context;
        var outcome = "failed";
        ProviderFailure? failure = null;
        IReadOnlyDictionary<string, WikimediaEntityClaims> results = new Dictionary<string, WikimediaEntityClaims>();
        try
        {
            using var priority = ProviderExecutionContext.Push(() => CurrentPriority(batch));
            using var context = telemetry?.UseDiagnosticContext((primaryContext ?? new()) with { RequestId = requestId });
            if (waiters.Length > 0)
            {
                results = await fetch(ids, batch.Cancellation.Token);
                outcome = ids.All(id => results.TryGetValue(id, out var result) && result.Image.IsSuccess && result.Identifier.IsSuccess)
                    ? "success" : "partial_or_failed";
            }
            else outcome = "cancelled";
        }
        catch (OperationCanceledException exception) { outcome = batch.Cancellation.IsCancellationRequested ? "cancelled" : "timeout"; failure = ProviderFailure.FromException(exception); }
        catch (HttpRequestException exception) { outcome = "http_error"; failure = ProviderFailure.FromException(exception); }
        catch (System.Text.Json.JsonException) { outcome = "malformed"; failure = new("invalid_response"); }
        finally
        {
            lock (_gate)
            {
                batch.Closed = true;
                foreach (var work in batch.Work) RemoveWork(work);
                waiters = batch.Participants.ToArray();
            }
            // Shared requests retain a link to every participating load/job, not only their first caller.
            var contexts = waiters.Select(waiter => waiter.Context).Distinct().ToArray();
            foreach (var callerContext in contexts)
                telemetry?.RecordDiagnostic("provider.wikimedia.batch.completed", (callerContext ?? new()) with { RequestId = requestId }, new()
                {
                    Provider = "wikimedia", Stage = "entity_claims", Outcome = outcome,
                    Count = ids.Length, Total = waiters.Length, Limit = MaximumEntities,
                    DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
                });
            lock (_gate)
            {
                foreach (var work in batch.Work)
                {
                    var result = results.TryGetValue(work.Id, out var entity) ? entity : WikimediaEntityClaims.Failed;
                    work.Completion.TrySetResult(result with { RequestId = requestId, Failure = failure ?? result.Failure });
                }
            }
            batch.Dispose();
        }
    }

    private static bool HasLiveWaiters(EntityWork work) => work.Waiters.Any(waiter => !waiter.Token.IsCancellationRequested);

    private IReadOnlyList<string> SelectEntityIds()
    {
        var candidates = _pending.GroupBy(work => work.Id, StringComparer.Ordinal).Select(group => group.ToArray()).ToList();
        List<string> selected = [];
        while (selected.Count < MaximumEntities && candidates.Count > 0)
        {
            var foreground = candidates.FindIndex(group => group.Any(work => CurrentPriority(work) == ProviderWorkPriority.Foreground));
            var background = candidates.FindIndex(group => group.All(work => CurrentPriority(work) != ProviderWorkPriority.Foreground));
            var index = background >= 0 && (foreground < 0 || _foregroundBurst >= 8) ? background : foreground;
            if (index < 0) index = 0;
            _foregroundBurst = index == foreground && background >= 0 ? Math.Min(8, _foregroundBurst + 1) : 0;
            selected.Add(candidates[index][0].Id);
            candidates.RemoveAt(index);
        }
        return selected;
    }

    private static ProviderWorkPriority CurrentPriority(EntityWork work)
        => work.Waiters.Where(waiter => !waiter.Token.IsCancellationRequested)
            .Select(waiter => waiter.Priority()).DefaultIfEmpty(ProviderWorkPriority.Maintenance).Min();

    private ProviderWorkPriority CurrentPriority(Batch batch)
    {
        lock (_gate) return batch.Work.Select(CurrentPriority).DefaultIfEmpty(ProviderWorkPriority.Maintenance).Min();
    }

    private void RemoveWork(EntityWork work)
    {
        var key = (work.Id, work.Generation);
        if (_work.TryGetValue(key, out var current) && ReferenceEquals(current, work)) _work.Remove(key);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Waiter(Func<ProviderWorkPriority> Priority, PerformanceDiagnosticContext? Context, CancellationToken Token, long QueuedAt)
    {
        internal long AdmittedAt { get; } = Stopwatch.GetTimestamp();
    }

    private sealed class EntityWork(string id, long generation)
    {
        internal string Id { get; } = id;
        internal long Generation { get; } = generation;
        internal long QueuedAt { get; } = Stopwatch.GetTimestamp();
        internal List<Waiter> Waiters { get; } = [];
        internal TaskCompletionSource<WikimediaEntityClaims> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Batch? Batch { get; set; }
    }

    private sealed class Batch(EntityWork[] work) : IDisposable
    {
        internal EntityWork[] Work { get; } = work;
        internal List<Waiter> Participants { get; } = work.SelectMany(entity => entity.Waiters).ToList();
        internal string RequestId { get; } = Guid.NewGuid().ToString("N");
        internal long StartedAt { get; set; }
        internal bool Closed { get; set; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        public void Dispose() => Cancellation.Dispose();
    }
}

internal readonly record struct WikimediaClaimValue(string? Value, bool IsSuccess);
internal sealed record WikimediaEntityClaims(WikimediaClaimValue Image, WikimediaClaimValue Identifier, string? RequestId = null, ProviderFailure? Failure = null)
{
    internal static WikimediaEntityClaims Failed { get; } = new(new(null, false), new(null, false), Failure: new("invalid_response"));
}
