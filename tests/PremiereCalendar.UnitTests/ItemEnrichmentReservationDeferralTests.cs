using Microsoft.Data.Sqlite;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TvmazeReservationLocalBudgetFailureDefersOnlyItsSessionAndCanRecoverImmediately(bool operationBudget)
    {
        var clock = new ItemCacheClock();
        var memory = new MemoryItemEnrichmentStore(clock);
        var store = new ReservationFailureStore(memory)
        {
            Failure = operationBudget ? new ItemEnrichmentStoreOperationTimeoutException(new TimeoutException())
                : new ItemEnrichmentStoreBusyException(new TimeoutException())
        };
        var coordinator = ItemCacheCoordinatorForReservation(store, clock);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        await using var failedSession = await coordinator.CreateSessionAsync(default);
        await failedSession.PrepareAsync([identity], null, default);
        var providers = 0;
        Task<ItemEnrichmentRefresh> Refresh(ItemEnrichmentSnapshot previous, bool _, bool __, CancellationToken token)
        { providers++; return Task.FromResult(new ItemEnrichmentRefresh(null, null, RefreshMetadataClock: false, RefreshRatingsClock: false)); }

        var first = await Assert.ThrowsAsync<ProviderRetryAfterException>(() => failedSession.GetAsync(identity, true, Refresh, default));
        Assert.Equal(clock.GetUtcNow().AddSeconds(5), first.RetryAtUtc);
        Assert.Equal(1, store.Reservations);
        Assert.Equal(0, providers);
        clock.Advance(TimeSpan.FromSeconds(1));
        var sameSession = await Assert.ThrowsAsync<ProviderRetryAfterException>(() => failedSession.GetAsync(identity, true, Refresh, default));
        Assert.Equal(first.RetryAtUtc, sameSession.RetryAtUtc);
        Assert.Equal(1, store.Reservations);

        store.Failure = null;
        await using var healthySession = await coordinator.CreateSessionAsync(default);
        await healthySession.PrepareAsync([identity], null, default);
        await healthySession.GetAsync(identity, true, Refresh, default);
        Assert.Equal(2, store.Reservations);
        Assert.Equal(1, providers);
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TvmazeReservationStorageOutageUsesTheSharedReadBackoffWithoutExtendingIt(bool transientSqlite)
    {
        var clock = new ItemCacheClock();
        var memory = new MemoryItemEnrichmentStore(clock);
        var store = new ReservationFailureStore(memory)
            { Failure = transientSqlite ? new SqliteException("Transient busy connection", 5) : new IOException("Unavailable store") };
        var coordinator = ItemCacheCoordinatorForReservation(store, clock);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var providers = 0;
        Task<ItemEnrichmentRefresh> Refresh(ItemEnrichmentSnapshot previous, bool _, bool __, CancellationToken token)
        { providers++; return Task.FromResult(new ItemEnrichmentRefresh(null, null, RefreshMetadataClock: false, RefreshRatingsClock: false)); }
        await using var failedSession = await coordinator.CreateSessionAsync(default);
        await failedSession.PrepareAsync([identity], null, default);
        var first = await Assert.ThrowsAsync<ProviderRetryAfterException>(() => failedSession.GetAsync(identity, true, Refresh, default));
        Assert.Equal(clock.GetUtcNow().AddSeconds(30), first.RetryAtUtc);
        clock.Advance(TimeSpan.FromSeconds(10));
        await using var deferredSession = await coordinator.CreateSessionAsync(default);
        await deferredSession.PrepareAsync([identity], null, default);
        var deferred = await Assert.ThrowsAsync<ProviderRetryAfterException>(() => deferredSession.GetAsync(identity, true, Refresh, default));
        Assert.Equal(first.RetryAtUtc, deferred.RetryAtUtc);
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, store.Reservations);
        Assert.Equal(0, providers);

        clock.Advance(TimeSpan.FromSeconds(21));
        store.Failure = null;
        await using var recoveredSession = await coordinator.CreateSessionAsync(default);
        await recoveredSession.PrepareAsync([identity], null, default);
        await recoveredSession.GetAsync(identity, true, Refresh, default);
        Assert.Equal(2, store.Reads);
        Assert.Equal(2, store.Reservations);
        Assert.Equal(1, providers);
    }

    [Fact]
    public async Task ItemCacheReadFailureIsATimedBackoffThatTheSameSessionRecoversFrom()
    {
        var clock = new ItemCacheClock();
        var store = new ReservationFailureStore(new MemoryItemEnrichmentStore(clock)) { ReadFailure = new IOException("Unavailable store") };
        var coordinator = ItemCacheCoordinatorForReservation(store, clock);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var providers = 0;
        Task<ItemEnrichmentRefresh> Refresh(ItemEnrichmentSnapshot previous, bool _, bool __, CancellationToken token)
        { providers++; return Task.FromResult(new ItemEnrichmentRefresh(null, null, RefreshMetadataClock: false, RefreshRatingsClock: false)); }
        await using var session = await coordinator.CreateSessionAsync(default);
        await session.PrepareAsync([identity], null, default);
        var failed = await Assert.ThrowsAsync<ProviderRetryAfterException>(() => session.GetAsync(identity, true, Refresh, default));
        Assert.Equal(clock.GetUtcNow().AddSeconds(30), failed.RetryAtUtc);
        var readsAfterFailure = store.Reads;

        // Inside the backoff window the session keeps failing fast without touching the store.
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ProviderRetryAfterException>(() => session.GetAsync(identity, true, Refresh, default));
        Assert.Equal(readsAfterFailure, store.Reads);
        Assert.Equal(0, providers);

        // Once the retry time has passed, the missing read is repeated and the item is usable again.
        clock.Advance(TimeSpan.FromSeconds(21));
        store.ReadFailure = null;
        await session.GetAsync(identity, true, Refresh, default);
        Assert.True(store.Reads > readsAfterFailure);
        Assert.Equal(1, providers);
    }

    private static async Task<(ItemEnrichmentCacheCoordinator.ItemEnrichmentSession Session, ReservationFailureStore Store, ItemCacheClock Clock)> FailedReadSessionAsync(
        ItemEnrichmentIdentity identity)
    {
        var clock = new ItemCacheClock();
        var store = new ReservationFailureStore(new MemoryItemEnrichmentStore(clock)) { ReadFailure = new IOException("Unavailable store") };
        var session = await ItemCacheCoordinatorForReservation(store, clock).CreateSessionAsync(default);
        await session.PrepareAsync([identity], null, default);
        await Assert.ThrowsAsync<ProviderRetryAfterException>(() => session.GetAsync(identity, true, NoopRefresh, default));
        store.ReadFailure = null;
        clock.Advance(TimeSpan.FromSeconds(31));
        return (session, store, clock);
    }

    private static Task<ItemEnrichmentRefresh> NoopRefresh(ItemEnrichmentSnapshot previous, bool _, bool __, CancellationToken token)
        => Task.FromResult(new ItemEnrichmentRefresh(null, null, RefreshMetadataClock: false, RefreshRatingsClock: false));

    [Fact]
    public async Task CancelledStorageRecoveryLeavesTheFailedStateUntouchedAndRecoversLater()
    {
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var (session, store, _) = await FailedReadSessionAsync(identity);
        await using var owned = session;
        store.BeforeRead = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetAsync(identity, true, NoopRefresh, cancellation.Token));

        // Nothing was removed or cleared: the placeholder row is still there and the session still fails fast.
        Assert.Equal(-1, session.Find(identity)!.Record.Generation);
        store.BeforeRead = null;
        var reads = store.Reads;
        await session.GetAsync(identity, true, NoopRefresh, default);
        Assert.True(store.Reads > reads);
        Assert.True(session.Find(identity)!.Record.Generation >= 0);
    }

    [Fact]
    public async Task ConcurrentGetDuringStorageRecoveryFailsFastInsteadOfSeeingMissingRows()
    {
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var (session, store, _) = await FailedReadSessionAsync(identity);
        await using var owned = session;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeRead = async token => { started.TrySetResult(); await release.Task.WaitAsync(token); };

        var recovering = session.GetAsync(identity, true, NoopRefresh, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrent = await Assert.ThrowsAsync<ProviderRetryAfterException>(() => session.GetAsync(identity, true, NoopRefresh, default));
        Assert.True(concurrent.RetryAtUtc > DateTimeOffset.MinValue);
        Assert.Equal(-1, session.Find(identity)!.Record.Generation);

        release.SetResult();
        await recovering.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Find(identity)!.Record.Generation >= 0);
    }

    [Fact]
    public async Task TvmazeReservationCallerCancellationKeepsSharedReservationAndDoesNotTripStorageBackoff()
    {
        var clock = new ItemCacheClock();
        var store = new ReservationFailureStore(new MemoryItemEnrichmentStore(clock));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        store.Reserve = async token =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return new ReservationTestLease(() => Interlocked.Increment(ref disposed));
        };
        var coordinator = ItemCacheCoordinatorForReservation(store, clock);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        await using var session = await coordinator.CreateSessionAsync(default);
        await session.PrepareAsync([identity], null, default);
        var providers = 0;
        Task<ItemEnrichmentRefresh> Refresh(ItemEnrichmentSnapshot previous, bool _, bool __, CancellationToken token)
        { providers++; return Task.FromResult(new ItemEnrichmentRefresh(null, null, RefreshMetadataClock: false, RefreshRatingsClock: false)); }
        using var cancellation = new CancellationTokenSource();
        var abandoned = session.GetAsync(identity, true, Refresh, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        var surviving = session.GetAsync(identity, true, Refresh, default);
        Assert.Equal(1, store.Reservations);
        release.SetResult();
        await surviving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, providers);
        await session.DisposeAsync();
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task TvmazeReservationLifetimeCancellationDoesNotPoisonTheNextSession()
    {
        var clock = new ItemCacheClock();
        var store = new ReservationFailureStore(new MemoryItemEnrichmentStore(clock));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Reserve = async token =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return new ReservationTestLease(() => { });
        };
        var coordinator = ItemCacheCoordinatorForReservation(store, clock);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        using var lifetime = new CancellationTokenSource();
        await using var cancelledSession = await coordinator.CreateSessionAsync(lifetime.Token);
        await cancelledSession.PrepareAsync([identity], null, default);
        Task<ItemEnrichmentRefresh> Refresh(ItemEnrichmentSnapshot previous, bool _, bool __, CancellationToken token)
            => Task.FromResult(new ItemEnrichmentRefresh(null, null, RefreshMetadataClock: false, RefreshRatingsClock: false));
        var abandoned = cancelledSession.GetAsync(identity, true, Refresh, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await lifetime.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        release.SetResult();
        await using var nextSession = await coordinator.CreateSessionAsync(default);
        await nextSession.PrepareAsync([identity], null, default);
        await nextSession.GetAsync(identity, true, Refresh, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, store.Reads);
        Assert.Equal(2, store.Reservations);
    }

    private static ItemEnrichmentCacheCoordinator ItemCacheCoordinatorForReservation(IItemEnrichmentStore store, ItemCacheClock clock) => new(
        store, Microsoft.Extensions.Options.Options.Create(new PremiereCalendar.Options.ItemEnrichmentCacheOptions()),
        Microsoft.Extensions.Options.Options.Create(new PremiereCalendar.Options.TmdbOptions()),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ItemEnrichmentCacheCoordinator>.Instance, timeProvider: clock);

    private sealed class ReservationFailureStore(IItemEnrichmentStore inner) : IItemEnrichmentStore
    {
        public Exception? Failure;
        public Exception? ReadFailure;
        public Func<CancellationToken, Task<IAsyncDisposable>>? Reserve;
        public int Reads;
        public int Reservations;
        public int Writes;
        public Func<CancellationToken, Task>? BeforeRead;
        public async Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadManyAsync(string partition,
            IReadOnlyCollection<ItemEnrichmentIdentity> identities, bool reserveMissing, CancellationToken token)
        {
            Reads++;
            if (BeforeRead is { } before) await before(token);
            if (ReadFailure is { } readFailure) throw readFailure;
            return await inner.ReadManyAsync(partition, identities, reserveMissing, token);
        }
        public Task<IAsyncDisposable> ReserveTvmazeLookupAsync(string partition, ItemEnrichmentIdentity identity, CancellationToken token)
        {
            Reservations++;
            return Reserve is not null ? Reserve(token) : Failure is { } failure ? Task.FromException<IAsyncDisposable>(failure)
                : inner.ReserveTvmazeLookupAsync(partition, identity, token);
        }
        public Task<int> WriteManyAsync(string partition, IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken token)
        { Writes++; return inner.WriteManyAsync(partition, writes, token); }
        public Task<int> InvalidateTmdbAsync(PremiereMediaType type, IReadOnlyCollection<int> ids, DateTimeOffset changed, CancellationToken token)
            => inner.InvalidateTmdbAsync(type, ids, changed, token);
        public Task<int> InvalidateTvmazeAsync(IReadOnlyCollection<int> ids, DateTimeOffset changed, CancellationToken token)
            => inner.InvalidateTvmazeAsync(ids, changed, token);
        public Task<int> CleanupAsync(DateTimeOffset cutoff, CancellationToken token) => inner.CleanupAsync(cutoff, token);
    }

    private sealed class ReservationTestLease(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
    }
}
