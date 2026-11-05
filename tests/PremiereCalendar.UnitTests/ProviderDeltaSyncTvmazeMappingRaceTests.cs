using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class ProviderDeltaSyncServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TvmazeFirstMappingCannotEscapeUpdateBetweenKnownReadAndUnmappedFence(bool publishBeforeFence)
    {
        var now = DateTimeOffset.Parse("2026-05-16T12:00:00Z");
        var store = new FirstMappingRaceStore(publishBeforeFence);
        var states = new InMemoryProviderCacheStateStore();
        var tvmaze = new EmptyTvmazeClient { Updates = [new(7, now.AddMinutes(-1))] };
        var service = new ProviderDeltaSyncService(new RecordingTmdbClient(), tvmaze, states,
            new FixedOptionsMonitor<ProviderDeltaSyncOptions>(new() { UseTmdbChanges = false }),
            new FixedTimeProvider(now), NullLogger<ProviderDeltaSyncService>.Instance, itemEnrichmentStore: store);

        await service.RunOnceAsync(default);

        // The lookup was reserved before the feed observation. Its first mapping
        // must either be generation-fenced or be seen by targeted invalidation.
        Assert.False(store.MetadataFresh);
        Assert.Equal(publishBeforeFence, store.MappingAccepted);
        Assert.Equal(publishBeforeFence, store.TargetedInvalidation);
        Assert.Equal(!publishBeforeFence, store.UnmappedFence);
        Assert.Equal(1, (await states.GetAsync("tvmaze", ProviderCacheScope.Global, "show-updates", default))!.ItemCount);
    }

    private sealed class FirstMappingRaceStore(bool publishBeforeFence) : IItemEnrichmentStore
    {
        private bool _mapped;
        private bool _mappingAttempted;
        private long _generation;
        public bool MappingAccepted { get; private set; }
        public bool MetadataFresh { get; private set; }
        public bool TargetedInvalidation { get; private set; }
        public bool UnmappedFence { get; private set; }

        private void CompleteFirstLookup()
        {
            if (_mappingAttempted) return;
            _mappingAttempted = true;
            if (_generation != 0) return; // The first lookup captured generation 0.
            MappingAccepted = MetadataFresh = _mapped = true;
        }

        public Task<IReadOnlySet<int>> FindKnownTvmazeIdsAsync(IReadOnlyCollection<int> candidates, CancellationToken token)
        {
            IReadOnlySet<int> snapshot = _mapped && candidates.Contains(7) ? new HashSet<int> { 7 } : new HashSet<int>();
            // Reproduces the old ordering deterministically: publication occurs
            // after the known-ID snapshot was read and before a later fence call.
            CompleteFirstLookup();
            return Task.FromResult(snapshot);
        }

        public Task<int> FenceUnmappedTvmazeReservationsAsync(DateTimeOffset epoch, CancellationToken token)
        {
            if (publishBeforeFence) CompleteFirstLookup();
            if (_mapped) return Task.FromResult(0);
            _generation++;
            UnmappedFence = true;
            return Task.FromResult(1);
        }

        public Task<int> InvalidateTvmazeAsync(IReadOnlyCollection<int> ids, DateTimeOffset changedAt, CancellationToken token)
        {
            if (!_mapped || !ids.Contains(7)) return Task.FromResult(0);
            _generation++;
            TargetedInvalidation = true;
            MetadataFresh = false;
            return Task.FromResult(1);
        }

        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadManyAsync(string partition,
            IReadOnlyCollection<ItemEnrichmentIdentity> identities, bool reserveMissing, CancellationToken token)
            => throw new NotSupportedException();
        public Task<int> WriteManyAsync(string partition, IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken token)
            => throw new NotSupportedException();
        public Task<int> InvalidateTmdbAsync(PremiereMediaType type, IReadOnlyCollection<int> ids, DateTimeOffset changedAt, CancellationToken token)
            => throw new NotSupportedException();
        public Task<int> CleanupAsync(DateTimeOffset cutoff, CancellationToken token) => Task.FromResult(0);
    }
}
