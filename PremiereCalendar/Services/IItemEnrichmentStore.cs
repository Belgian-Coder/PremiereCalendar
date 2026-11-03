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
    string? ExpectedRatingsJson = null);

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

    // A first lookup has no show ID to match against a TVmaze update feed yet.
    // Fence unmapped rows before targeted updates without making saved metadata stale.
    Task<int> FenceUnmappedTvmazeReservationsAsync(DateTimeOffset epochUtc, CancellationToken cancellationToken) =>
        Task.FromResult(0);

    Task<int> CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}
