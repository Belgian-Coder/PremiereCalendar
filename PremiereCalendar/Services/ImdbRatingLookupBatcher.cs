using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PremiereCalendar.Services;

/// <summary>Coalesces concurrent local dataset lookups without retaining completed ratings.</summary>
internal static class ImdbRatingLookupBatcher
{
    private static readonly ConditionalWeakTable<IImdbRatingsStore, StoreBatcher> Stores = new();

    public static Task<ImdbRatingRecord?> GetAsync(IImdbRatingsStore store, string imdbId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(imdbId)) return Task.FromResult<ImdbRatingRecord?>(null);
        return Stores.GetValue(store, static value => new StoreBatcher(value)).GetAsync(imdbId.Trim(), cancellationToken);
    }

    private sealed class StoreBatcher(IImdbRatingsStore store)
    {
        private const int MaximumIds = 64;
        private const int MaximumConcurrentBatches = 2;
        private static readonly TimeSpan CoalescingWindow = TimeSpan.FromMilliseconds(10);
        private readonly Lock _gate = new();
        private readonly SemaphoreSlim _capacity = new(512, 512);
        private readonly Dictionary<string, Work> _work = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Work> _pending = [];
        private TaskCompletionSource _changed = Signal();
        private bool _draining;

        public async Task<ImdbRatingRecord?> GetAsync(string id, CancellationToken cancellationToken)
        {
            await _capacity.WaitAsync(cancellationToken);
            Work? work = null;
            var registered = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var startDrain = false;
                lock (_gate)
                {
                    if (!_work.TryGetValue(id, out work) || work.Batch?.Closed == true)
                    {
                        _work[id] = work = new Work(id);
                        _pending.Add(work);
                    }
                    work.Waiters++;
                    registered = true;
                    Pulse();
                    if (!_draining) startDrain = _draining = true;
                }
                if (startDrain) _ = DrainAsync();
                return await work.Completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Batch? cancel = null;
                if (registered)
                {
                    lock (_gate)
                    {
                        if (--work!.Waiters == 0 && work.Completion.Task.IsFaulted)
                            _ = work.Completion.Task.Exception;
                        if (work.Waiters == 0 && !work.Completion.Task.IsCompleted)
                        {
                            Remove(work);
                            _pending.Remove(work);
                            work.Completion.TrySetCanceled();
                            if (work.Batch is { Closed: false } batch && batch.Work.All(item => item.Waiters == 0))
                            {
                                batch.Closed = true;
                                foreach (var item in batch.Work) Remove(item);
                                cancel = batch;
                            }
                            Pulse();
                        }
                    }
                }
                if (cancel is not null)
                {
                    try { await cancel.Cancellation.CancelAsync(); }
                    catch (Exception) { /* Store callback failures must not replace caller cancellation. */ }
                    // A caller abandoning the final batch waits for the store to release I/O.
                    await cancel.Finished.Task;
                }
                _capacity.Release();
            }
        }

        private async Task DrainAsync()
        {
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
                    if (running.Count < MaximumConcurrentBatches && _pending.Count > 0)
                    {
                        var remaining = CoalescingWindow - Stopwatch.GetElapsedTime(_pending[0].QueuedAt);
                        if (remaining > TimeSpan.Zero) coalescing = Task.Delay(remaining);
                        else
                        {
                            var selected = _pending.Take(MaximumIds).ToArray();
                            _pending.RemoveRange(0, selected.Length);
                            batch = new Batch(selected);
                            foreach (var item in selected) item.Batch = batch;
                        }
                    }
                    else if (_pending.Count == 0 && running.Count == 0)
                    {
                        _draining = false;
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
            try
            {
                var results = await store.GetByImdbIdsAsync(batch.Work.Select(work => work.Id).ToArray(), batch.Cancellation.Token);
                lock (_gate)
                {
                    foreach (var work in batch.Work)
                    {
                        // Store adapters may use a case-sensitive result dictionary.
                        var value = results.TryGetValue(work.Id, out var exact) ? exact
                            : results.FirstOrDefault(pair => pair.Key.Equals(work.Id, StringComparison.OrdinalIgnoreCase)).Value;
                        work.Completion.TrySetResult(value);
                    }
                }
            }
            catch (Exception exception)
            {
                lock (_gate)
                    foreach (var work in batch.Work) work.Completion.TrySetException(exception);
            }
            finally
            {
                lock (_gate)
                {
                    batch.Closed = true;
                    foreach (var work in batch.Work) Remove(work);
                }
                batch.Cancellation.Dispose();
                batch.Finished.TrySetResult();
            }
        }

        private void Remove(Work work)
        {
            if (_work.TryGetValue(work.Id, out var current) && ReferenceEquals(current, work)) _work.Remove(work.Id);
        }

        private void Pulse()
        {
            var changed = _changed;
            _changed = Signal();
            changed.TrySetResult();
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Work(string id)
    {
        public string Id { get; } = id;
        public long QueuedAt { get; } = Stopwatch.GetTimestamp();
        public TaskCompletionSource<ImdbRatingRecord?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters;
        public Batch? Batch;
    }

    private sealed class Batch(Work[] work)
    {
        public Work[] Work { get; } = work;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Finished { get; } = Signal();
        public bool Closed;
    }
}
