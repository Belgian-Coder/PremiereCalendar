using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed partial class PremiereService
{
    private async Task<PremiereItem?> MapMovieCoreAsync(TmdbMovieDiscoverItem item,
        CancellationToken cancellationToken, bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem>? cachedEnrichment = null,
        bool allowWatchmodeAvailabilityFallback = true, MediaDiscoveryCriteria? requestedMovieCriteria = null)
    {
        if (_itemCacheSession.Value is not { } session)
            return await MapMovieUncachedCoreAsync(item, cancellationToken, forceRefresh, cachedEnrichment,
                allowWatchmodeAvailabilityFallback, requestedMovieCriteria);
        var discovered = MapMovieMetadata(item);
        if (discovered is null) return null;
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, item.Id);
        var outcome = await session.GetAsync(identity, forceRefresh, async (previous, metadataDue, ratingsDue, token) =>
        {
            var attempt = new ItemEnrichmentAttempt(metadataDue, ratingsDue, previous.Ratings, previous.Metadata);
            var oldAttempt = _itemEnrichmentAttempt.Value;
            _itemEnrichmentAttempt.Value = attempt;
            try
            {
                if (metadataDue)
                {
                    // Every producer of the shared entry checks the same availability providers.
                    var mapped = await MapMovieUncachedCoreAsync(item, token, false, null, allowWatchmodeAvailabilityFallback: true);
                    var metadata = mapped is not null && attempt.MetadataSucceeded
                        ? attempt.MetadataFailed && previous.Metadata is not null ? previous.Metadata
                            : ItemEnrichmentMetadata.FromItem(mapped) with { AvailabilityComplete = !attempt.MetadataFailed } : null;
                    return new(metadata, mapped is not null && attempt.RatingsSucceeded && !attempt.RatingsFailed ? ItemEnrichmentRatings.FromItem(mapped) : null,
                        RefreshMetadataClock: !attempt.MetadataFailed, MetadataFailed: attempt.MetadataFailed, RatingsFailed: attempt.RatingsFailed);
                }
                var availability = await CompleteCachedAvailabilityAsync(previous, discovered, token);
                var ratingsItem = await RefreshItemRatingsAsync(previous.Apply(discovered), token);
                return new(availability, attempt.RatingsSucceeded && !attempt.RatingsFailed ? ItemEnrichmentRatings.FromItem(ratingsItem) : null,
                    RefreshMetadataClock: false, MetadataFailed: attempt.MetadataFailed, RatingsFailed: attempt.RatingsFailed);
            }
            finally { _itemEnrichmentAttempt.Value = oldAttempt; }
        }, cancellationToken, ratingsInputs: discovered);
        var result = outcome.Apply(discovered);
        return MovieMatchesKnownCriteria(result, requestedMovieCriteria) ? result : null;
    }

    private async Task<PremiereItem?> MapSeriesCoreAsync(TmdbTvDiscoverItem item,
        CancellationToken cancellationToken, bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem>? cachedEnrichment = null,
        DateOnly? premiereDateOverride = null, PremiereItemType? itemTypeOverride = null,
        string? episodeTitle = null, int? seasonNumber = null, int? episodeNumber = null,
        string? episodeSource = null, bool allowWatchmodeAvailabilityFallback = true,
        DateOnly? requestedStart = null, DateOnly? requestedEnd = null, bool canonicalizeSeriesPremiereDate = false)
    {
        if (_itemCacheSession.Value is not { } session)
            return await MapSeriesUncachedCoreAsync(item, cancellationToken, forceRefresh, cachedEnrichment,
                premiereDateOverride, itemTypeOverride, episodeTitle, seasonNumber, episodeNumber, episodeSource,
                allowWatchmodeAvailabilityFallback, requestedStart, requestedEnd, canonicalizeSeriesPremiereDate);
        if (canonicalizeSeriesPremiereDate)
        {
            var canonicalDate = await GetSeasonOneEpisodeOneDateAsync(item.Id, cancellationToken, forceRefresh);
            premiereDateOverride = canonicalDate ?? premiereDateOverride;
        }
        var discovered = MapSeriesMetadata(item, premiereDateOverride, itemTypeOverride, episodeTitle, seasonNumber, episodeNumber, episodeSource);
        if (discovered is null || requestedStart is { } start && discovered.PremiereDate < start
            || requestedEnd is { } end && discovered.PremiereDate > end) return null;
        discovered = discovered with
        {
            DateSemantics = BuildSeriesDateSemantics(discovered.Type, discovered.PremiereDate, premiereDateOverride,
                canonicalizeSeriesPremiereDate, episodeSource)
        };
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, item.Id);
        var outcome = await session.GetAsync(identity, forceRefresh, async (previous, metadataDue, ratingsDue, token) =>
        {
            var attempt = new ItemEnrichmentAttempt(metadataDue, ratingsDue, previous.Ratings, previous.Metadata);
            var oldAttempt = _itemEnrichmentAttempt.Value;
            _itemEnrichmentAttempt.Value = attempt;
            try
            {
                if (metadataDue)
                {
                    var mapped = await MapSeriesUncachedCoreAsync(item, token, false, null, discovered.PremiereDate,
                        discovered.Type, episodeTitle, seasonNumber, episodeNumber, episodeSource, allowWatchmodeAvailabilityFallback: true);
                    ItemEnrichmentMetadata? metadata = null;
                    if (mapped is not null && attempt.MetadataSucceeded)
                    {
                        metadata = attempt.MetadataFailed && previous.Metadata is not null ? previous.Metadata
                            : ItemEnrichmentMetadata.FromItem(mapped) with { TvmazeShowId = attempt.TvmazeShowId, AvailabilityComplete = !attempt.MetadataFailed };
                        if (!attempt.MetadataFailed && session.FirstEpisodeDates.TryGetValue(item.Id, out var validationTask))
                        {
                            var validation = await validationTask.Value;
                            metadata = metadata with { FirstEpisodeDateChecked = validation.Successful, FirstEpisodeDate = validation.Date };
                        }
                        else if (!attempt.MetadataFailed && previous.Metadata is { FirstEpisodeDateChecked: true } oldMetadata
                            && previous.Record.MetadataInvalidatedUtc is null)
                            metadata = metadata with { FirstEpisodeDateChecked = true, FirstEpisodeDate = oldMetadata.FirstEpisodeDate };
                    }
                    return new(metadata, mapped is not null && attempt.RatingsSucceeded && !attempt.RatingsFailed
                        ? ItemEnrichmentRatings.FromItem(mapped) : null,
                        RefreshMetadataClock: !attempt.MetadataFailed, MetadataFailed: attempt.MetadataFailed, RatingsFailed: attempt.RatingsFailed);
                }
                var availability = await CompleteCachedAvailabilityAsync(previous, discovered, token);
                var ratingsItem = await RefreshItemRatingsAsync(previous.Apply(discovered), token);
                return new(availability, attempt.RatingsSucceeded && !attempt.RatingsFailed ? ItemEnrichmentRatings.FromItem(ratingsItem) : null,
                    RefreshMetadataClock: false, MetadataFailed: attempt.MetadataFailed, RatingsFailed: attempt.RatingsFailed);
            }
            finally { _itemEnrichmentAttempt.Value = oldAttempt; }
        }, cancellationToken, ratingsInputs: discovered);
        return outcome.Apply(discovered);
    }

    private async Task<PremiereItem> RefreshItemRatingsAsync(PremiereItem item, CancellationToken cancellationToken)
    {
        var ratingsTask = GetExternalRatingsAsync(item.ImdbId, cancellationToken, false);
        var tomatoesTask = GetRottenTomatoesScoresAsync(item.MediaType, item.Title, item.PremiereDate.Year,
            item.WikidataId, cancellationToken, false);
        try { await Task.WhenAll(ratingsTask, tomatoesTask); }
        catch { await ObserveStartedEnrichmentTasksAsync([ratingsTask, tomatoesTask]); throw; }
        var ratings = await ratingsTask;
        var tomatoes = await tomatoesTask;
        // This path changes scores only. It never requests metadata or artwork.
        return item with
        {
            ImdbScore = ratings.ImdbScore ?? item.ImdbScore, ImdbVoteCount = ratings.ImdbVoteCount ?? item.ImdbVoteCount,
            RottenTomatoesScore = ratings.RottenTomatoesScore ?? tomatoes.CriticScore ?? item.RottenTomatoesScore,
            RottenTomatoesAudienceScore = ratings.RottenTomatoesAudienceScore ?? tomatoes.AudienceScore ?? item.RottenTomatoesAudienceScore,
            MetacriticScore = ratings.MetacriticScore ?? item.MetacriticScore
        };
    }

    private async Task<ItemEnrichmentMetadata?> CompleteCachedAvailabilityAsync(ItemEnrichmentSnapshot previous,
        PremiereItem discovered, CancellationToken cancellationToken)
    {
        if (previous.Metadata is not { AvailabilityComplete: false } metadata) return null;
        var sources = await SourceEntriesWithWatchmodeFallbackAsync(metadata.Sources, discovered.MediaType,
            discovered.TmdbId, metadata.ImdbId, cancellationToken, false);
        return _itemEnrichmentAttempt.Value?.MetadataFailed == true ? null : metadata with { Sources = sources, AvailabilityComplete = true };
    }

    private static bool MovieMatchesKnownCriteria(PremiereItem item, MediaDiscoveryCriteria? criteria)
    {
        if (criteria is null) return true;
        if (criteria.OriginalLanguages.Length > 0 && !string.IsNullOrWhiteSpace(item.OriginalLanguage)
            && !criteria.OriginalLanguages.Contains(item.OriginalLanguage, StringComparer.OrdinalIgnoreCase)) return false;
        return item.RuntimeMinutes is not > 0 || (item.RuntimeMinutes >= criteria.RuntimeMinMinutes
            && (criteria.RuntimeMaxMinutes >= 360 || item.RuntimeMinutes <= criteria.RuntimeMaxMinutes));
    }

    private async Task<bool> NeedsRemoteCachedHydrationAsync(IReadOnlyList<PremiereItem> items,
        CalendarFilters? filters, CancellationToken cancellationToken)
    {
        if (_itemEnrichmentCache is null) return NeedsRemoteCachedHydration(items, filters);
        var session = _itemCacheSession.Value ?? await _itemEnrichmentCache.CreateSessionAsync(cancellationToken);
        var identities = items.Where(item => item.TmdbId > 0 && item.VerificationState == PremiereVerificationState.Verified)
            .Select(item => new ItemEnrichmentIdentity(item.MediaType, item.TmdbId)).Distinct().ToArray();
        await session.PrepareAsync(identities, items, cancellationToken);
        await session.FlushAsync(cancellationToken, rejectGenerationConflicts: false);
        return identities.Any(identity => session.MetadataRefreshNeeded(identity) || session.RatingsRefreshNeeded(identity));
    }

    private async Task<IReadOnlyList<PremiereItem>> HydrateSharedItemRatingsAsync(IReadOnlyList<PremiereItem> items,
        CancellationToken cancellationToken, bool allowRemote)
    {
        if (items.Count == 0 || _itemEnrichmentCache is null) return items;
        var session = _itemCacheSession.Value ?? await _itemEnrichmentCache.CreateSessionAsync(cancellationToken);
        await session.PrepareAsync(items.Select(item => new ItemEnrichmentIdentity(item.MediaType, item.TmdbId)), items, cancellationToken);
        using var gate = new SemaphoreSlim(Math.Clamp(_options.MaxEnrichmentConcurrency, 1, 32));
        var hydrated = await Task.WhenAll(items.Select(async item =>
        {
            var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
            if (item.TmdbId <= 0 || item.VerificationState != PremiereVerificationState.Verified) return item;
            if (!allowRemote) return session.Find(identity) is { } local ? local.Rebase(item, metadataChanged: false) : item;
            await gate.WaitAsync(cancellationToken);
            try
            {
                var outcome = await session.GetAsync(identity, false, async (previous, _, ratingsDue, token) =>
                {
                    var attempt = new ItemEnrichmentAttempt(false, ratingsDue, previous.Ratings, previous.Metadata);
                    var oldAttempt = _itemEnrichmentAttempt.Value;
                    _itemEnrichmentAttempt.Value = attempt;
                    try
                    {
                        var ratingsItem = await RefreshItemRatingsAsync(previous.Apply(item), token);
                        return new ItemEnrichmentRefresh(null, attempt.RatingsSucceeded && !attempt.RatingsFailed
                            ? ItemEnrichmentRatings.FromItem(ratingsItem) : null, RatingsFailed: attempt.RatingsFailed);
                    }
                    finally { _itemEnrichmentAttempt.Value = oldAttempt; }
                }, cancellationToken, ratingsOnly: true, ratingsInputs: item);
                return outcome.Rebase(item, metadataChanged: false);
            }
            finally { gate.Release(); }
        }));
        await session.FlushAsync(cancellationToken);
        return hydrated;
    }

    private async Task<(IReadOnlyList<PremiereItem> Items, bool DiscoveryChanged)> HydrateSharedItemMetadataAsync(
        IReadOnlyList<PremiereItem> items, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        if (_itemCacheSession.Value is not { } session || items.Count == 0) return (items, false);
        using var gate = new SemaphoreSlim(Math.Clamp(_options.MaxEnrichmentConcurrency, 1, 32));
        var changed = 0;
        var hydrated = await Task.WhenAll(items.Select(async item =>
        {
            var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
            if (item.TmdbId <= 0 || item.VerificationState != PremiereVerificationState.Verified || !session.MetadataRefreshNeeded(identity)) return item;
            await gate.WaitAsync(cancellationToken);
            try
            {
                var date = item.PremiereDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var mapped = item.MediaType == PremiereMediaType.Movie
                    ? await MapMovieCoreAsync(new TmdbMovieDiscoverItem { Id = item.TmdbId, Title = item.Title, ReleaseDate = date }, cancellationToken, false)
                    : await MapSeriesCoreAsync(new TmdbTvDiscoverItem { Id = item.TmdbId, Name = item.Title, FirstAirDate = date },
                        cancellationToken, false, premiereDateOverride: item.PremiereDate, itemTypeOverride: item.Type,
                        episodeTitle: item.EpisodeTitle, seasonNumber: item.SeasonNumber, episodeNumber: item.EpisodeNumber, episodeSource: item.EpisodeSource,
                        requestedStart: start, requestedEnd: end, canonicalizeSeriesPremiereDate: item.Type == PremiereItemType.SeriesPremiere);
                if (mapped is null || mapped.PremiereDate != item.PremiereDate)
                {
                    Interlocked.Exchange(ref changed, 1);
                    return item;
                }
                var sources = mapped.Sources.Concat(item.Sources.Where(source => source.Kind.Equals("schedule", StringComparison.OrdinalIgnoreCase)))
                    .DistinctBy(source => $"{source.Kind}:{source.Id}:{source.Name}", StringComparer.OrdinalIgnoreCase).ToArray();
                return mapped with
                {
                    CanonicalId = item.CanonicalId, Type = item.Type, PremiereDate = item.PremiereDate, DateSemantics = item.DateSemantics,
                    EpisodeTitle = item.EpisodeTitle, SeasonNumber = item.SeasonNumber, EpisodeNumber = item.EpisodeNumber, EpisodeSource = item.EpisodeSource,
                    VerificationState = item.VerificationState, VerificationNote = item.VerificationNote,
                    ExternalProviderId = item.ExternalProviderId, ExternalCandidateKey = item.ExternalCandidateKey, ExternalUrl = item.ExternalUrl,
                    MergeContributions = item.MergeContributions, Sources = sources, SourceNames = SourceNames(sources)
                };
            }
            finally { gate.Release(); }
        }));
        await session.FlushAsync(cancellationToken);
        var discoveryChanged = changed != 0;
        session.ForceDiscoveryRefresh |= discoveryChanged;
        return (hydrated, discoveryChanged);
    }

    private Task PrepareItemEnrichmentAsync(PremiereMediaType mediaType, IEnumerable<int> ids,
        IReadOnlyDictionary<string, PremiereItem>? cachedEnrichment, CancellationToken cancellationToken)
        => _itemCacheSession.Value?.PrepareAsync(ids.Select(id => new ItemEnrichmentIdentity(mediaType, id)),
            cachedEnrichment?.Values, cancellationToken) ?? Task.CompletedTask;

    private bool ForceDiscoveryRefresh(bool requested) => requested || _itemCacheSession.Value?.ForceDiscoveryRefresh == true;

    private async Task PrepareExternalItemEnrichmentAsync(IReadOnlyList<ExternalPremiereCandidate> candidates,
        bool forceRefresh, IReadOnlyDictionary<string, PremiereItem> cachedEnrichment, CancellationToken cancellationToken)
    {
        if (_itemCacheSession.Value is not { } session) return;
        // Reserve supplied IDs before debut-date validation. Resolve the remaining
        // candidates together, then reserve that cohort before detail enrichment.
        await session.PrepareAsync(candidates.Where(candidate => candidate.TmdbId is > 0)
            .Select(candidate => new ItemEnrichmentIdentity(candidate.MediaType, candidate.TmdbId!.Value)),
            cachedEnrichment.Values, cancellationToken);
        using var gate = new SemaphoreSlim(Math.Clamp(_options.MaxEnrichmentConcurrency, 1, 32));
        var identities = await Task.WhenAll(candidates.Select(async candidate =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var id = await ResolveCandidateTmdbIdAsync(candidate, cancellationToken, forceRefresh);
                return id is > 0 ? new ItemEnrichmentIdentity(candidate.MediaType, id.Value) : null;
            }
            finally { gate.Release(); }
        }));
        await session.PrepareAsync(identities.Where(identity => identity is not null).Select(identity => identity!),
            cachedEnrichment.Values, cancellationToken);
    }

    private IDisposable UseItemCacheSession(ItemEnrichmentCacheCoordinator.ItemEnrichmentSession? session)
    {
        var previous = _itemCacheSession.Value;
        _itemCacheSession.Value = session;
        return new ItemCacheScope(() => _itemCacheSession.Value = previous);
    }

    private sealed class ItemCacheScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private sealed class ItemEnrichmentAttempt(bool refreshMetadata, bool refreshRatings, ItemEnrichmentRatings? previousRatings,
        ItemEnrichmentMetadata? previousMetadata = null)
    {
        public bool ForceMetadata { get; } = refreshMetadata;
        public bool ForceRatings => RefreshRatings;
        public bool RefreshRatings { get; set; } = refreshRatings;
        public ItemEnrichmentRatings? PreviousRatings { get; } = previousRatings;
        public ItemEnrichmentMetadata? PreviousMetadata { get; } = previousMetadata;
        public bool MetadataSucceeded;
        public bool MetadataFailed;
        public bool RatingsSucceeded;
        public bool RatingsFailed;
        public int? TvmazeShowId;
    }
}
