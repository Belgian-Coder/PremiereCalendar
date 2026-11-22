using System.Collections.Concurrent;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

// Tests of this class run in parallel with each other (ParallelMode.All ignores [Collection]). Batch membership never
// depends on the 10 ms coalescing window: the store slots are occupied or the callers are held by gates.
public sealed class ImdbRatingLookupBatcherTests
{
    [Fact]
    public async Task CandidateIdsUseSmallBatchesAndDuplicateCallersShareTheirLookup()
    {
        var firstStarted = Signal();
        var warmupStarted = Signal();
        var release = Signal();
        var store = new Store(async (ids, token) =>
        {
            if (ids.Contains("tt-first")) firstStarted.TrySetResult();
            else if (ids.Contains("tt-warmup")) warmupStarted.TrySetResult();
            else return Ratings(ids);
            await release.Task.WaitAsync(token);
            return Ratings(ids);
        });
        var first = ImdbRatingLookupBatcher.GetAsync(store, "tt-first", CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        // A second blocked batch occupies both store slots, so every candidate below queues together and forms
        // one batch independently of how long caller creation takes (the 10 ms coalescing window alone could split them).
        var warmup = ImdbRatingLookupBatcher.GetAsync(store, "tt-warmup", CancellationToken.None);
        await warmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var ids = Enumerable.Range(0, 40).Select(index => $"tt{index:0000000}").ToArray();
        var lookups = ids.Select(id => ImdbRatingLookupBatcher.GetAsync(store, id, CancellationToken.None)).ToArray();
        var duplicate = ImdbRatingLookupBatcher.GetAsync(store, "TT-FIRST", CancellationToken.None);
        release.SetResult();

        var results = await Task.WhenAll(lookups).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(ids, results.Select(result => result!.ImdbId));
        Assert.Equal(await first, await duplicate);
        Assert.NotNull(await warmup);
        // The in-flight first batch, the warmup batch and one batch for all 40 candidates: the duplicate adds no lookup.
        Assert.Equal(3, store.Batches.Count);
        Assert.Equal(1, store.Batches.Sum(batch => batch.Count(id => id.Equals("tt-first", StringComparison.OrdinalIgnoreCase))));
        Assert.Single(store.Batches, batch => batch.SequenceEqual(ids));
        Assert.Equal(42, store.Batches.Sum(batch => batch.Length));
        Assert.All(store.Batches, batch => Assert.InRange(batch.Length, 1, 64));
        Assert.Equal(0, store.SingleLookups);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelTheSharedStoreQuery()
    {
        var started = Signal();
        var release = Signal();
        var storeToken = CancellationToken.None;
        var store = new Store(async (ids, token) =>
        {
            storeToken = token;
            started.SetResult();
            await release.Task.WaitAsync(token);
            return Ratings(ids);
        });
        using var cancellation = new CancellationTokenSource();
        var cancelled = ImdbRatingLookupBatcher.GetAsync(store, "tt1", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var surviving = ImdbRatingLookupBatcher.GetAsync(store, "tt1", CancellationToken.None);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(storeToken.IsCancellationRequested);
        release.SetResult();
        Assert.Equal("tt1", (await surviving)!.ImdbId);
        Assert.Single(store.Batches);
    }

    [Fact]
    public async Task FinalWaiterCancelsAndObservesStoreCleanupAndNewCallsAvoidTheAbandonedBatch()
    {
        var started = Signal();
        var cleanupStarted = Signal();
        var releaseCleanup = Signal();
        var calls = 0;
        var store = new Store(async (ids, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { cleanupStarted.SetResult(); await releaseCleanup.Task; }
            }
            return Ratings(ids);
        });
        using var cancellation = new CancellationTokenSource();
        var abandoned = ImdbRatingLookupBatcher.GetAsync(store, "tt1", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(abandoned.IsCompleted);

        Assert.Equal("tt1", (await ImdbRatingLookupBatcher.GetAsync(store, "tt1", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))!.ImdbId);
        releaseCleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.Equal(0, store.Active);
    }

    [Fact]
    public async Task StoreFailuresPropagateAndDoNotBecomeCachedMissingRatings()
    {
        var calls = 0;
        var store = new Store((ids, _) => Interlocked.Increment(ref calls) == 1
            ? Task.FromException<IReadOnlyDictionary<string, ImdbRatingRecord>>(new IOException("Unavailable dataset"))
            : Task.FromResult(Ratings(ids)));

        await Assert.ThrowsAsync<IOException>(() => ImdbRatingLookupBatcher.GetAsync(store, "tt1", CancellationToken.None));
        Assert.NotNull(await ImdbRatingLookupBatcher.GetAsync(store, "tt1", CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task LargeCancelledQueueKeepsAtMostTwoStoreBatchesActiveAndCanRestart()
    {
        var twoStarted = Signal();
        var block = true;
        var calls = 0;
        var store = new Store(async (ids, token) =>
        {
            if (Interlocked.Increment(ref calls) == 2) twoStarted.SetResult();
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ratings(ids);
        });
        using var cancellation = new CancellationTokenSource();
        var pending = Enumerable.Range(0, 700).Select(index =>
            ImdbRatingLookupBatcher.GetAsync(store, $"tt{index:0000000}", cancellation.Token)).ToArray();
        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(2, store.MaximumActive);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(0, store.Active);
        Assert.All(store.Batches, batch => Assert.InRange(batch.Length, 1, 64));
        block = false;
        Assert.NotNull(await ImdbRatingLookupBatcher.GetAsync(store, "tt-restarted", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeparateStoresAndCompletedCallsNeverReuseAnOldRating()
    {
        var first = new Store((ids, _) => Task.FromResult(Ratings(ids)));
        var second = new Store((_, _) => Task.FromResult<IReadOnlyDictionary<string, ImdbRatingRecord>>(new Dictionary<string, ImdbRatingRecord>()));
        Assert.NotNull(await ImdbRatingLookupBatcher.GetAsync(first, "tt1", CancellationToken.None));
        Assert.Null(await ImdbRatingLookupBatcher.GetAsync(second, "tt1", CancellationToken.None));
        Assert.NotNull(await ImdbRatingLookupBatcher.GetAsync(first, "tt1", CancellationToken.None));
        Assert.Equal(2, first.Batches.Count);
        Assert.Single(second.Batches);
    }

    private static IReadOnlyDictionary<string, ImdbRatingRecord> Ratings(IReadOnlyCollection<string> ids)
        => ids.ToDictionary(id => id, id => new ImdbRatingRecord(id, 7.5, 123, DateTimeOffset.UnixEpoch));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Store(Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, ImdbRatingRecord>>> read) : IImdbRatingsStore
    {
        public ConcurrentQueue<string[]> Batches { get; } = new();
        public int SingleLookups { get; private set; }
        private int _active;
        private int _maximum;
        public int Active => Volatile.Read(ref _active);
        public int MaximumActive => Volatile.Read(ref _maximum);
        public Task<ImdbRatingRecord?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken)
        {
            SingleLookups++;
            throw new InvalidOperationException("Batch lookup must use the existing store batch API.");
        }
        public async Task<IReadOnlyDictionary<string, ImdbRatingRecord>> GetByImdbIdsAsync(IReadOnlyCollection<string> ids, CancellationToken token)
        {
            Batches.Enqueue(ids.ToArray());
            var active = Interlocked.Increment(ref _active);
            int maximum;
            do { maximum = Volatile.Read(ref _maximum); }
            while (active > maximum && Interlocked.CompareExchange(ref _maximum, active, maximum) != maximum);
            try { return await read(ids, token); }
            finally { Interlocked.Decrement(ref _active); }
        }
        public Task ReplaceAllAsync(IEnumerable<ImdbRatingRecord> ratings, DateTimeOffset importedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImdbDatasetState> GetStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveStateAsync(ImdbDatasetState state, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
