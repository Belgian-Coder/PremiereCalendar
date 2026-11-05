using System.Collections.Concurrent;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ImdbRatingLookupBatcherTests
{
    [Fact]
    public async Task CandidateIdsUseSmallBatchesAndDuplicateCallersShareTheirLookup()
    {
        var firstStarted = Signal();
        var secondStarted = Signal();
        var release = Signal();
        var calls = 0;
        var store = new Store(async (ids, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) firstStarted.SetResult();
            else secondStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return Ratings(ids);
        });
        var first = ImdbRatingLookupBatcher.GetAsync(store, "tt-first", CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var ids = Enumerable.Range(0, 40).Select(index => $"tt{index:0000000}").ToArray();
        var lookups = ids.Select(id => ImdbRatingLookupBatcher.GetAsync(store, id, CancellationToken.None)).ToArray();
        var duplicate = ImdbRatingLookupBatcher.GetAsync(store, "TT-FIRST", CancellationToken.None);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();

        var results = await Task.WhenAll(lookups).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ids, results.Select(result => result!.ImdbId));
        Assert.Equal(await first, await duplicate);
        Assert.Equal(2, store.Batches.Count);
        Assert.Equal(41, store.Batches.Sum(batch => batch.Length));
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
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
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
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(abandoned.IsCompleted);

        Assert.Equal("tt1", (await ImdbRatingLookupBatcher.GetAsync(store, "tt1", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10)))!.ImdbId);
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
        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, store.MaximumActive);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, store.Active);
        Assert.All(store.Batches, batch => Assert.InRange(batch.Length, 1, 64));
        block = false;
        Assert.NotNull(await ImdbRatingLookupBatcher.GetAsync(store, "tt-restarted", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10)));
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
