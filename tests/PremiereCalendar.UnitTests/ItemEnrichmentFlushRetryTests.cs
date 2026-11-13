using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedItemFlushRetriesWithoutFetchingAgainAndPreservesNewerQueuedMetadata(bool concurrentNewerProposal)
    {
        var clock = new ItemCacheClock();
        var memory = new MemoryItemEnrichmentStore(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FlushRetryStore(memory, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        var coordinator = new ItemEnrichmentCacheCoordinator(store,
            Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions()),
            NullLogger<ItemEnrichmentCacheCoordinator>.Instance, timeProvider: clock);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 901);
        await using var session = await coordinator.CreateSessionAsync(default);
        await session.PrepareAsync([identity], null, default);
        var original = new ItemEnrichmentMetadata { Title = "Original provider result", ImdbId = "tt0000901", RuntimeMinutes = 47, AvailabilityComplete = true };
        var ratings = new ItemEnrichmentRatings(7.4, 740, null, null, null, ImdbId: original.ImdbId, Title: original.Title, Year: 2026);
        var providerCalls = 0;
        var proposal = await session.GetAsync(identity, true, (_, _, _, _) =>
        {
            providerCalls++;
            return Task.FromResult(new ItemEnrichmentRefresh(original, ratings));
        }, default);
        Assert.True(session.HasPendingWrites);
        var failedFlush = session.FlushAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(session.HasPendingWrites); // The first batch has already drained.
            if (concurrentNewerProposal)
            {
                clock.Advance(TimeSpan.FromDays(8));
                proposal = await session.GetAsync(identity, false, (_, metadataDue, ratingsDue, _) =>
                {
                    Assert.True(metadataDue);
                    Assert.False(ratingsDue);
                    providerCalls++;
                    return Task.FromResult(new ItemEnrichmentRefresh(original with { Title = "Newer concurrent result", RuntimeMinutes = 99 },
                        null, RefreshRatingsClock: false));
                }, default, metadataOnly: true);
                Assert.True(session.HasPendingWrites);
            }
        }
        finally { release.TrySetResult(); }
        await failedFlush.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(session.HasPendingWrites);
        Assert.Null(Assert.Single(memory.Records.Values).MetadataJson);

        var callsBeforeRetry = providerCalls;
        await session.FlushAsync(default);
        Assert.Equal(callsBeforeRetry, providerCalls);
        Assert.False(session.HasPendingWrites);
        var accepted = Assert.Single(memory.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(accepted.MetadataJson!)!;
        Assert.Equal(concurrentNewerProposal ? "Newer concurrent result" : original.Title, metadata.Title);
        Assert.Equal(concurrentNewerProposal ? 99 : 47, metadata.RuntimeMinutes);
        Assert.Equal(proposal.Record.MetadataFetchedUtc, accepted.MetadataFetchedUtc);
        Assert.Equal(proposal.Record.RatingsFetchedUtc, accepted.RatingsFetchedUtc);
        Assert.Equal(7.4, JsonSerializer.Deserialize<ItemEnrichmentRatings>(accepted.RatingsJson!)!.ImdbScore);
        var attempts = store.Attempts.ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(attempts[0][0].RatingsJson, attempts[1][0].RatingsJson);
        Assert.Equal(attempts[0][0].RatingsFetchedUtc, attempts[1][0].RatingsFetchedUtc);
        if (concurrentNewerProposal) Assert.NotEqual(attempts[0][0].MetadataJson, attempts[1][0].MetadataJson);
        else Assert.Equal(attempts[0][0], attempts[1][0]);
    }

    [Fact]
    public async Task LargeItemBatchReturnsEveryAcceptedWinnerAndValidationDoesNotTouchRetention()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var clock = new ItemCacheClock();
        var now = clock.GetUtcNow();
        var store = new ItemEnrichmentStore(database.Options, clock);
        var keys = Enumerable.Range(1, 2500).Select(id => new ItemEnrichmentIdentity(PremiereMediaType.Movie, id)).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.Equal(keys.Length, (await store.ReadManyAsync("bulk", keys, true, deadline.Token)).Count);
        var writes = keys.Select(key => new ItemEnrichmentWrite(key, 0,
            JsonSerializer.Serialize(new { Title = $"Bulk {key.TmdbId}", RuntimeMinutes = key.TmdbId }), now,
            ExpectedMetadataRevision: 0)).ToArray();
        var accepted = await store.WriteAcceptedAsync("bulk", writes, deadline.Token);
        Assert.Equal(keys.Length, accepted.Count);
        Assert.All(accepted, pair =>
        {
            Assert.Equal(now, pair.Value.MetadataFetchedUtc);
            Assert.Equal(1, pair.Value.MetadataRevision);
            Assert.Equal(pair.Key.TmdbId, JsonSerializer.Deserialize<ItemEnrichmentMetadata>(pair.Value.MetadataJson!)!.RuntimeMinutes);
        });
        clock.Advance(TimeSpan.FromHours(1));
        var validated = await store.ReadCurrentManyAsync("bulk", keys, deadline.Token);
        Assert.Equal(keys.Length, validated.Count);
        Assert.All(validated.Values, record => Assert.Equal(now, record.LastSeenUtc));
    }

    private sealed class FlushRetryStore(IItemEnrichmentStore inner, Func<CancellationToken, Task> beforeFailure) : IItemEnrichmentStore
    {
        public ConcurrentQueue<ItemEnrichmentWrite[]> Attempts { get; } = new();
        private int _writes;
        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadManyAsync(string partition,
            IReadOnlyCollection<ItemEnrichmentIdentity> identities, bool reserveMissing, CancellationToken cancellationToken)
            => inner.ReadManyAsync(partition, identities, reserveMissing, cancellationToken);
        public async Task<int> WriteManyAsync(string partition, IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken cancellationToken)
        {
            Attempts.Enqueue(writes.ToArray());
            if (Interlocked.Increment(ref _writes) == 1)
            {
                await beforeFailure(cancellationToken);
                throw new IOException("Simulated transient flush failure.");
            }
            return await inner.WriteManyAsync(partition, writes, cancellationToken);
        }
        public Task<int> InvalidateTmdbAsync(PremiereMediaType mediaType, IReadOnlyCollection<int> tmdbIds, DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken) => inner.InvalidateTmdbAsync(mediaType, tmdbIds, changedAtUtc, cancellationToken);
        public Task<int> InvalidateTvmazeAsync(IReadOnlyCollection<int> showIds, DateTimeOffset changedAtUtc, CancellationToken cancellationToken)
            => inner.InvalidateTvmazeAsync(showIds, changedAtUtc, cancellationToken);
        public Task<int> CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) => inner.CleanupAsync(cutoffUtc, cancellationToken);
    }
}
