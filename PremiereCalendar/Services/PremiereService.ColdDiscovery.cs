using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed partial class PremiereService
{
    private readonly AsyncLocal<ColdDiscoverySource?> _coldDiscoverySource = new();
    private readonly AsyncLocal<ExternalPremiereCandidate?> _coldExternalCandidate = new();
    private readonly AsyncLocal<ColdDetailFailure?> _coldDetailFailure = new();

    // Only profiles whose membership and ordering are established by TMDb core
    // fields can finish discovery before optional providers. Other profiles keep
    // the existing complete-item path, including its unknown-value semantics.
    private bool CanDiscoverCoreFirst(bool forceRefresh, CalendarFilters? requested)
    {
        if (forceRefresh || _itemCacheSession.Value is null) return false;
        var filters = requested ?? CalendarFilters.CreateDefault();
        if (filters.ScoreSource != ScoreSource.Tmdb || filters.SelectedSources.Count > 0
            || !string.IsNullOrWhiteSpace(filters.NetworkText) || !string.IsNullOrWhiteSpace(filters.KeywordText)) return false;
        if (filters.ShowSeries && (filters.RuntimeMinMinutes > 0 || filters.RuntimeMaxMinutes < 360
            || filters.SortMode == PremiereSortMode.Runtime)) return false;
        return (!filters.ShowMovies || CoreFieldsDetermineMembership(filters.MovieFilters, false))
            && (!filters.ShowSeries || CoreFieldsDetermineMembership(filters.SeriesFilters, true));
    }

    private static bool CoreFieldsDetermineMembership(MediaFilterSet filters, bool series)
        => filters.SelectedSources.Count == 0 && filters.MonetizationTypes.Count == 0
            && string.IsNullOrWhiteSpace(filters.SourceText) && string.IsNullOrWhiteSpace(filters.KeywordText)
            && string.IsNullOrWhiteSpace(filters.WatchRegion)
            && (!series || filters.RuntimeMinMinutes <= 0 && filters.RuntimeMaxMinutes >= 360);

    private sealed class ColdDiscoverySource(SemaphoreSlim optionalGate)
    {
        public SemaphoreSlim OptionalGate { get; } = optionalGate;
        public int ReservedOptionalPermit;
        public readonly ConcurrentQueue<ColdDiscoveryMapping> Mappings = new();
        public readonly ConcurrentDictionary<string, byte> CompleteExternalFallbacks = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentDictionary<ItemEnrichmentIdentity, ColdDetailFailure> DetailFailures = new();
        public IReadOnlyList<PremiereItem> CoreItems { get; set; } = [];
        public Exception? Error { get; set; }
        public bool Exhausted { get; set; }
        public int UnverifiedCore;

        private readonly object _deadlineGate = new();
        private CancellationTokenSource? _deadline;
        private TimeSpan _inactivityBudget;
        private int _activeMappings, _waitingMappings;
        private bool _consumerPaused, _clockPaused;

        public IDisposable TrackCompletionDeadline(CancellationTokenSource? deadline, TimeSpan budget)
        {
            lock (_deadlineGate)
            {
                _deadline = deadline;
                _inactivityBudget = budget;
                UpdateDeadline(renew: true);
            }
            return new ItemCacheScope(() => { lock (_deadlineGate) _deadline = null; });
        }

        public void SetCompletionDeadline(TimeSpan due)
        {
            lock (_deadlineGate)
            {
                _consumerPaused = due == Timeout.InfiniteTimeSpan;
                UpdateDeadline(renew: true);
            }
        }

        public void OptionalWaiting()
        {
            lock (_deadlineGate) { _waitingMappings++; UpdateDeadline(); }
        }

        public void OptionalAdmitted()
        {
            lock (_deadlineGate) { _waitingMappings--; _activeMappings++; UpdateDeadline(); }
        }

        public void OptionalFinished(bool admitted)
        {
            lock (_deadlineGate)
            {
                if (admitted) _activeMappings--; else _waitingMappings--;
                UpdateDeadline();
            }
        }

        private void UpdateDeadline(bool renew = false)
        {
            var pause = _consumerPaused || _activeMappings == 0 && _waitingMappings > 0;
            if (_deadline is not null && !_deadline.IsCancellationRequested && (renew || pause != _clockPaused))
                _deadline.CancelAfter(pause ? Timeout.InfiniteTimeSpan : _inactivityBudget);
            _clockPaused = pause;
        }

        public void ReleaseUnusedPermit()
        {
            if (Interlocked.Exchange(ref ReservedOptionalPermit, 0) != 0) OptionalGate.Release();
        }
    }

    private sealed record ColdDetailFailure(ItemEnrichmentIdentity Identity, TmdbDetailsWithExtras? Details);

    private void RememberColdDetailFailure(PremiereMediaType mediaType, int id, TmdbDetailsWithExtras? details)
    {
        if (_coldDiscoverySource.Value is not { } source) return;
        var identity = new ItemEnrichmentIdentity(mediaType, id);
        source.DetailFailures[identity] = new(identity, details);
    }

    private IDisposable UseColdDetailFailure(ColdDiscoverySource source, ItemEnrichmentIdentity identity)
    {
        var previous = _coldDetailFailure.Value;
        _coldDetailFailure.Value = source.DetailFailures.GetValueOrDefault(identity);
        return new ItemCacheScope(() => _coldDetailFailure.Value = previous);
    }

    // These are source projections, not queued delegates or already-started
    // tasks. Memory grows with retained candidates; optional tasks are created
    // lazily by bounded source continuations after each source has exhausted.
    private sealed record ColdDiscoveryMapping(
        ItemEnrichmentIdentity Identity,
        long Generation,
        TmdbMovieDiscoverItem? Movie = null,
        TmdbTvDiscoverItem? Series = null,
        ExternalPremiereCandidate? External = null,
        MediaDiscoveryCriteria? MovieCriteria = null,
        DateOnly? PremiereDate = null,
        PremiereItemType? ItemType = null,
        string? EpisodeTitle = null,
        int? SeasonNumber = null,
        int? EpisodeNumber = null,
        string? EpisodeSource = null,
        DateOnly? RequestedStart = null,
        DateOnly? RequestedEnd = null,
        bool CanonicalizeSeriesDate = false);

    private IDisposable UseColdDiscoverySource(ColdDiscoverySource? source)
    {
        var previous = _coldDiscoverySource.Value;
        _coldDiscoverySource.Value = source;
        return new ItemCacheScope(() => _coldDiscoverySource.Value = previous);
    }

    private IDisposable UseColdExternalCandidate(ExternalPremiereCandidate candidate)
    {
        var previous = _coldExternalCandidate.Value;
        _coldExternalCandidate.Value = _coldDiscoverySource.Value is null ? null : candidate;
        return new ItemCacheScope(() => _coldExternalCandidate.Value = previous);
    }

    private async IAsyncEnumerable<PremiereSourceBatch> WithColdDiscoverySource(
        IAsyncEnumerable<PremiereSourceBatch> batches, ColdDiscoverySource? source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerator<PremiereSourceBatch> enumerator;
        using (UseColdDiscoverySource(source)) enumerator = batches.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                PremiereSourceBatch batch;
                using (UseColdDiscoverySource(source))
                {
                    moved = await enumerator.MoveNextAsync();
                    batch = moved ? enumerator.Current : null!;
                }
                if (!moved) yield break;
                yield return batch;
            }
        }
        finally
        {
            using (UseColdDiscoverySource(source)) await enumerator.DisposeAsync();
        }
    }

    private async Task<PremiereItem?> MapMovieDiscoveryCoreAsync(TmdbMovieDiscoverItem item,
        MediaDiscoveryCriteria? criteria, CancellationToken cancellationToken)
    {
        var session = _itemCacheSession.Value!;
        var source = _coldDiscoverySource.Value!;
        var discovered = MapMovieMetadata(item);
        if (discovered is null) return null;
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, item.Id);
        var outcome = await session.GetAsync(identity, false, async (previous, metadataDue, _, token) =>
        {
            if (!metadataDue) return new(null, null, RefreshMetadataClock: false, RefreshRatingsClock: false);
            var attempt = new ItemEnrichmentAttempt(true, false, previous.Ratings, previous.Metadata,
                previous.Record.RatingsFetchedUtc, session.Now, session.RatingsCutoffUtc,
                previousMetadataFetchedUtc: previous.Record.MetadataFetchedUtc)
                { ItemId = item.Id, MediaType = PremiereMediaType.Movie };
            var old = _itemEnrichmentAttempt.Value;
            _itemEnrichmentAttempt.Value = attempt;
            try
            {
                var core = await MapMovieUncachedCoreAsync(item, token,
                    previous.Record.MetadataInvalidatedUtc > previous.Record.MetadataFetchedUtc, coreOnly: true);
                var metadata = core is not null && attempt.MetadataSucceeded
                    ? CaptureCoreMovieMetadata(attempt, core, previous.Metadata) : null;
                return new(metadata, null, RefreshMetadataClock: metadata is not null,
                    RefreshRatingsClock: false);
            }
            finally { _itemEnrichmentAttempt.Value = old; }
        }, cancellationToken, metadataOnly: true, ratingsInputs: discovered);
        if (!session.MetadataFresh(identity))
        {
            // A failed speculative core read must not suppress the complete
            // mapper's established fallback/provider and failure-clock rules.
            using var fallbackPolicy = UseColdDiscoverySource(null);
            using var failedDetails = UseColdDetailFailure(source, identity);
            var fallback = await MapMovieCoreAsync(item, cancellationToken, false, requestedMovieCriteria: criteria);
            if (!session.MetadataFresh(identity)) Interlocked.Exchange(ref source.UnverifiedCore, 1);
            if (_coldExternalCandidate.Value is { } failedExternal) source.CompleteExternalFallbacks[ExternalCandidateKey(failedExternal)] = 0;
            return fallback;
        }
        var result = outcome.Apply(discovered);
        if (!MovieMatchesKnownCriteria(result, criteria)) return null;
        source.Mappings.Enqueue(_coldExternalCandidate.Value is { } external
            ? new(identity, outcome.Record.Generation, External: external)
            : new(identity, outcome.Record.Generation, Movie: item, MovieCriteria: criteria));
        return result;
    }

    private async Task<PremiereItem?> MapSeriesDiscoveryCoreAsync(TmdbTvDiscoverItem item,
        DateOnly? premiereDateOverride, PremiereItemType? itemTypeOverride, string? episodeTitle,
        int? seasonNumber, int? episodeNumber, string? episodeSource, DateOnly? requestedStart,
        DateOnly? requestedEnd, bool canonicalizeSeriesDate, CancellationToken cancellationToken)
    {
        var session = _itemCacheSession.Value!;
        var source = _coldDiscoverySource.Value!;
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, item.Id);
        if (session.Find(identity)?.Metadata is { CoreMetadataOnly: false } && !session.MetadataFresh(identity))
        {
            // Existing complete series can carry TVmaze summary/runtime/network
            // and artwork fallbacks whose priority depends on the full refresh.
            // Keep that established preservation path when their core is due.
            using var fallbackPolicy = UseColdDiscoverySource(null);
            var fallback = await MapSeriesCoreAsync(item, cancellationToken, false,
                premiereDateOverride: premiereDateOverride, itemTypeOverride: itemTypeOverride, episodeTitle: episodeTitle,
                seasonNumber: seasonNumber, episodeNumber: episodeNumber, episodeSource: episodeSource,
                requestedStart: requestedStart, requestedEnd: requestedEnd, canonicalizeSeriesPremiereDate: canonicalizeSeriesDate);
            if (!session.MetadataFresh(identity)) Interlocked.Exchange(ref source.UnverifiedCore, 1);
            if (_coldExternalCandidate.Value is { } previousExternal) source.CompleteExternalFallbacks[ExternalCandidateKey(previousExternal)] = 0;
            return fallback;
        }
        if (canonicalizeSeriesDate)
            premiereDateOverride = await GetSeasonOneEpisodeOneDateAsync(item.Id, cancellationToken, false) ?? premiereDateOverride;
        var discovered = MapSeriesMetadata(item, premiereDateOverride, itemTypeOverride, episodeTitle, seasonNumber, episodeNumber, episodeSource);
        if (discovered is null || requestedStart is { } first && discovered.PremiereDate < first
            || requestedEnd is { } last && discovered.PremiereDate > last) return null;
        discovered = discovered with { DateSemantics = BuildSeriesDateSemantics(discovered.Type, discovered.PremiereDate,
            premiereDateOverride, canonicalizeSeriesDate, episodeSource) };
        var outcome = await session.GetAsync(identity, false, async (previous, metadataDue, _, token) =>
        {
            if (!metadataDue) return new(null, null, RefreshMetadataClock: false, RefreshRatingsClock: false);
            var attempt = new ItemEnrichmentAttempt(true, false, previous.Ratings, previous.Metadata,
                previous.Record.RatingsFetchedUtc, session.Now, session.RatingsCutoffUtc,
                previousMetadataFetchedUtc: previous.Record.MetadataFetchedUtc)
                { ItemId = item.Id, MediaType = PremiereMediaType.Series };
            var old = _itemEnrichmentAttempt.Value;
            _itemEnrichmentAttempt.Value = attempt;
            try
            {
                var details = await TryGetDetailsAsync(() => _tmdbClient.GetTvDetailsAsync(item.Id, token,
                    previous.Record.MetadataInvalidatedUtc > previous.Record.MetadataFetchedUtc), PremiereMediaType.Series, item.Id, token);
                if (details is null || !attempt.MetadataSucceeded)
                    return new(null, null, RefreshMetadataClock: false, RefreshRatingsClock: false);
                var sources = EnrichmentSourceEntries(details, _options.SourceRegions);
                var runtime = details.EpisodeRunTime.FirstOrDefault(value => value > 0);
                var poster = BuildImageUrl(_options.PosterSize,
                    CoalesceText(item.PosterPath, details.PosterPath, SelectBestImagePath(details.Images?.Posters)));
                var core = discovered with
                {
                    ImdbId = details.ExternalIds?.ImdbId, TvdbId = details.ExternalIds?.TvdbId, WikidataId = details.ExternalIds?.WikidataId,
                    Title = CoalesceText(item.Name, details.Name, details.OriginalName) ?? discovered.Title,
                    OriginalTitle = CoalesceText(item.OriginalName, details.OriginalName), Overview = CoalesceText(item.Overview, details.Overview),
                    PosterUrl = poster, ImageSource = poster is null ? null : "TMDb poster",
                    BackdropUrl = BuildImageUrl(_options.BackdropSize, CoalesceText(item.BackdropPath, details.BackdropPath, SelectBestImagePath(details.Images?.Backdrops))),
                    TrailerUrl = _trailerSelector.SelectBestYouTubeTrailer(details.Videos?.Results), ImdbUrl = BuildImdbUrl(details.ExternalIds?.ImdbId),
                    OriginalLanguage = CoalesceText(item.OriginalLanguage, details.OriginalLanguage) ?? "",
                    OriginCountries = OriginCountriesOrFallback(details, item.OriginCountry), Sources = sources, SourceNames = SourceNames(sources),
                    GenreIds = GenreIdsOrFallback(details, item.GenreIds), Genres = GenreNames(details), Keywords = KeywordNames(details.Keywords),
                    Certifications = TvCertifications(details, _options.SourceRegions), TvStatus = details.Status, TvType = details.TvType,
                    RuntimeMinutes = runtime is > 0 ? runtime : null
                };
                var metadata = attempt.CaptureMetadata(core) with
                {
                    CoreMetadataOnly = true, AvailabilityComplete = HasWatchProviderSource(sources),
                    AvailabilityFetchedUtc = HasWatchProviderSource(sources) ? session.Now : previous.Metadata?.AvailabilityFetchedUtc,
                    AvailabilityOwnedByWatchmode = !HasWatchProviderSource(sources) && previous.Metadata?.AvailabilityOwnedByWatchmode == true,
                    Sources = HasWatchProviderSource(sources) ? sources
                        : sources.Concat(previous.Metadata?.Sources.Where(value => HasWatchProviderSource([value])) ?? []).Distinct().ToArray(),
                    TvmazeComplete = false, TvmazeFetchedUtc = previous.Metadata?.TvmazeFetchedUtc,
                    RuntimeFromTvmaze = runtime is not > 0, ArtworkComplete = poster is not null,
                    ArtworkFetchedUtc = poster is not null ? session.Now : previous.Metadata?.ArtworkFetchedUtc,
                    OptionalMetadataComplete = false,
                    PosterUrl = poster ?? previous.Metadata?.PosterUrl,
                    ImageSource = poster is null ? previous.Metadata?.ImageSource : "TMDb poster",
                    PosterOwnerVerifiedEmpty = poster is null && previous.Metadata?.ImageSource == "TMDb poster"
                        || core.BackdropUrl is null && previous.Metadata?.ImageSource == "TMDb backdrop"
                        || previous.Metadata?.PosterOwnerVerifiedEmpty == true && poster is null
                };
                var invalidationPending = previous.Record.MetadataInvalidatedUtc is { } invalidated
                    && (previous.Record.MetadataFetchedUtc is null || previous.Record.MetadataFetchedUtc < invalidated);
                if (session.FirstEpisodeDates.TryGetValue((item.Id, previous.Record.Generation), out var validationTask))
                {
                    var validation = await validationTask.Value.WaitAsync(token);
                    if (validation.Successful) metadata = metadata with { FirstEpisodeDateChecked = true, FirstEpisodeDate = validation.Date };
                    else if (!invalidationPending && previous.Metadata is { FirstEpisodeDateChecked: true } prior)
                        metadata = metadata with { FirstEpisodeDateChecked = true, FirstEpisodeDate = prior.FirstEpisodeDate };
                }
                else if (!invalidationPending && previous.Metadata is { FirstEpisodeDateChecked: true } prior)
                    metadata = metadata with { FirstEpisodeDateChecked = true, FirstEpisodeDate = prior.FirstEpisodeDate };
                return new(metadata, null, RefreshRatingsClock: false);
            }
            finally { _itemEnrichmentAttempt.Value = old; }
        }, cancellationToken, metadataOnly: true, ratingsInputs: discovered);
        if (!session.MetadataFresh(identity))
        {
            using var fallbackPolicy = UseColdDiscoverySource(null);
            using var failedDetails = UseColdDetailFailure(source, identity);
            var fallback = await MapSeriesCoreAsync(item, cancellationToken, false, premiereDateOverride: premiereDateOverride,
                itemTypeOverride: itemTypeOverride, episodeTitle: episodeTitle, seasonNumber: seasonNumber,
                episodeNumber: episodeNumber, episodeSource: episodeSource, requestedStart: requestedStart,
                requestedEnd: requestedEnd, canonicalizeSeriesPremiereDate: canonicalizeSeriesDate);
            if (!session.MetadataFresh(identity)) Interlocked.Exchange(ref source.UnverifiedCore, 1);
            if (_coldExternalCandidate.Value is { } failedExternal) source.CompleteExternalFallbacks[ExternalCandidateKey(failedExternal)] = 0;
            return fallback;
        }
        if (discovered.Type == PremiereItemType.SeriesPremiere
            && outcome.Metadata is not { FirstEpisodeDateChecked: true })
            Interlocked.Exchange(ref source.UnverifiedCore, 1);
        source.Mappings.Enqueue(_coldExternalCandidate.Value is { } external ? new(identity, outcome.Record.Generation, External: external)
            : new(identity, outcome.Record.Generation, Series: item, PremiereDate: premiereDateOverride, ItemType: itemTypeOverride,
                EpisodeTitle: episodeTitle, SeasonNumber: seasonNumber, EpisodeNumber: episodeNumber, EpisodeSource: episodeSource,
                RequestedStart: requestedStart, RequestedEnd: requestedEnd, CanonicalizeSeriesDate: canonicalizeSeriesDate));
        return outcome.Apply(discovered);
    }

    private async IAsyncEnumerable<PremiereItemBatch> CompleteColdDiscoverySourceAsync(ColdDiscoverySource source,
        PremiereDiscoveryCriteria criteria, IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        DateOnly start, DateOnly end, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var policy = UseColdDiscoverySource(null);
        await foreach (var batch in MapInProgressBatchesAsync(source.Mappings, CompleteAsync, cancellationToken))
        {
            if (_itemCacheSession.Value is { } session) await session.FlushAsync(cancellationToken);
            yield return batch;
        }

        async Task<PremiereItem?> CompleteAsync(ColdDiscoveryMapping mapping, CancellationToken token)
        {
            source.OptionalWaiting();
            var admitted = false;
            try
            {
                if (Interlocked.Exchange(ref source.ReservedOptionalPermit, 0) == 0)
                    await source.OptionalGate.WaitAsync(token);
                source.OptionalAdmitted();
                admitted = true;
                if (_itemCacheSession.Value?.Find(mapping.Identity)?.Record.Generation != mapping.Generation)
                {
                    Interlocked.Exchange(ref source.UnverifiedCore, 1);
                    throw new ItemEnrichmentInvalidatedException();
                }
                if (mapping.External is { } external)
                    return await MapExternalCandidateAsync(external, token, false, criteria, cachedEnrichment, start, end);
                if (mapping.Movie is { } movie)
                    return await MapMovieAsync(movie, token, false, cachedEnrichment, requestedMovieCriteria: mapping.MovieCriteria);
                return await MapSeriesAsync(mapping.Series!, token, false, cachedEnrichment,
                    mapping.PremiereDate, mapping.ItemType, mapping.EpisodeTitle, mapping.SeasonNumber, mapping.EpisodeNumber,
                    mapping.EpisodeSource, requestedStart: mapping.RequestedStart, requestedEnd: mapping.RequestedEnd,
                    canonicalizeSeriesPremiereDate: mapping.CanonicalizeSeriesDate);
            }
            finally
            {
                source.OptionalFinished(admitted);
                if (admitted) source.OptionalGate.Release();
            }
        }
    }
}
