using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed record ItemEnrichmentIdentity(PremiereMediaType MediaType, int TmdbId);

public sealed record ItemEnrichmentRecord(
    ItemEnrichmentIdentity Identity,
    string? MetadataJson,
    string? RatingsJson,
    DateTimeOffset? MetadataFetchedUtc,
    DateTimeOffset? RatingsFetchedUtc,
    DateTimeOffset LastSeenUtc,
    long Generation,
    int? TvmazeShowId,
    DateTimeOffset? MetadataInvalidatedUtc,
    DateTimeOffset? TmdbObservedUtc = null,
    long MetadataRevision = 0);

// A missing component means "leave its last successful value and clock alone".
// Same-clock metadata repairs require the revision captured by ReadManyAsync.
// Legacy/unverified ratings retain that same revision guard. Explicit tracked
// provider projections are merged atomically by their own clocks and accepted
// metadata identities; they cannot replace core metadata through that merge.
// ExpectedRatingsJson authorizes a same-clock final projection repair only while
// both that exact payload and ExpectedMetadataRevision remain current.
public sealed record ItemEnrichmentWrite(
    ItemEnrichmentIdentity Identity,
    long ExpectedGeneration,
    string? MetadataJson = null,
    DateTimeOffset? MetadataFetchedUtc = null,
    string? RatingsJson = null,
    DateTimeOffset? RatingsFetchedUtc = null,
    int? TvmazeShowId = null,
    DateTimeOffset? TmdbObservedUtc = null,
    long? ExpectedMetadataRevision = null,
    string? ExpectedRatingsJson = null,
    string? ExpectedTvmazeReservationToken = null);

public interface IItemEnrichmentLookupLease : IAsyncDisposable
{
    string Token { get; }
    DateTimeOffset ExpiresUtc { get; }
}

public interface IItemEnrichmentStore
{
    // Reserve misses before provider requests so invalidation can fence their writes.
    // Reads update retention activity, never the component success clocks.
    Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadManyAsync(
        string partition,
        IReadOnlyCollection<ItemEnrichmentIdentity> identities,
        bool reserveMissing,
        CancellationToken cancellationToken);

    Task<int> WriteManyAsync(
        string partition,
        IReadOnlyList<ItemEnrichmentWrite> writes,
        CancellationToken cancellationToken);

    // Validation must not refresh retention or take reservation write locks.
    Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadCurrentManyAsync(
        string partition, IReadOnlyCollection<ItemEnrichmentIdentity> identities, CancellationToken cancellationToken)
        => ReadManyAsync(partition, identities, false, cancellationToken);

    async Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> WriteAcceptedAsync(
        string partition, IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken cancellationToken)
    {
        await WriteManyAsync(partition, writes, cancellationToken);
        return await ReadCurrentManyAsync(partition, writes.Select(write => write.Identity).Distinct().ToArray(), cancellationToken);
    }

    Task<IReadOnlySet<int>> FindKnownTvmazeIdsAsync(IReadOnlyCollection<int> candidateIds, CancellationToken cancellationToken)
        // Legacy adapters retain conservative invalidation coverage. Concrete
        // stores narrow this set to locally retained identities.
        => Task.FromResult<IReadOnlySet<int>>(candidateIds.Where(id => id > 0).ToHashSet());

    // Only skip a history request if every retained partition is already dirty.
    Task<IReadOnlySet<int>> FindDirtyTmdbIdsAsync(PremiereMediaType mediaType,
        IReadOnlyCollection<int> candidateIds, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());

    Task<IReadOnlySet<int>> FindKnownTmdbIdsAsync(
        PremiereMediaType mediaType,
        IReadOnlyCollection<int> candidateIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());

    // Invalidation retains payloads and success clocks for fallback. Metadata is
    // current only if MetadataFetchedUtc >= MetadataInvalidatedUtc, when present.
    Task<int> InvalidateTmdbAsync(
        PremiereMediaType mediaType,
        IReadOnlyCollection<int> tmdbIds,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken);

    Task<int> InvalidateTvmazeAsync(
        IReadOnlyCollection<int> showIds,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken);

    async Task<int> InvalidateTmdbManyAsync(
        PremiereMediaType mediaType,
        IReadOnlyDictionary<int, DateTimeOffset> changes,
        CancellationToken cancellationToken)
    {
        var updated = 0;
        foreach (var group in changes.GroupBy(change => change.Value))
            updated += await InvalidateTmdbAsync(mediaType, group.Select(change => change.Key).ToArray(), group.Key, cancellationToken);
        return updated;
    }

    async Task<int> InvalidateTvmazeManyAsync(
        IReadOnlyDictionary<int, DateTimeOffset> changes,
        CancellationToken cancellationToken)
    {
        var updated = 0;
        foreach (var group in changes.GroupBy(change => change.Value))
            updated += await InvalidateTvmazeAsync(group.Select(change => change.Key).ToArray(), group.Key, cancellationToken);
        return updated;
    }

    // Hold through publication (or session disposal), not just provider completion.
    // Stamp its token and captured metadata revision on writes. A bounded validity
    // window retires abandoned markers; expired tokens cannot publish their results.
    // Distinct leases protect overlapping lookups across stores and processes.
    Task<IAsyncDisposable> ReserveTvmazeLookupAsync(string partition, ItemEnrichmentIdentity identity,
        CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable>(EmptyItemEnrichmentLease.Instance);

    // A first lookup has no show ID to match against a TVmaze update feed yet.
    // Fence unresolved and active lookup reservations without rewriting saved idle metadata.
    Task<int> FenceUnmappedTvmazeReservationsAsync(DateTimeOffset epochUtc, CancellationToken cancellationToken) =>
        Task.FromResult(0);

    Task<int> CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}

internal sealed class EmptyItemEnrichmentLease : IAsyncDisposable
{
    internal static readonly EmptyItemEnrichmentLease Instance = new();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
