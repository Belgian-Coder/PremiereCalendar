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
            var attempt = new ItemEnrichmentAttempt(metadataDue, ratingsDue, previous.Ratings, previous.Metadata,
                previous.Record.RatingsFetchedUtc, session.Now, session.RatingsCutoffUtc, forceRefresh,
                forceRefresh || previous.Record.MetadataInvalidatedUtc is { } invalidated
                    && (previous.Record.MetadataFetchedUtc is null || invalidated > previous.Record.MetadataFetchedUtc), previous.Record.MetadataFetchedUtc,
                session.RatingsRefreshAllowed(identity), _imdbRatingsStore is not null)
                { ItemId = item.Id, MediaType = PremiereMediaType.Movie };
            var oldAttempt = _itemEnrichmentAttempt.Value;
            _itemEnrichmentAttempt.Value = attempt;
            try
            {
                if (metadataDue)
                {
                    // Every producer of the shared entry checks the same availability providers.
                    var mapped = await MapMovieUncachedCoreAsync(item, token, attempt.ForceMetadata, null, allowWatchmodeAvailabilityFallback: true);
                    var metadata = mapped is not null && attempt.MetadataSucceeded
                        ? attempt.CaptureMetadata(mapped) with { AvailabilityComplete = !attempt.AvailabilityFailed,
                            OptionalMetadataComplete = !attempt.OptionalMetadataFailed, ArtworkComplete = !attempt.ArtworkFailed } : null;
                    return new(metadata, mapped is null ? null : attempt.CaptureRatings(mapped),
                        RefreshMetadataClock: attempt.MetadataSucceeded, MetadataFailed: !attempt.MetadataSucceeded, RatingsFailed: attempt.RatingsFailed,
                        AvailabilityFailed: attempt.AvailabilityFailed, OptionalMetadataFailed: attempt.OptionalMetadataFailed, RefreshRatingsClock: attempt.RatingsSucceeded);
                }
                var availability = await CompleteCachedOptionalMetadataAsync(previous, discovered, token,
                    session.AvailabilityRefreshNeeded(identity), session.OptionalMetadataRefreshNeeded(identity));
                var ratingsItem = await RefreshItemRatingsAsync(previous.Apply(discovered), token);
                return new(availability, attempt.CaptureRatings(ratingsItem),
                    RefreshMetadataClock: false, RatingsFailed: attempt.RatingsFailed,
                    AvailabilityFailed: attempt.AvailabilityFailed, OptionalMetadataFailed: attempt.OptionalMetadataFailed, RefreshRatingsClock: attempt.RatingsSucceeded);
            }
            finally { _itemEnrichmentAttempt.Value = oldAttempt; }
        }, cancellationToken, ratingsInputs: discovered, requireDataset: _imdbRatingsStore is not null);
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
            var attempt = new ItemEnrichmentAttempt(metadataDue, ratingsDue, previous.Ratings, previous.Metadata,
                previous.Record.RatingsFetchedUtc, session.Now, session.RatingsCutoffUtc, forceRefresh,
                forceRefresh || previous.Record.MetadataInvalidatedUtc is { } invalidated
                    && (previous.Record.MetadataFetchedUtc is null || invalidated > previous.Record.MetadataFetchedUtc), previous.Record.MetadataFetchedUtc,
                session.RatingsRefreshAllowed(identity), _imdbRatingsStore is not null)
                { ItemId = item.Id, MediaType = PremiereMediaType.Series };
            var oldAttempt = _itemEnrichmentAttempt.Value;
            _itemEnrichmentAttempt.Value = attempt;
            try
            {
                if (metadataDue)
                {
                    var mapped = await MapSeriesUncachedCoreAsync(item, token, attempt.ForceMetadata, null, discovered.PremiereDate,
                        discovered.Type, episodeTitle, seasonNumber, episodeNumber, episodeSource, allowWatchmodeAvailabilityFallback: true);
                    ItemEnrichmentMetadata? metadata = null;
                    if (mapped is not null && attempt.MetadataSucceeded)
                    {
                        metadata = attempt.CaptureMetadata(mapped) with { TvmazeShowId = attempt.TvmazeShowId ?? previous.Metadata?.TvmazeShowId,
                            AvailabilityComplete = !attempt.AvailabilityFailed, OptionalMetadataComplete = !attempt.OptionalMetadataFailed,
                            TvmazeComplete = !attempt.TvmazeFailed, ArtworkComplete = !attempt.ArtworkFailed,
                            RuntimeFromTvmaze = attempt.RuntimeFromTvmaze };
                        var canonicalInvalidationPending = previous.Record.MetadataInvalidatedUtc is { } canonicalInvalidated
                            && (previous.Record.MetadataFetchedUtc is null || previous.Record.MetadataFetchedUtc < canonicalInvalidated);
                        if (session.FirstEpisodeDates.TryGetValue((item.Id, previous.Record.Generation), out var validationTask))
                        {
                            var validation = await validationTask.Value.WaitAsync(token);
                            if (validation.Successful)
                                metadata = metadata with { FirstEpisodeDateChecked = true, FirstEpisodeDate = validation.Date };
                            else if (!canonicalInvalidationPending && previous.Metadata is { FirstEpisodeDateChecked: true } priorCanonical)
                                metadata = metadata with { FirstEpisodeDateChecked = true, FirstEpisodeDate = priorCanonical.FirstEpisodeDate };
                        }
                        else if (previous.Metadata is { FirstEpisodeDateChecked: true } oldMetadata
                            && !canonicalInvalidationPending)
                            metadata = metadata with { FirstEpisodeDateChecked = true, FirstEpisodeDate = oldMetadata.FirstEpisodeDate };
                    }
                    return new(metadata, mapped is null ? null : attempt.CaptureRatings(mapped),
                        RefreshMetadataClock: attempt.MetadataSucceeded, MetadataFailed: !attempt.MetadataSucceeded, RatingsFailed: attempt.RatingsFailed,
                        AvailabilityFailed: attempt.AvailabilityFailed, OptionalMetadataFailed: attempt.OptionalMetadataFailed, RefreshRatingsClock: attempt.RatingsSucceeded);
                }
                var availability = await CompleteCachedOptionalMetadataAsync(previous, discovered, token,
                    session.AvailabilityRefreshNeeded(identity), session.OptionalMetadataRefreshNeeded(identity));
                var ratingsItem = await RefreshItemRatingsAsync(previous.Apply(discovered), token);
                return new(availability, attempt.CaptureRatings(ratingsItem),
                    RefreshMetadataClock: false, RatingsFailed: attempt.RatingsFailed,
                    AvailabilityFailed: attempt.AvailabilityFailed, OptionalMetadataFailed: attempt.OptionalMetadataFailed, RefreshRatingsClock: attempt.RatingsSucceeded);
            }
            finally { _itemEnrichmentAttempt.Value = oldAttempt; }
        }, cancellationToken, ratingsInputs: discovered, requireDataset: _imdbRatingsStore is not null);
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
            ImdbScore = ratings.ImdbScore, ImdbVoteCount = ratings.ImdbVoteCount,
            RottenTomatoesScore = ratings.RottenTomatoesScore ?? tomatoes.CriticScore,
            RottenTomatoesAudienceScore = ratings.RottenTomatoesAudienceScore ?? tomatoes.AudienceScore,
            MetacriticScore = ratings.MetacriticScore
        };
    }

    private async Task<ItemEnrichmentMetadata?> CompleteCachedOptionalMetadataAsync(ItemEnrichmentSnapshot previous,
        PremiereItem discovered, CancellationToken cancellationToken, bool refreshAvailability, bool refreshOptionalMetadata)
    {
        if ((!refreshAvailability && !refreshOptionalMetadata) || previous.Metadata is not { } metadata || _itemEnrichmentAttempt.Value is not { } attempt) return null;
        string? fetchedTvmazePoster = null;
        var updated = metadata.OptionalFreshnessTracked ? metadata : metadata with
        {
            OptionalFreshnessTracked = true, AvailabilityFetchedUtc = previous.Record.MetadataFetchedUtc,
            TvmazeFetchedUtc = discovered.MediaType == PremiereMediaType.Series ? previous.Record.MetadataFetchedUtc : null,
            ArtworkFetchedUtc = previous.Record.MetadataFetchedUtc
        };
        if (refreshAvailability && !metadata.AvailabilityComplete)
        {
            var availabilityStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var baseSources = metadata.AvailabilityOwnedByWatchmode
                ? metadata.Sources.Where(source => !HasWatchProviderSource([source])).ToArray() : metadata.Sources;
            PremiereSource[] sources;
            if (!metadata.AvailabilityOwnedByWatchmode && HasWatchProviderSource(baseSources))
            {
                sources = baseSources;
                attempt.AvailabilitySkipped = true;
                RecordItemComponent("cache", "availability", "reused", availabilityStarted, "legacy_provenance",
                    itemId: discovered.TmdbId, mediaType: discovered.MediaType);
            }
            else sources = await SourceEntriesWithWatchmodeFallbackAsync(baseSources, discovered.MediaType,
                discovered.TmdbId, metadata.ImdbId, cancellationToken, true);
            updated = updated with { Sources = sources, AvailabilityComplete = !attempt.AvailabilityFailed,
                AvailabilityOwnedByWatchmode = !attempt.AvailabilityFailed && !attempt.AvailabilitySkipped
                    ? attempt.AvailabilityOwnedByWatchmode ?? metadata.AvailabilityOwnedByWatchmode : metadata.AvailabilityOwnedByWatchmode,
                AvailabilityFetchedUtc = !attempt.AvailabilityFailed && !attempt.AvailabilitySkipped ? attempt.CurrentUtc : updated.AvailabilityFetchedUtc };
        }
        if (refreshOptionalMetadata && !metadata.TvmazeComplete && discovered.MediaType == PremiereMediaType.Series)
        {
            var tvmaze = await GetTvSeriesEnrichmentAsync(new TmdbExternalIds { ImdbId = metadata.ImdbId, TvdbId = metadata.TvdbId },
                discovered.Title, cancellationToken, true);
            if (!attempt.TvmazeFailed)
            {
                if (!attempt.TvmazeSkipped) fetchedTvmazePoster = tvmaze.ImageUrl;
                var sources = updated.Sources.Where(source => !(source.Id is null && source.Kind.Equals("network", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(source.Name, metadata.NetworkName, StringComparison.OrdinalIgnoreCase))
                    && !(source.Id is null && source.Kind.Equals("web", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(source.Name, metadata.WebChannelName, StringComparison.OrdinalIgnoreCase))).ToList();
                AddSource(sources, tvmaze.NetworkName, null, "network");
                AddSource(sources, tvmaze.WebChannelName, null, "web");
                updated = updated with { NetworkName = tvmaze.NetworkName, WebChannelName = tvmaze.WebChannelName,
                    TvmazeAverageRuntimeMinutes = tvmaze.AverageRuntimeMinutes, TvmazeRating = tvmaze.TvmazeRating,
                    OfficialSiteUrl = tvmaze.OfficialSiteUrl, TvmazeUrl = tvmaze.TvmazeUrl,
                    TvmazeShowId = attempt.TvmazeSkipped ? updated.TvmazeShowId : attempt.TvmazeShowId,
                    Sources = sources.ToArray(), TvmazeComplete = true,
                    TvmazeFetchedUtc = attempt.TvmazeSkipped ? updated.TvmazeFetchedUtc : attempt.CurrentUtc,
                    RuntimeMinutes = metadata.RuntimeFromTvmaze ? tvmaze.AverageRuntimeMinutes : metadata.RuntimeMinutes };
                if (attempt.TvmazeSucceeded && (metadata.ImageSource == ArtworkSources.TvmazeImage || metadata.PosterUrl is null))
                {
                    if (attempt.TvmazeSucceeded && tvmaze.ImageUrl is not null) attempt.PosterOwnerVerifiedEmpty = false;
                    updated = updated with { PosterUrl = tvmaze.ImageUrl, ImageSource = tvmaze.ImageUrl is null ? null : ArtworkSources.TvmazeImage,
                        PosterOwnerVerifiedEmpty = tvmaze.ImageUrl is not null && attempt.PosterOwnerVerifiedEmpty,
                        ArtworkFetchedUtc = tvmaze.ImageUrl is not null ? attempt.CurrentUtc : updated.ArtworkFetchedUtc,
                        ArtworkComplete = metadata.ImageSource == ArtworkSources.TvmazeImage && tvmaze.ImageUrl is null && !attempt.TvmazeSkipped
                            ? false : updated.ArtworkComplete };
                }
            }
        }
        if (refreshOptionalMetadata && !updated.ArtworkComplete)
        {
            // A retained fallback is not a newly verified known cover. Retry the
            // failed provider unless this attempt just recovered an owning source.
            var artwork = await ResolveArtworkAsync(null,
                attempt.ExternalSucceeded ? attempt.ExternalProviderResult?.PosterUrl : null,
                fetchedTvmazePoster, updated.BackdropUrl,
                new ArtworkRequest(discovered.MediaType, discovered.TmdbId, metadata.ImdbId, metadata.TvdbId, metadata.WikidataId, discovered.Title),
                cancellationToken, true);
            if (artwork is not null || !attempt.ArtworkFailed)
                updated = updated with { PosterUrl = artwork?.Url, ImageSource = artwork?.Source, ArtworkComplete = !attempt.ArtworkFailed,
                    PosterOwnerVerifiedEmpty = artwork is not null && attempt.PosterOwnerVerifiedEmpty,
                    ArtworkFetchedUtc = !attempt.ArtworkFailed && !attempt.ArtworkSkipped && !attempt.ArtworkReused
                        ? attempt.CurrentUtc : updated.ArtworkFetchedUtc };
        }
        updated = updated with { OptionalMetadataComplete = updated.TvmazeComplete && updated.ArtworkComplete };
        return updated == metadata ? null : updated;
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
        return items.Where(item => item.TmdbId > 0 && item.VerificationState == PremiereVerificationState.Verified)
            .Any(item =>
            {
                var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
                return session.MetadataRefreshNeeded(identity) || session.RatingsRefreshNeeded(identity, item, _imdbRatingsStore is not null);
            });
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
                    var attempt = new ItemEnrichmentAttempt(false, ratingsDue, previous.Ratings, previous.Metadata,
                        previous.Record.RatingsFetchedUtc, session.Now, session.RatingsCutoffUtc,
                        previousMetadataFetchedUtc: previous.Record.MetadataFetchedUtc,
                        ratingsRetryAllowed: session.RatingsRefreshAllowed(identity), datasetEnabled: _imdbRatingsStore is not null)
                        { ItemId = item.TmdbId, MediaType = item.MediaType };
                    var oldAttempt = _itemEnrichmentAttempt.Value;
                    _itemEnrichmentAttempt.Value = attempt;
                    try
                    {
                        var ratingsItem = await RefreshItemRatingsAsync(previous.Apply(item), token);
                        return new ItemEnrichmentRefresh(null, attempt.CaptureRatings(ratingsItem), RatingsFailed: attempt.RatingsFailed, RefreshRatingsClock: attempt.RatingsSucceeded);
                    }
                    finally { _itemEnrichmentAttempt.Value = oldAttempt; }
                }, cancellationToken, ratingsOnly: true, ratingsInputs: item, requireDataset: _imdbRatingsStore is not null);
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
        ItemEnrichmentMetadata? previousMetadata = null, DateTimeOffset? previousRatingsFetchedUtc = null,
        DateTimeOffset? now = null, DateTimeOffset? ratingsCutoffUtc = null, bool explicitForceRefresh = false, bool forceIdentifierRefresh = false,
        DateTimeOffset? previousMetadataFetchedUtc = null, bool ratingsRetryAllowed = true, bool datasetEnabled = false)
    {
        public bool ForceMetadata { get; } = refreshMetadata;
        public bool ForceIdentifierRefresh { get; } = forceIdentifierRefresh;
        public DateTimeOffset CurrentUtc { get; } = now ?? DateTimeOffset.UtcNow;
        public bool RefreshRatings { get; } = refreshRatings;
        public bool RatingsRetryAllowed { get; } = explicitForceRefresh || ratingsRetryAllowed;
        public ItemEnrichmentRatings? PreviousRatings { get; } = previousRatings;
        public ItemEnrichmentMetadata? PreviousMetadata { get; } = previousMetadata;
        public bool MetadataSucceeded;
        public bool MetadataFailed;
        public bool AvailabilityFailed;
        public bool OptionalMetadataFailed;
        public bool TvmazeFailed;
        public bool TvmazeSucceeded;
        public bool PosterOwnerVerifiedEmpty = previousMetadata?.PosterOwnerVerifiedEmpty == true;
        public bool ArtworkFailed;
        public bool ArtworkSkipped;
        public bool ArtworkReused;
        public bool BackdropReused;
        public bool AvailabilitySkipped;
        public bool? AvailabilityOwnedByWatchmode;
        public bool TvmazeSkipped;
        public bool RuntimeFromTvmaze;
        public bool RatingsFailed;
        public bool ExternalSucceeded;
        public bool ExternalReused;
        public bool DatasetSucceeded;
        public bool DatasetRequired = datasetEnabled;
        public ItemEnrichmentDatasetRating? DatasetResult;
        public bool TomatoesSucceeded;
        public bool TomatoesReused;
        public bool RatingsSucceeded => ExternalSucceeded || TomatoesSucceeded || DatasetSucceeded;
        public bool TomatoesNotRequired = previousRatings?.TomatoesRequired == false;
        public bool ExternalNotRequired = previousRatings?.ExternalRequired == false;
        public int ItemId;
        public PremiereMediaType? MediaType;
        public ExternalRatings? ExternalResult;
        public ExternalRatings? ExternalProviderResult;
        public RottenTomatoesScores? TomatoesResult;
        public int? TvmazeShowId;

        private DateTimeOffset? PreviousMetadataClock(DateTimeOffset? component)
            => PreviousMetadata?.OptionalFreshnessTracked == true ? component : previousMetadataFetchedUtc;

        public ItemEnrichmentMetadata CaptureMetadata(PremiereItem item) => ItemEnrichmentMetadata.FromItem(item) with
        {
            PosterOwnerVerifiedEmpty = item.PosterUrl is not null && PosterOwnerVerifiedEmpty,
            OptionalFreshnessTracked = true,
            AvailabilityOwnedByWatchmode = !AvailabilityFailed && !AvailabilitySkipped
                ? AvailabilityOwnedByWatchmode ?? PreviousMetadata?.AvailabilityOwnedByWatchmode ?? false
                : PreviousMetadata?.AvailabilityOwnedByWatchmode ?? false,
            AvailabilityFetchedUtc = !AvailabilityFailed && !AvailabilitySkipped ? CurrentUtc : PreviousMetadataClock(PreviousMetadata?.AvailabilityFetchedUtc),
            TvmazeFetchedUtc = MediaType != PremiereMediaType.Series ? null
                : !TvmazeFailed && !TvmazeSkipped ? CurrentUtc : PreviousMetadataClock(PreviousMetadata?.TvmazeFetchedUtc),
            ArtworkFetchedUtc = !ArtworkFailed && !ArtworkSkipped && !ArtworkReused && !BackdropReused ? CurrentUtc
                : PreviousMetadataClock(PreviousMetadata?.ArtworkFetchedUtc),
            OmdbPlot = ExternalSucceeded || ExternalReused ? ExternalResult?.Plot
                : string.Equals(item.ImdbId, PreviousMetadata?.ImdbId, StringComparison.OrdinalIgnoreCase) ? PreviousMetadata?.OmdbPlot : null,
            OmdbPosterUrl = ExternalSucceeded || ExternalReused ? ExternalResult?.PosterUrl
                : string.Equals(item.ImdbId, PreviousMetadata?.ImdbId, StringComparison.OrdinalIgnoreCase) ? PreviousMetadata?.OmdbPosterUrl : null
        };

        public bool ExternalNeeded(string? imdbId) => (explicitForceRefresh || ratingsRetryAllowed) && (explicitForceRefresh
            || PreviousRatings?.MatchesImdb(imdbId) != true
            || PreviousRatings.FreshnessVerified == false
            || PreviousRatings.ExternalRequired && RefreshRatings && (PreviousRatings.ProviderFreshnessTracked ? PreviousRatings.ExternalFetchedUtc : previousRatingsFetchedUtc) < ratingsCutoffUtc
            || PreviousRatings.ExternalRequired && RefreshRatings && (PreviousRatings.ProviderFreshnessTracked ? PreviousRatings.ExternalFetchedUtc : previousRatingsFetchedUtc) is null);

        public bool TomatoesNeeded(string? wikidataId, string? title, int? year) => (explicitForceRefresh || ratingsRetryAllowed) && (explicitForceRefresh
            || PreviousRatings?.MatchesTomatoes(wikidataId, title, year) != true
            || PreviousRatings.FreshnessVerified == false
            || PreviousRatings.TomatoesRequired && RefreshRatings && (PreviousRatings.ProviderFreshnessTracked ? PreviousRatings.TomatoesFetchedUtc : previousRatingsFetchedUtc) < ratingsCutoffUtc
            || PreviousRatings.TomatoesRequired && RefreshRatings && (PreviousRatings.ProviderFreshnessTracked ? PreviousRatings.TomatoesFetchedUtc : previousRatingsFetchedUtc) is null);

        public bool DatasetNeeded(string? imdbId) => DatasetRequired && !string.IsNullOrWhiteSpace(imdbId) && RatingsRetryAllowed
            && (explicitForceRefresh || PreviousRatings?.DatasetRequired != true || PreviousRatings.Dataset is not { } dataset
                || !string.Equals(dataset.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase)
                || dataset.FetchedUtc < ratingsCutoffUtc || !PreviousRatings.FreshnessVerified);

        public ItemEnrichmentRatings? CaptureRatings(PremiereItem item)
        {
            var previous = PreviousRatings;
            var skipChanged = previous is not null
                && (ExternalNotRequired && (previous.ExternalRequired || !previous.MatchesImdb(item.ImdbId) && (TomatoesSucceeded || TomatoesReused))
                    || TomatoesNotRequired && (previous.TomatoesRequired || !previous.MatchesTomatoes(item.WikidataId, item.Title, item.PremiereDate.Year)
                        && (ExternalSucceeded || ExternalReused || DatasetSucceeded)));
            if (!ExternalSucceeded && !TomatoesSucceeded && !DatasetSucceeded && !(ExternalNotRequired && TomatoesNotRequired) && !skipChanged) return null;
            var external = ExternalSucceeded && ExternalProviderResult is { } result
                ? new ItemEnrichmentRatingValues(result.ImdbScore, result.ImdbVoteCount, result.RottenTomatoesScore,
                    result.RottenTomatoesAudienceScore, result.MetacriticScore)
                : previous?.MatchesImdb(item.ImdbId) == true ? previous.ExternalValues
                    ?? (previous.ProviderFreshnessTracked ? null
                        : new(previous.ImdbScore, previous.ImdbVoteCount, MetacriticScore: previous.MetacriticScore)) : null;
            var dataset = DatasetSucceeded ? DatasetResult
                : previous?.Dataset is { } previousDataset && string.Equals(previousDataset.ImdbId, item.ImdbId, StringComparison.OrdinalIgnoreCase)
                    ? previousDataset : null;
            var tomatoes = TomatoesSucceeded && TomatoesResult is { } scores
                ? new ItemEnrichmentRatingValues(RottenTomatoesScore: scores.CriticScore, RottenTomatoesAudienceScore: scores.AudienceScore)
                : previous?.MatchesTomatoes(item.WikidataId, item.Title, item.PremiereDate.Year) == true
                    ? previous.TomatoesValues ?? (TomatoesNotRequired || previous.ProviderFreshnessTracked ? null
                        : new(RottenTomatoesScore: previous.RottenTomatoesScore, RottenTomatoesAudienceScore: previous.RottenTomatoesAudienceScore)) : null;
            var externalFetched = ExternalSucceeded ? now : previous?.MatchesImdb(item.ImdbId) != true ? null
                : previous.ProviderFreshnessTracked ? previous.ExternalFetchedUtc : previousRatingsFetchedUtc;
            var tomatoesFetched = TomatoesSucceeded ? now
                : previous?.MatchesTomatoes(item.WikidataId, item.Title, item.PremiereDate.Year) != true ? null
                : previous.ProviderFreshnessTracked ? previous.TomatoesFetchedUtc : previousRatingsFetchedUtc;
            var tomatoScores = ItemEnrichmentRatingsMerger.Combine(external, externalFetched, tomatoes, tomatoesFetched);
            return new(dataset?.ImdbScore ?? external?.ImdbScore, dataset?.ImdbScore is not null ? dataset.ImdbVoteCount : external?.ImdbVoteCount,
                tomatoScores.Critic, tomatoScores.Audience, external?.MetacriticScore,
                item.ImdbId, item.WikidataId, item.Title, item.PremiereDate.Year)
            {
                ProviderFreshnessTracked = true, ExternalValues = external, TomatoesValues = tomatoes,
                ExternalRequired = !ExternalNotRequired, TomatoesRequired = !TomatoesNotRequired,
                DatasetRequired = DatasetRequired, Dataset = dataset,
                ExternalFetchedUtc = externalFetched, TomatoesFetchedUtc = tomatoesFetched
            };
        }
    }
}
