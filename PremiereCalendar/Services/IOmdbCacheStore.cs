using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed record OmdbCacheEntry(
    string ImdbId,
    OmdbItem Item,
    DateTimeOffset CachedAtUtc);

public sealed record OmdbProviderCacheState(
    DateTimeOffset? RateLimitedUntilUtc,
    string? LastError,
    DateTimeOffset? LastFailureUtc);

public interface IOmdbCacheStore
{
    Task<OmdbCacheEntry?> GetAsync(string imdbId, CancellationToken cancellationToken);

    Task SetAsync(string imdbId, OmdbItem item, DateTimeOffset cachedAtUtc, CancellationToken cancellationToken);

    Task<OmdbProviderCacheState> GetProviderStateAsync(CancellationToken cancellationToken);

    Task MarkRateLimitedAsync(DateTimeOffset untilUtc, string error, CancellationToken cancellationToken);

    Task MarkFailureAsync(string error, CancellationToken cancellationToken);
}
