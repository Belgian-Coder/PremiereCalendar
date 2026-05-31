using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class SourceHealthService
{
    private readonly IProviderCacheStateStore _providerCacheStateStore;
    private readonly BackgroundJobTimelineService _timeline;
    private readonly IOmdbCacheStore? _omdbCacheStore;
    private readonly IImdbRatingsStore? _imdbRatingsStore;
    private readonly TimeProvider _timeProvider;

    public SourceHealthService(
        IProviderCacheStateStore providerCacheStateStore,
        BackgroundJobTimelineService timeline,
        IOmdbCacheStore? omdbCacheStore,
        IImdbRatingsStore? imdbRatingsStore,
        TimeProvider timeProvider)
    {
        _providerCacheStateStore = providerCacheStateStore;
        _timeline = timeline;
        _omdbCacheStore = omdbCacheStore;
        _imdbRatingsStore = imdbRatingsStore;
        _timeProvider = timeProvider;
    }

    public async Task<SourceHealthOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var providerStates = await _providerCacheStateStore.GetRecentAsync(50, cancellationToken);
        var providers = providerStates
            .Select(state => new SourceHealthProviderState(
                state.Provider,
                state.Scope,
                state.Key,
                state.LastCheckedUtc,
                state.LastChangedUtc,
                state.Watermark,
                state.ItemCount,
                state.MetadataJson))
            .ToArray();

        var omdb = _omdbCacheStore is null
            ? null
            : await _omdbCacheStore.GetProviderStateAsync(cancellationToken);
        var imdb = _imdbRatingsStore is null
            ? null
            : await _imdbRatingsStore.GetStateAsync(cancellationToken);
        var jobs = await _timeline.GetRecentAsync(cancellationToken);

        _ = _timeProvider.GetUtcNow();
        return new SourceHealthOverview(providers, omdb, imdb, jobs.Take(10).ToArray());
    }
}
