using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class ItemEnrichmentCacheCoordinator
{
    private readonly IItemEnrichmentStore _store;
    private readonly ItemEnrichmentCacheOptions _options;
    private readonly ILogger<ItemEnrichmentCacheCoordinator> _logger;
    private readonly TimeProvider _clock;
    private readonly IIntegrationSettingsStore? _integrationSettingsStore;
    private readonly IAppStateStore? _appStateStore;
    private readonly SemaphoreSlim _legacyPartitionGate = new(1);
    private string? _legacyPartition;
    private readonly SingleFlightCoordinator _flights = new();
    private const int MaximumCoreProposalCohorts = 2048;
    private const int MaximumProposalsPerCoreCohort = 16;
    private readonly object _coreProposalGate = new();
    private readonly Dictionary<string, CoreProposalCohort> _coreProposalCohorts = new(StringComparer.Ordinal);
    private readonly PremiereTelemetry _telemetry;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _metadataRetryAfter = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _availabilityRetryAfter = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _optionalMetadataRetryAfter = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _componentRetryAfter = new(StringComparer.Ordinal);
    private long _storeRetryAfterTicks;
    public bool Enabled => _options.Enabled;
    public string Partition { get; }

    internal int RetainedCoreProposalCohortCount
    {
        get { lock (_coreProposalGate) return _coreProposalCohorts.Count; }
    }

    // These are completed provider proposals, not accepted cache entries. Only
    // sessions sharing the exact reserved core flight join this short lifetime;
    // every consumer still stages and validates its own ordinary CAS writes.
    private sealed class CoreProposalCohort
    {
        public int Leases;
        private readonly object _gate = new();
        private readonly Dictionary<string, ItemEnrichmentSnapshot> _proposals = new(StringComparer.Ordinal);
        private bool _closed;

        public ItemEnrichmentSnapshot? Find(string exactFlightKey)
        {
            lock (_gate) return !_closed ? _proposals.GetValueOrDefault(exactFlightKey) : null;
        }

        public void Remember(string exactFlightKey, ItemEnrichmentSnapshot proposal)
        {
            lock (_gate)
                if (!_closed && _proposals.Count < MaximumProposalsPerCoreCohort)
                    _proposals.TryAdd(exactFlightKey, proposal);
        }

        public void Close()
        {
            lock (_gate) { _closed = true; _proposals.Clear(); }
        }
    }

    private CoreProposalCohort? LeaseCoreProposalCohort(string exactCoreFlightKey)
    {
        lock (_coreProposalGate)
        {
            if (!_coreProposalCohorts.TryGetValue(exactCoreFlightKey, out var cohort))
            {
                // Capacity only declines this optimization; it never drops work
                // or evicts proposals still owned by an active session.
                if (_coreProposalCohorts.Count >= MaximumCoreProposalCohorts) return null;
                _coreProposalCohorts.Add(exactCoreFlightKey, cohort = new CoreProposalCohort());
            }
            cohort.Leases++;
            return cohort;
        }
    }

    private void ReleaseCoreProposalCohort(string key, CoreProposalCohort cohort)
    {
        lock (_coreProposalGate)
        {
            if (--cohort.Leases > 0) return;
            if (_coreProposalCohorts.GetValueOrDefault(key) == cohort) _coreProposalCohorts.Remove(key);
            cohort.Close();
        }
    }

    public ItemEnrichmentCacheCoordinator(IItemEnrichmentStore store,
        IOptions<ItemEnrichmentCacheOptions> options, IOptions<TmdbOptions> tmdbOptions,
        ILogger<ItemEnrichmentCacheCoordinator> logger, IConfiguration? configuration = null, TimeProvider? timeProvider = null,
        IIntegrationSettingsStore? integrationSettingsStore = null, IAppStateStore? appStateStore = null,
        PremiereTelemetry? telemetry = null)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
        _clock = timeProvider ?? TimeProvider.System;
        _integrationSettingsStore = integrationSettingsStore;
        _appStateStore = appStateStore;
        _telemetry = telemetry ?? new PremiereTelemetry();
        var tmdb = tmdbOptions.Value;
        var config = new StringBuilder(JsonSerializer.Serialize(new
        {
            Version = 1, tmdb.BaseUrl, tmdb.ImageBaseUrl, tmdb.PosterSize, tmdb.BackdropSize,
            Regions = tmdb.SourceRegions.Select(region => region.Trim().ToUpperInvariant()).Order().ToArray()
        }));
        // Only the hash is persisted. Provider switches, endpoints, language and
        // account settings partition results; request criteria and dates do not.
        foreach (var section in new[] { "Omdb", "ImdbDataset", "Tvmaze", "Fanart", "TheTvdb", "Wikimedia", "RottenTomatoes", "Watchmode" })
        {
            foreach (var setting in configuration?.GetSection(section).AsEnumerable().OrderBy(pair => pair.Key, StringComparer.Ordinal)
                         ?? Enumerable.Empty<KeyValuePair<string, string?>>())
            {
                // Admission policy changes no provider payload. Keep the existing
                // data partition when introducing or tuning this new setting.
                if (setting.Key.Equals("Tvmaze:MinimumRequestIntervalMilliseconds", StringComparison.OrdinalIgnoreCase)) continue;
                config.Append('\n').Append(setting.Key).Append('=').Append(setting.Value);
            }
        }
        Partition = "item-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.ToString()))).ToLowerInvariant();
    }

    internal async Task<ItemEnrichmentSession> CreateSessionAsync(CancellationToken cancellationToken)
    {
        if (_integrationSettingsStore is null) return new(this, Partition, await CanImportLegacyAsync(Partition, cancellationToken), cancellationToken);
        var sources = (await _integrationSettingsStore.GetAsync(cancellationToken)).Sources;
        var effective = JsonSerializer.Serialize(new
        {
            Tvmaze = sources.Tvmaze.Enabled, Omdb = sources.Omdb.Enabled, Fanart = sources.Fanart.Enabled,
            TheTvdb = sources.TheTvdb.Enabled, Wikimedia = sources.Wikimedia.Enabled,
            Watchmode = sources.Watchmode.Enabled,
            Regions = sources.Watchmode.Regions.Select(region => region.Trim().ToUpperInvariant()).Order().ToArray(),
            sources.Watchmode.EnableAvailabilityEnrichment
        });
        var partition = Partition + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(effective))).ToLowerInvariant();
        return new(this, partition, await CanImportLegacyAsync(partition, cancellationToken), cancellationToken);
    }

    private async Task<bool> CanImportLegacyAsync(string partition, CancellationToken cancellationToken)
    {
        await _legacyPartitionGate.WaitAsync(cancellationToken);
        try
        {
            if (_legacyPartition is null)
            {
                const string key = "itemcache:legacy-import-partition:v1";
                _legacyPartition = _appStateStore is null ? null : await _appStateStore.GetValueAsync(key, cancellationToken);
                if (string.IsNullOrWhiteSpace(_legacyPartition))
                {
                    _legacyPartition = partition;
                    if (_appStateStore is not null) await _appStateStore.SetValueAsync(key, partition, cancellationToken);
                }
            }
            return string.Equals(_legacyPartition, partition, StringComparison.Ordinal);
        }
        finally { _legacyPartitionGate.Release(); }
    }

    internal async Task ImportAsync(IReadOnlyList<PremiereItem> items, CancellationToken cancellationToken)
    {
        if (!Enabled || items.Count == 0) return;
        var started = Stopwatch.GetTimestamp();
        var session = await CreateSessionAsync(cancellationToken);
        await session.PrepareAsync(items.Select(item => new ItemEnrichmentIdentity(item.MediaType, item.TmdbId)), items, cancellationToken);
        await session.FlushAsync(cancellationToken, rejectGenerationConflicts: false);
        _telemetry.RecordDiagnostic("item_cache.import", details: new() { Count = items.Count, Outcome = "complete", DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
    }

    internal async Task SynchronizeMaintainedProjectionAsync(IReadOnlyList<PremiereItem> originalItems,
        IReadOnlyList<PremiereItem> publishedItems, CalendarCacheMaintenanceKind kind, CancellationToken cancellationToken)
    {
        if (!Enabled || publishedItems.Count == 0) return;
        var session = await CreateSessionAsync(cancellationToken);
        await session.PrepareAsync(originalItems.Select(item => new ItemEnrichmentIdentity(item.MediaType, item.TmdbId)), originalItems, cancellationToken);
        await session.FlushAsync(cancellationToken, rejectGenerationConflicts: false);
        var baseline = publishedItems.Select(item => new ItemEnrichmentIdentity(item.MediaType, item.TmdbId)).Distinct()
            .Select(identity => (Identity: identity, Snapshot: session.Find(identity))).Where(entry => entry.Snapshot is not null)
            .ToDictionary(entry => entry.Identity, entry => entry.Snapshot!);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            session.SynchronizeMaintenanceFields(originalItems, publishedItems, kind, baseline);
            if (!session.HasPendingWrites) return;
            await session.FlushAsync(cancellationToken, rejectGenerationConflicts: false);
        }
        session.SynchronizeMaintenanceFields(originalItems, publishedItems, kind, baseline);
        if (session.HasPendingWrites)
            _logger.LogWarning("Item maintenance synchronization could not settle after three revision retries; current database winners remain authoritative.");
    }

    internal bool MetadataFresh(ItemEnrichmentSnapshot snapshot) => snapshot.Metadata is not null
        && snapshot.Record.MetadataFetchedUtc is { } fetched
        && fetched >= _clock.GetUtcNow().AddDays(-Math.Max(1, _options.MetadataRefreshDays))
        && (snapshot.Record.MetadataInvalidatedUtc is not { } invalidated || fetched >= invalidated);

    internal bool RatingsFresh(ItemEnrichmentSnapshot snapshot) => snapshot.Ratings is not null
        && snapshot.Ratings.FreshnessVerified
        && snapshot.Ratings.MatchesMetadata(snapshot.Metadata)
        && (snapshot.Ratings.ProviderFreshnessTracked
            ? (!snapshot.Ratings.ExternalRequired || snapshot.Ratings.ExternalFetchedUtc >= RatingsCutoffUtc)
                && (!snapshot.Ratings.TomatoesRequired || snapshot.Ratings.TomatoesFetchedUtc >= RatingsCutoffUtc)
                && (!snapshot.Ratings.DatasetRequired || snapshot.Ratings.Dataset is { } dataset
                    && dataset.FetchedUtc >= RatingsCutoffUtc && snapshot.Ratings.MatchesImdb(dataset.ImdbId))
            : snapshot.Record.RatingsFetchedUtc >= RatingsCutoffUtc);

    private DateTimeOffset RatingsCutoffUtc => _clock.GetUtcNow().AddHours(-Math.Max(1, _options.RatingsRefreshHours));

    internal sealed class ItemEnrichmentSession(ItemEnrichmentCacheCoordinator owner, string partition, bool allowLegacyImport,
        CancellationToken lifetimeToken) : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<ItemEnrichmentIdentity, ItemEnrichmentSnapshot> _records = new();
        private readonly ConcurrentDictionary<ItemEnrichmentIdentity, ItemEnrichmentSnapshot> _completed = new();
        private readonly ConcurrentDictionary<ItemEnrichmentIdentity, ItemEnrichmentWrite> _writes = new();
        private readonly ConcurrentDictionary<ItemEnrichmentIdentity, RatingProjectionChange> _ratingChanges = new();
        private readonly ConcurrentDictionary<(ItemEnrichmentIdentity Identity, string Binding), AcceptedTomatoes> _acceptedTomatoes = new();
        private readonly SemaphoreSlim _prepareGate = new(1);
        private readonly SemaphoreSlim _flushGate = new(1);
        private readonly ConcurrentDictionary<PremiereMediaType, byte> _discoveryChanged = new();
        private readonly ConcurrentDictionary<ItemEnrichmentIdentity, long> _selfAcceptedRevisions = new();
        // Accessed only under _flushGate. A data-equivalent accepted core may
        // advance its own optional continuation by exactly one revision.
        private readonly Dictionary<ItemEnrichmentIdentity, AcceptedCorePredecessor> _acceptedCorePredecessors = new();
        public bool ForceDiscoveryRefresh { get; set; }
        public DateTimeOffset Now => owner._clock.GetUtcNow();
        public DateTimeOffset RatingsCutoffUtc => owner.RatingsCutoffUtc;
        private bool _storageUnavailable;
        private DateTimeOffset? _storageRetryAtUtc;
        private readonly ConcurrentDictionary<ItemEnrichmentIdentity, Lazy<Task<IAsyncDisposable>>> _lookupReservations = new();
        private readonly ConcurrentDictionary<(ItemEnrichmentIdentity Identity, long Generation), DateTimeOffset> _providerObservationStarts = new();
        private readonly object _coreProposalLeaseGate = new();
        private readonly Dictionary<string, CoreProposalCohort> _coreProposalLeases = new(StringComparer.Ordinal);
        private readonly Dictionary<(ItemEnrichmentIdentity Identity, long Generation), CoreProposalCohort> _coreProposalsByItem = new();

        private CoreProposalCohort? JoinCoreProposalCohort(string key, ItemEnrichmentIdentity identity, long generation)
        {
            lock (_coreProposalLeaseGate)
            {
                if (_coreProposalLeases.TryGetValue(key, out var existing)) return existing;
                var cohort = owner.LeaseCoreProposalCohort(key);
                if (cohort is null) return null;
                _coreProposalLeases.Add(key, cohort);
                _coreProposalsByItem[(identity, generation)] = cohort;
                return cohort;
            }
        }

        private CoreProposalCohort? FindCoreProposalCohort(ItemEnrichmentIdentity identity, long generation)
        {
            lock (_coreProposalLeaseGate) return _coreProposalsByItem.GetValueOrDefault((identity, generation));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Start all releases together: each store budget includes waiting
                // for its gate, so an outage costs one deadline, not N deadlines.
                await Task.WhenAll(_lookupReservations.Values.Where(reservation => reservation.IsValueCreated).Select(async reservation =>
                {
                    try { await (await reservation.Value).DisposeAsync(); }
                    catch (Exception ex) { owner._logger.LogWarning(ex, "Could not release TVmaze lookup reservation; retained fence remains conservative."); }
                }));
            }
            finally
            {
                _lookupReservations.Clear();
                KeyValuePair<string, CoreProposalCohort>[] leases;
                lock (_coreProposalLeaseGate)
                {
                    leases = _coreProposalLeases.ToArray();
                    _coreProposalLeases.Clear();
                    _coreProposalsByItem.Clear();
                }
                foreach (var lease in leases) owner.ReleaseCoreProposalCohort(lease.Key, lease.Value);
            }
        }

        public bool HasPendingWrites => !_writes.IsEmpty;
        public void MarkDiscoveryUnverified(PremiereMediaType mediaType) => _discoveryChanged[mediaType] = 0;
        public CancellationToken LifetimeToken { get; } = lifetimeToken;
        public ConcurrentDictionary<(int TmdbId, long Generation), Lazy<Task<FirstEpisodeValidation>>> FirstEpisodeDates { get; } = new();
        public ConcurrentDictionary<string, Lazy<Task<int?>>> ResolvedCandidates { get; } = new(StringComparer.Ordinal);

        public ItemEnrichmentSnapshot? Find(ItemEnrichmentIdentity identity) =>
            _completed.TryGetValue(identity, out var result) ? result : _records.GetValueOrDefault(identity);

        public bool MetadataFresh(ItemEnrichmentIdentity identity) => Find(identity) is { } snapshot && owner.MetadataFresh(snapshot);
        public bool RatingsFresh(ItemEnrichmentIdentity identity) => Find(identity) is { } snapshot && owner.RatingsFresh(snapshot);
        public bool RatingsRefreshNeeded(ItemEnrichmentIdentity identity, PremiereItem? inputs = null, bool requireDataset = false)
        {
            var current = Find(identity);
            if (current?.PreserveRatings == true) return false;
            var ratings = current?.Ratings;
            if (ratings is null || !ratings.FreshnessVerified || inputs is not null && ratings.Matches(current?.Metadata?.Apply(inputs) ?? inputs) != true)
                return RatingsRetryAllowed(identity, inputs);
            if (!ratings.ProviderFreshnessTracked) return !RatingsFresh(identity) && RatingsRetryAllowed(identity, inputs);
            return ratings.ExternalRequired && ratings.ExternalFetchedUtc < owner.RatingsCutoffUtc && ComponentRetryAllowed(identity, "omdb")
                || ratings.ExternalRequired && ratings.ExternalFetchedUtc is null && ComponentRetryAllowed(identity, "omdb")
                || ratings.TomatoesRequired && (ratings.TomatoesFetchedUtc is null || ratings.TomatoesFetchedUtc < owner.RatingsCutoffUtc) && ComponentRetryAllowed(identity, "rottentomatoes")
                || (requireDataset || ratings.DatasetRequired) && DatasetRefreshNeeded(identity) && ComponentRetryAllowed(identity, "imdb_dataset");
        }
        public bool RatingsRefreshAllowed(ItemEnrichmentIdentity identity) => RatingsRetryAllowed(identity);
        private bool DatasetRefreshNeeded(ItemEnrichmentIdentity identity) => Find(identity) is { Metadata.ImdbId: { Length: > 0 } imdbId } current
            && (current.Ratings?.DatasetRequired != true || current.Ratings.Dataset is not { } dataset
                || dataset.FetchedUtc < owner.RatingsCutoffUtc || !string.Equals(dataset.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase));
        public static string ComponentIdentity(string provider, string? imdbId = null, string? wikidataId = null,
            string? title = null, int? tvdbId = null, int? year = null) => (provider switch
            {
                "omdb" or "imdb_dataset" => imdbId?.Trim() ?? "",
                "rottentomatoes" => $"{wikidataId?.Trim()}:{title?.Trim()}:{year?.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                "tvmaze" => $"{imdbId?.Trim()}:{tvdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                _ => ""
            }).ToUpperInvariant();

        private string ComponentKey(ItemEnrichmentIdentity identity, string provider, string? componentIdentity = null)
        {
            var current = Find(identity);
            var metadata = current?.Metadata;
            return $"{RetryKey(identity)}:{provider}:{componentIdentity ?? ComponentIdentity(provider, metadata?.ImdbId, metadata?.WikidataId, metadata?.Title, metadata?.TvdbId, current?.Ratings?.Year)}";
        }
        public bool ComponentRetryAllowed(ItemEnrichmentIdentity identity, string provider, string? componentIdentity = null)
            => RetryAllowed(owner._componentRetryAfter, ComponentKey(identity, provider, componentIdentity));
        public void RecordComponentOutcome(ItemEnrichmentIdentity identity, string provider, bool successful, DateTimeOffset? deadline = null,
            string? componentIdentity = null)
        {
            var key = ComponentKey(identity, provider, componentIdentity);
            if (successful) owner._componentRetryAfter.TryRemove(key, out _);
            else owner._componentRetryAfter[key] = deadline > Now ? deadline.Value : Now.AddMinutes(
                Math.Max(1, provider is "omdb" or "rottentomatoes" or "imdb_dataset" ? owner._options.RatingsFailureRetryMinutes : owner._options.MetadataFailureRetryMinutes));
            if (owner._componentRetryAfter.Count > 8192)
                foreach (var entry in owner._componentRetryAfter.OrderBy(entry => entry.Value).Take(owner._componentRetryAfter.Count - 8192))
                    owner._componentRetryAfter.TryRemove(entry.Key, out _);
        }
        public bool TvmazeRefreshNeeded(ItemEnrichmentIdentity identity) => Find(identity)?.Metadata is { TvmazeComplete: false }
            && ComponentRetryAllowed(identity, "tvmaze");
        public bool ArtworkRefreshNeeded(ItemEnrichmentIdentity identity) => Find(identity)?.Metadata is { ArtworkComplete: false }
            && ComponentRetryAllowed(identity, "artwork");
        public bool MetadataRefreshNeeded(ItemEnrichmentIdentity identity)
        {
            var current = Find(identity);
            return (current is null || !owner.MetadataFresh(current)) && RetryAllowed(identity)
                || current?.Metadata?.CoreMetadataOnly == true
                || AvailabilityRefreshNeeded(identity) || OptionalMetadataRefreshNeeded(identity);
        }
        public bool AvailabilityRefreshNeeded(ItemEnrichmentIdentity identity) => Find(identity)?.Metadata is { AvailabilityComplete: false }
            && RetryAllowed(owner._availabilityRetryAfter, RetryKey(identity));
        public bool OptionalMetadataRefreshNeeded(ItemEnrichmentIdentity identity) => TvmazeRefreshNeeded(identity) || ArtworkRefreshNeeded(identity);
        private string RetryKey(ItemEnrichmentIdentity identity) => $"{partition}:{identity.MediaType}:{identity.TmdbId}";
        private bool RetryAllowed(ItemEnrichmentIdentity identity)
            => RetryAllowed(owner._metadataRetryAfter, $"{RetryKey(identity)}:{Find(identity)?.Record.Generation}");
        private bool RetryAllowed(ConcurrentDictionary<string, DateTimeOffset> retries, string key)
        {
            if (!retries.TryGetValue(key, out var retry)) return true;
            if (retry > owner._clock.GetUtcNow()) return false;
            retries.TryRemove(key, out _);
            return true;
        }
        private string RatingsComponentIdentity(ItemEnrichmentIdentity identity, PremiereItem? inputs)
        {
            var current = Find(identity);
            var target = inputs is null ? null : current?.Metadata?.Apply(inputs) ?? inputs;
            return target is null ? ComponentIdentity("rottentomatoes", wikidataId: current?.Metadata?.WikidataId,
                    title: current?.Metadata?.Title, year: current?.Ratings?.Year)
                : ComponentIdentity("rottentomatoes", wikidataId: target.WikidataId, title: target.Title, year: target.PremiereDate.Year);
        }

        private bool RatingsRetryAllowed(ItemEnrichmentIdentity identity, PremiereItem? inputs = null) => Find(identity)?.PreserveRatings != true
            && (ComponentRetryAllowed(identity, "omdb")
                || ComponentRetryAllowed(identity, "rottentomatoes", RatingsComponentIdentity(identity, inputs))
                || ComponentRetryAllowed(identity, "imdb_dataset"));

        private void ObserveTmdbVotes(ItemEnrichmentIdentity identity, PremiereItem discovered)
        {
            var current = Find(identity);
            if (current?.Metadata is null || current.Record.MetadataFetchedUtc is null) return;
            var observation = ItemEnrichmentTmdbObservation.Observe(current.Metadata.TmdbVotes,
                discovered.TmdbScore, discovered.TmdbVoteCount, current.Record.LastSeenUtc);
            if (observation == current.Metadata.TmdbVotes) return;
            var metadata = current.Metadata with { TmdbVotes = observation };
            var updated = current with { Metadata = metadata };
            updated = _records.AddOrUpdate(identity, updated, (_, existing) => PreserveAcceptedRatings(updated, existing));
            if (_completed.ContainsKey(identity)) updated = _completed.AddOrUpdate(identity, updated, (_, existing) =>
                PreserveAcceptedRatings(updated, _records.GetValueOrDefault(identity) ?? existing));
            if (current.Record.Generation < 0) return;
            var write = new ItemEnrichmentWrite(identity, current.Record.Generation,
                    JsonSerializer.Serialize(metadata), current.Record.MetadataFetchedUtc, TvmazeShowId: metadata.TvmazeShowId,
                    TmdbObservedUtc: metadata.TmdbVotes?.TmdbObservedUtc, ExpectedMetadataRevision: current.Record.MetadataRevision)
            { RatingsOrigin = current.RatingsReadOrigin };
            _writes.AddOrUpdate(identity, write,
                (_, previous) => previous.ExpectedMetadataRevision != current.Record.MetadataRevision ? write : previous with
                { MetadataJson = write.MetadataJson, MetadataFetchedUtc = write.MetadataFetchedUtc, TmdbObservedUtc = write.TmdbObservedUtc,
                    ExpectedMetadataRevision = current.Record.MetadataRevision, CoreContinuation = null, FreshMovieCore = null,
                    RatingsOrigin = previous.RatingsJson is not null ? previous.RatingsOrigin : write.RatingsOrigin });
            if (Find(identity) is { PreserveRatings: true }) DropPendingRatings(identity);
        }

        public void RememberFirstEpisodeDate(ItemEnrichmentIdentity identity, long generation, FirstEpisodeValidation validation)
        {
            var current = Find(identity);
            if (!validation.Successful || current?.Metadata is null || !owner.MetadataFresh(current)
                || current.Record.Generation < 0 || current.Record.Generation != generation) return;
            var metadata = current.Metadata with { FirstEpisodeDateChecked = true, FirstEpisodeDate = validation.Date };
            var updated = current with { Metadata = metadata };
            updated = _records.AddOrUpdate(identity, updated, (_, existing) => PreserveAcceptedRatings(updated, existing));
            if (_completed.ContainsKey(identity)) updated = _completed.AddOrUpdate(identity, updated, (_, existing) =>
                PreserveAcceptedRatings(updated, _records.GetValueOrDefault(identity) ?? existing));
            var write = new ItemEnrichmentWrite(identity, current.Record.Generation,
                    JsonSerializer.Serialize(metadata), current.Record.MetadataFetchedUtc, TvmazeShowId: metadata.TvmazeShowId,
                    TmdbObservedUtc: metadata.TmdbVotes?.TmdbObservedUtc, ExpectedMetadataRevision: current.Record.MetadataRevision)
            { RatingsOrigin = current.RatingsReadOrigin };
            _writes.AddOrUpdate(identity, write,
                (_, previous) => previous.ExpectedMetadataRevision != current.Record.MetadataRevision ? write : previous with
                { MetadataJson = write.MetadataJson, MetadataFetchedUtc = write.MetadataFetchedUtc, TmdbObservedUtc = write.TmdbObservedUtc,
                    ExpectedMetadataRevision = current.Record.MetadataRevision, CoreContinuation = null, FreshMovieCore = null,
                    RatingsOrigin = previous.RatingsJson is not null ? previous.RatingsOrigin : write.RatingsOrigin });
            if (Find(identity) is { PreserveRatings: true }) DropPendingRatings(identity);
        }

        public IReadOnlyList<PremiereItem> ApplyPreservedRatings(IReadOnlyList<PremiereItem> items)
        {
            PremiereItem[]? projected = null;
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (Find(new(item.MediaType, item.TmdbId)) is not { PreserveRatings: true } accepted) continue;
                projected ??= items.ToArray();
                var cleared = item with { ImdbScore = null, ImdbVoteCount = null, RottenTomatoesScore = null,
                    RottenTomatoesAudienceScore = null, MetacriticScore = null };
                projected[index] = accepted.Ratings?.Apply(cleared) ?? cleared;
            }
            return projected ?? items;
        }

        public void SynchronizeMaintainedProjection(IReadOnlyList<PremiereItem> items)
        {
            foreach (var item in items)
            {
                var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
                var current = Find(identity);
                if (current?.Metadata is null || current.Record.MetadataFetchedUtc is null || current.Record.Generation < 0) continue;
                var metadata = ItemEnrichmentMetadata.FromItem(item) with
                {
                    FirstEpisodeDateChecked = current.Metadata.FirstEpisodeDateChecked,
                    FirstEpisodeDate = current.Metadata.FirstEpisodeDate,
                    TvmazeShowId = current.Metadata.TvmazeShowId,
                    TmdbVotes = current.Metadata.TmdbVotes,
                    AvailabilityComplete = current.Metadata.AvailabilityComplete,
                    AvailabilityOwnedByWatchmode = current.Metadata.AvailabilityOwnedByWatchmode,
                    OptionalMetadataComplete = current.Metadata.OptionalMetadataComplete,
                    TvmazeComplete = current.Metadata.TvmazeComplete, ArtworkComplete = current.Metadata.ArtworkComplete,
                    PosterOwnerVerifiedEmpty = item.PosterUrl == current.Metadata.PosterUrl && item.ImageSource == current.Metadata.ImageSource
                        && current.Metadata.PosterOwnerVerifiedEmpty,
                    RuntimeFromTvmaze = current.Metadata.RuntimeFromTvmaze,
                    OmdbPlot = current.Metadata.OmdbPlot, OmdbPosterUrl = current.Metadata.OmdbPosterUrl,
                    OptionalFreshnessTracked = current.Metadata.OptionalFreshnessTracked,
                    AvailabilityFetchedUtc = current.Metadata.AvailabilityFetchedUtc,
                    TvmazeFetchedUtc = current.Metadata.TvmazeFetchedUtc, ArtworkFetchedUtc = current.Metadata.ArtworkFetchedUtc
                };
                var ratings = current.Ratings;
                if (!current.PreserveRatings && ratings?.Matches(item) == true && ratings.MatchesMetadata(metadata))
                    ratings = ratings with
                    {
                        ImdbScore = item.ImdbScore, ImdbVoteCount = item.ImdbVoteCount,
                        RottenTomatoesScore = item.RottenTomatoesScore, RottenTomatoesAudienceScore = item.RottenTomatoesAudienceScore,
                        MetacriticScore = item.MetacriticScore
                    };
                var metadataJson = JsonSerializer.Serialize(metadata);
                var ratingsJson = ratings is null ? null : JsonSerializer.Serialize(ratings);
                var metadataChanged = metadataJson != JsonSerializer.Serialize(current.Metadata);
                var ratingsChanged = ratingsJson != (current.Ratings is null ? null : JsonSerializer.Serialize(current.Ratings));
                if (metadataChanged || ratingsChanged)
                {
                    _records[identity] = current with { Metadata = metadata, Ratings = ratings };
                    _completed.TryRemove(identity, out _);
                    _writes[identity] = new(identity, current.Record.Generation,
                        metadataChanged ? metadataJson : null, metadataChanged ? current.Record.MetadataFetchedUtc : null,
                        ratingsChanged ? ratingsJson : null, ratingsChanged ? current.Record.RatingsFetchedUtc : null, metadata.TvmazeShowId,
                        metadata.TmdbVotes?.TmdbObservedUtc, current.Record.MetadataRevision,
                        ExpectedRatingsJson: ratingsChanged ? current.Record.RatingsJson ?? "null" : null)
                    { RatingsOrigin = current.RatingsReadOrigin };
                }
                else if (!owner.RatingsFresh(current)) _completed.TryRemove(identity, out _);
            }
        }

        public void SynchronizeMaintenanceFields(IReadOnlyList<PremiereItem> originals, IReadOnlyList<PremiereItem> published,
            CalendarCacheMaintenanceKind kind, IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentSnapshot> baselineByIdentity)
        {
            var originalById = originals.GroupBy(item => item.CanonicalId).ToDictionary(group => group.Key, group => group.First());
            foreach (var item in published)
            {
                var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
                var current = Find(identity);
                if (current is null || current.Record.Generation < 0 || !originalById.TryGetValue(item.CanonicalId, out var original)
                    || !baselineByIdentity.TryGetValue(identity, out var firstRead)) continue;
                if (kind == CalendarCacheMaintenanceKind.ExternalIds && item.ImdbId == original.ImdbId && item.ImdbUrl == original.ImdbUrl
                    && item.TvdbId == original.TvdbId && item.WikidataId == original.WikidataId) continue;
                var metadata = current.Metadata;
                if (kind == CalendarCacheMaintenanceKind.ExternalIds)
                {
                    metadata ??= new ItemEnrichmentMetadata();
                    var imdbId = MaintenanceDelta(metadata.ImdbId, firstRead.Metadata?.ImdbId, original.ImdbId, item.ImdbId);
                    metadata = metadata with
                    {
                        ImdbId = imdbId,
                        ImdbUrl = imdbId == item.ImdbId
                            ? MaintenanceDelta(metadata.ImdbUrl, firstRead.Metadata?.ImdbUrl, original.ImdbUrl, item.ImdbUrl) : metadata.ImdbUrl,
                        TvdbId = MaintenanceDelta(metadata.TvdbId, firstRead.Metadata?.TvdbId, original.TvdbId, item.TvdbId),
                        WikidataId = MaintenanceDelta(metadata.WikidataId, firstRead.Metadata?.WikidataId, original.WikidataId, item.WikidataId)
                    };
                }
                var proposedRatings = ItemEnrichmentRatings.FromItem(item);
                var ratings = current.Ratings;
                var ratingsClock = current.Record.RatingsFetchedUtc;
                var scoresChanged = item.ImdbScore != original.ImdbScore || item.ImdbVoteCount != original.ImdbVoteCount
                    || item.RottenTomatoesScore != original.RottenTomatoesScore || item.RottenTomatoesAudienceScore != original.RottenTomatoesAudienceScore
                    || item.MetacriticScore != original.MetacriticScore;
                if (kind == CalendarCacheMaintenanceKind.Scores && scoresChanged && proposedRatings.MatchesMetadata(metadata))
                {
                    var baseline = current.Ratings?.MatchesMetadata(metadata) == true ? current.Ratings
                        : new ItemEnrichmentRatings(null, null, null, null, null, item.ImdbId, item.WikidataId, item.Title, item.PremiereDate.Year);
                    var tomatoesMatch = proposedRatings.MatchesTomatoes(baseline.WikidataId, baseline.Title, baseline.Year);
                    ratings = baseline with
                    {
                        ImdbScore = MaintenanceDelta(baseline.ImdbScore, firstRead.Ratings?.ImdbScore, original.ImdbScore, item.ImdbScore),
                        ImdbVoteCount = MaintenanceDelta(baseline.ImdbVoteCount, firstRead.Ratings?.ImdbVoteCount, original.ImdbVoteCount, item.ImdbVoteCount),
                        RottenTomatoesScore = tomatoesMatch ? MaintenanceDelta(baseline.RottenTomatoesScore, firstRead.Ratings?.RottenTomatoesScore,
                            original.RottenTomatoesScore, item.RottenTomatoesScore) : baseline.RottenTomatoesScore,
                        RottenTomatoesAudienceScore = tomatoesMatch ? MaintenanceDelta(baseline.RottenTomatoesAudienceScore, firstRead.Ratings?.RottenTomatoesAudienceScore,
                            original.RottenTomatoesAudienceScore, item.RottenTomatoesAudienceScore) : baseline.RottenTomatoesAudienceScore,
                        MetacriticScore = MaintenanceDelta(baseline.MetacriticScore, firstRead.Ratings?.MetacriticScore, original.MetacriticScore, item.MetacriticScore),
                        FreshnessVerified = false
                    };
                    if (ratings with { FreshnessVerified = baseline.FreshnessVerified } == baseline) ratings = baseline;
                    if (item.ImdbScore is not null || item.ImdbVoteCount is not null || item.RottenTomatoesScore is not null
                        || item.RottenTomatoesAudienceScore is not null || item.MetacriticScore is not null)
                        ratingsClock ??= DateTimeOffset.MinValue; // Data imported by maintenance; freshness is unverified.
                }
                else if (kind == CalendarCacheMaintenanceKind.ExternalIds && ratings?.MatchesMetadata(metadata) == false)
                {
                    ratings = new ItemEnrichmentRatings(null, null, null, null, null, metadata?.ImdbId, metadata?.WikidataId,
                        item.Title, item.PremiereDate.Year, FreshnessVerified: false);
                    ratingsClock ??= DateTimeOffset.MinValue;
                }
                var metadataChanged = metadata != current.Metadata;
                var ratingsChanged = ratings != current.Ratings;
                if (!metadataChanged && !ratingsChanged) continue;
                var metadataClock = metadataChanged ? current.Record.MetadataFetchedUtc ?? DateTimeOffset.MinValue : current.Record.MetadataFetchedUtc;
                _records[identity] = current with { Metadata = metadata, Ratings = ratings, Record = current.Record with
                    { MetadataFetchedUtc = metadataClock, RatingsFetchedUtc = ratingsClock } };
                _completed.TryRemove(identity, out _);
                var write = new ItemEnrichmentWrite(identity, current.Record.Generation,
                    metadataChanged ? JsonSerializer.Serialize(metadata) : null,
                    metadataChanged ? metadataClock : null,
                    ratingsChanged ? JsonSerializer.Serialize(ratings) : null, ratingsChanged ? ratingsClock : null,
                    metadata?.TvmazeShowId, metadata?.TmdbVotes?.TmdbObservedUtc, current.Record.MetadataRevision)
                { RatingsOrigin = current.RatingsReadOrigin };
                _writes.AddOrUpdate(identity, write, (_, previous) => previous.ExpectedMetadataRevision != write.ExpectedMetadataRevision ? write : write with
                {
                    MetadataJson = write.MetadataJson ?? previous.MetadataJson, MetadataFetchedUtc = write.MetadataFetchedUtc ?? previous.MetadataFetchedUtc,
                    RatingsJson = write.RatingsJson ?? previous.RatingsJson, RatingsFetchedUtc = write.RatingsFetchedUtc ?? previous.RatingsFetchedUtc,
                    RatingsOrigin = write.RatingsJson is null ? previous.RatingsOrigin : write.RatingsOrigin,
                    ExpectedRatingsBaseline = write.RatingsJson is null ? previous.ExpectedRatingsBaseline : write.ExpectedRatingsBaseline,
                    CoreContinuation = write.MetadataJson is null ? previous.CoreContinuation : write.CoreContinuation,
                    FreshMovieCore = write.MetadataJson is null ? previous.FreshMovieCore : write.FreshMovieCore
                });
            }
        }

        private static T MaintenanceDelta<T>(T current, T firstRead, T original, T published) =>
            EqualityComparer<T>.Default.Equals(original, published) || !EqualityComparer<T>.Default.Equals(current, firstRead) ? current : published;

        public async Task PrepareAsync(IEnumerable<ItemEnrichmentIdentity> identities,
            IEnumerable<PremiereItem>? legacyItems, CancellationToken cancellationToken)
        {
            var preparedAt = Stopwatch.GetTimestamp();
            var requested = identities.Where(identity => identity.TmdbId > 0).Distinct().ToArray();
            if (requested.Length == 0) return;
            await _prepareGate.WaitAsync(cancellationToken);
            try
            {
                var missing = requested.Where(identity => !_records.ContainsKey(identity)).ToArray();
                if (missing.Length == 0) return;
                var readRecords = new Dictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>();
                IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord> stored = readRecords;
                var readStarted = Stopwatch.GetTimestamp();
                try
                {
                    if (owner._clock.GetUtcNow().UtcTicks < Volatile.Read(ref owner._storeRetryAfterTicks))
                        throw new ProviderRetryAfterException("item_cache", new DateTimeOffset(Volatile.Read(ref owner._storeRetryAfterTicks), TimeSpan.Zero));
                    foreach (var batch in missing.Chunk(250))
                        foreach (var pair in await owner._store.ReadManyAsync(partition, batch, true, cancellationToken))
                            readRecords[pair.Key] = pair.Value;
                    Interlocked.Exchange(ref owner._storeRetryAfterTicks, 0);
                    owner._telemetry.RecordDiagnostic("item_cache.read", details: new() { Count = stored.Count, Total = missing.Length,
                        Outcome = "success", DurationMilliseconds = Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds });
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    DeferStorage(ex);
                    owner._telemetry.RecordDiagnostic("item_cache.read", details: new() { Total = missing.Length, Outcome = "failed",
                        DurationMilliseconds = Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds });
                    stored = readRecords;
                }
                var seeds = (legacyItems ?? []).Where(item => item.TmdbId > 0
                        && item.VerificationState == PremiereVerificationState.Verified
                        && item.LastUpdatedUtc != default)
                    .GroupBy(item => new ItemEnrichmentIdentity(item.MediaType, item.TmdbId))
                    .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.LastUpdatedUtc).First());
                foreach (var identity in missing)
                {
                    // Failed DB reservations must never produce a write with an invented generation.
                    var reserved = stored.TryGetValue(identity, out var record);
                    if (reserved && record is not null) RememberAcceptedTomatoes(record);
                    record ??= new(identity, null, null, null, null, owner._clock.GetUtcNow(), -1, null, null);
                    var snapshot = new ItemEnrichmentSnapshot(record, Deserialize<ItemEnrichmentMetadata>(record.MetadataJson),
                        Deserialize<ItemEnrichmentRatings>(record.RatingsJson));
                    if (!reserved && _storageUnavailable && seeds.TryGetValue(identity, out var retained))
                    {
                        snapshot = snapshot with { Metadata = ItemEnrichmentMetadata.FromItem(retained), Ratings = ItemEnrichmentRatings.FromItem(retained) };
                        _discoveryChanged[identity.MediaType] = 0;
                    }
                    if (allowLegacyImport && reserved && record.Generation == 0 && record.MetadataInvalidatedUtc is null
                        && record.MetadataJson is null && record.RatingsJson is null
                        && record.MetadataFetchedUtc is null && record.RatingsFetchedUtc is null
                        && seeds.TryGetValue(identity, out var seed)
                        && (seed.RuntimeMinutes is > 0 || !string.IsNullOrWhiteSpace(seed.ImdbId)
                            || seed.Genres.Length > 0 || seed.Sources.Any(source => !string.Equals(source.Kind, "schedule", StringComparison.OrdinalIgnoreCase))))
                    {
                        var metadata = snapshot.Metadata ?? ItemEnrichmentMetadata.FromItem(seed);
                        var hasRatings = seed.ImdbScore is not null || seed.ImdbVoteCount is not null
                            || seed.RottenTomatoesScore is not null || seed.RottenTomatoesAudienceScore is not null || seed.MetacriticScore is not null;
                        var ratings = hasRatings ? ItemEnrichmentRatings.FromItem(seed) : null;
                        var write = new ItemEnrichmentWrite(identity, record.Generation,
                            snapshot.Metadata is null ? JsonSerializer.Serialize(metadata) : null,
                            snapshot.Metadata is null ? seed.LastUpdatedUtc : null,
                            ratings is not null ? JsonSerializer.Serialize(ratings) : null,
                            ratings is not null ? seed.LastUpdatedUtc : null, metadata.TvmazeShowId,
                            metadata.TmdbVotes?.TmdbObservedUtc, record.MetadataRevision)
                        { RatingsOrigin = snapshot.RatingsReadOrigin };
                        _writes[identity] = write;
                        snapshot = new(record with
                        {
                            MetadataFetchedUtc = record.MetadataFetchedUtc ?? seed.LastUpdatedUtc,
                            RatingsFetchedUtc = ratings is not null ? seed.LastUpdatedUtc : null
                        }, metadata, ratings) { RatingsReadOrigin = snapshot.RatingsReadOrigin };
                    }
                    _records[identity] = snapshot;
                }
            }
            finally
            {
                _prepareGate.Release();
                owner._telemetry.RecordDiagnostic("item_cache.prepare", details: new() { Count = requested.Length, Outcome = "complete",
                    DurationMilliseconds = Stopwatch.GetElapsedTime(preparedAt).TotalMilliseconds });
            }
        }

        private void DeferStorage(Exception exception)
        {
            _storageUnavailable = true;
            if (exception is ItemEnrichmentStoreBusyException or ItemEnrichmentStoreOperationTimeoutException)
                _storageRetryAtUtc = Now.AddSeconds(5);
            else
            {
                if (exception is not ProviderRetryAfterException)
                    Interlocked.Exchange(ref owner._storeRetryAfterTicks, owner._clock.GetUtcNow().AddSeconds(30).UtcTicks);
                _storageRetryAtUtc = new DateTimeOffset(Volatile.Read(ref owner._storeRetryAfterTicks), TimeSpan.Zero);
            }
            owner._logger.LogWarning(exception, "Item enrichment storage unavailable; preserving cached rows and deferring provider enrichment.");
        }

        public async Task<ItemEnrichmentSnapshot> GetAsync(ItemEnrichmentIdentity identity, bool forceRefresh,
            Func<ItemEnrichmentSnapshot, bool, bool, CancellationToken, Task<ItemEnrichmentRefresh>> refresh,
            CancellationToken cancellationToken, bool ratingsOnly = false, PremiereItem? ratingsInputs = null, bool requireDataset = false, bool metadataOnly = false,
            string? refreshVariant = null)
        {
            var decisionStarted = Stopwatch.GetTimestamp();
            if (_storageUnavailable)
                throw new ProviderRetryAfterException("item_cache", _storageRetryAtUtc ?? Now.AddSeconds(5));
            if (!ratingsOnly && ratingsInputs is not null) ObserveTmdbVotes(identity, ratingsInputs);
            bool CanReuseCompleted(ItemEnrichmentSnapshot snapshot) => metadataOnly ? owner.MetadataFresh(snapshot)
                : (ratingsOnly || snapshot.Metadata?.CoreMetadataOnly != true && !MetadataRefreshNeeded(identity))
                    && !RatingsRefreshNeeded(identity, ratingsInputs, requireDataset)
                    && ((!requireDataset || !DatasetRefreshNeeded(identity))
                        && (ratingsInputs is null || snapshot.Ratings?.Matches(snapshot.Metadata?.Apply(ratingsInputs) ?? ratingsInputs) == true)
                        || !RatingsRetryAllowed(identity, ratingsInputs));
            if (_completed.TryGetValue(identity, out var completed) && CanReuseCompleted(completed))
            {
                owner._telemetry.RecordDiagnostic("item_cache.item", details: new() { ItemId = identity.TmdbId, MediaType = identity.MediaType.ToString(),
                    Stage = identity.MediaType.ToString(), Outcome = "session_hit",
                    DurationMilliseconds = Stopwatch.GetElapsedTime(decisionStarted).TotalMilliseconds });
                return completed;
            }
            var current = Find(identity) ?? throw new InvalidOperationException("Item identities must be reserved in a batch before enrichment.");
            var metadataDue = !ratingsOnly && (forceRefresh || !owner.MetadataFresh(current) && RetryAllowed(identity));
            var availabilityDue = !ratingsOnly && !metadataOnly && current.Metadata is { AvailabilityComplete: false }
                && (forceRefresh || AvailabilityRefreshNeeded(identity));
            var optionalMetadataDue = !ratingsOnly && !metadataOnly && (current.Metadata?.CoreMetadataOnly == true
                || current.Metadata is { OptionalMetadataComplete: false } && (forceRefresh || OptionalMetadataRefreshNeeded(identity)));
            var ratingsDue = !metadataOnly && (forceRefresh || RatingsRefreshNeeded(identity, ratingsInputs, requireDataset));
            var coreContinuation = !forceRefresh && !metadataDue && !metadataOnly && !ratingsOnly
                && current.Metadata is { CoreMetadataOnly: true } && current.Record.Generation >= 0
                ? new ItemEnrichmentCoreContinuation(current.Record.Generation, current.Record.MetadataRevision,
                    JsonSerializer.Serialize(current.Metadata), current.Record.MetadataFetchedUtc, current.Record.MetadataInvalidatedUtc)
                : null;
            owner._telemetry.RecordDiagnostic("item_cache.item", details: new() { ItemId = identity.TmdbId, MediaType = identity.MediaType.ToString(), Stage = identity.MediaType.ToString(),
                Outcome = forceRefresh ? "forced" : metadataDue ? "metadata_due" : ratingsDue ? "ratings_due" : availabilityDue ? "availability_due"
                    : optionalMetadataDue ? "optional_metadata_due" : "hit",
                DurationMilliseconds = Stopwatch.GetElapsedTime(decisionStarted).TotalMilliseconds });
            void RecordDecision(string component, bool due, bool fresh, bool retryAllowed)
                => owner._telemetry.RecordDiagnostic("item_cache.decision", details: new() { ItemId = identity.TmdbId, MediaType = identity.MediaType.ToString(),
                    Stage = component, Outcome = due ? "refresh" : !fresh && !retryAllowed ? "cooldown" : "reuse",
                    Reason = forceRefresh ? "forced" : fresh ? "fresh" : retryAllowed ? "stale_or_missing" : "failure_retry_pending",
                    DurationMilliseconds = Stopwatch.GetElapsedTime(decisionStarted).TotalMilliseconds });
            if (!ratingsOnly)
            {
                RecordDecision("metadata", metadataDue, owner.MetadataFresh(current), RetryAllowed(identity));
                if (!metadataOnly)
                {
                    RecordDecision("availability", availabilityDue || metadataDue, current.Metadata?.AvailabilityComplete != false,
                        RetryAllowed(owner._availabilityRetryAfter, RetryKey(identity)));
                    RecordDecision("tvmaze", metadataDue || TvmazeRefreshNeeded(identity), current.Metadata?.TvmazeComplete != false,
                        ComponentRetryAllowed(identity, "tvmaze"));
                    RecordDecision("artwork", metadataDue || ArtworkRefreshNeeded(identity), current.Metadata?.ArtworkComplete != false,
                        ComponentRetryAllowed(identity, "artwork"));
                }
            }
            if (!metadataOnly) RecordDecision("ratings", ratingsDue, owner.RatingsFresh(current), RatingsRetryAllowed(identity, ratingsInputs));
            if (!metadataDue && !ratingsDue && !availabilityDue && !optionalMetadataDue) return current;
            var ratingsBinding = metadataOnly ? "" : RatingsComponentIdentity(identity, ratingsInputs);
            var flightKey = $"{partition}:{identity.MediaType}:{identity.TmdbId}:{current.Record.Generation}:{current.Record.MetadataRevision}:{metadataDue}:{ratingsDue}:{availabilityDue}:{optionalMetadataDue}:{forceRefresh}:{metadataOnly}:{ratingsBinding}:{refreshVariant}";
            var proposalCohort = metadataOnly && metadataDue && !forceRefresh
                ? JoinCoreProposalCohort(flightKey, identity, current.Record.Generation)
                : coreContinuation is not null ? FindCoreProposalCohort(identity, current.Record.Generation) : null;
            var observationStarted = _providerObservationStarts.GetOrAdd((identity, current.Record.Generation), current.Record.LastSeenUtc);
            var flightHash = owner._telemetry.DiagnosticsHealth.Enabled
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(flightKey)))[..20].ToLowerInvariant() : null;
            void TraceFlight(string outcome)
            {
                if (flightHash is null) return;
                owner._telemetry.RecordDiagnostic("item_cache.flight", details: new()
                {
                    ItemId = identity.TmdbId, MediaType = identity.MediaType.ToString(),
                    CacheKey = flightHash, Stage = metadataOnly ? "core" : "complete", Outcome = outcome,
                    Sequence = current.Record.MetadataRevision, Count = (int)Math.Clamp(current.Record.Generation, 0, int.MaxValue)
                });
            }
            TraceFlight("requested");
            if (!ratingsOnly && identity.MediaType == PremiereMediaType.Series && current.Record.TvmazeShowId is null
                && (metadataDue || TvmazeRefreshNeeded(identity)))
            {
                var reservation = _lookupReservations.GetOrAdd(identity,
                    key => new(() => owner._store.ReserveTvmazeLookupAsync(partition, key, LifetimeToken)));
                try { await reservation.Value.WaitAsync(cancellationToken); }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested && !LifetimeToken.IsCancellationRequested
                    && (exception is IOException or TimeoutException or ProviderRetryAfterException || DatabaseConnectionFactory.IsTransient(exception)))
                {
                    ((ICollection<KeyValuePair<ItemEnrichmentIdentity, Lazy<Task<IAsyncDisposable>>>>)_lookupReservations)
                        .Remove(new(identity, reservation));
                    DeferStorage(exception);
                    throw new ProviderRetryAfterException("item_cache", _storageRetryAtUtc ?? Now.AddSeconds(5));
                }
            }
            var retainedProposal = !metadataOnly ? proposalCohort?.Find(flightKey) : null;
            if (retainedProposal is not null) TraceFlight("proposal_reused");
            var outcome = retainedProposal ?? await owner._flights.RunAsync(flightKey, async token =>
            {
                // A caller may have missed a proposal just before its producer
                // stored it. Check again under the ordinary shared flight before
                // spending provider work; running-task cancellation stays there.
                if (!metadataOnly && proposalCohort?.Find(flightKey) is { } completedProposal)
                {
                    TraceFlight("proposal_reused");
                    return completedProposal;
                }
                TraceFlight("produced");
                if (_completed.TryGetValue(identity, out var existing) && CanReuseCompleted(existing)) return existing;
                // Reservation precedes every provider branch, including debut-date
                // validation. An older overlapping request cannot win by finishing last.
                // A core flush may read its accepted row and advance LastSeenUtc
                // before optional work starts. Both phases still belong to the
                // original provider observation for this reserved generation.
                var started = observationStarted;
                var refreshStarted = Stopwatch.GetTimestamp();
                var result = await refresh(current, metadataDue, ratingsDue, token);
                if (result.MetadataFailed)
                {
                    owner._metadataRetryAfter[$"{RetryKey(identity)}:{current.Record.Generation}"] = owner._clock.GetUtcNow().AddMinutes(Math.Max(1, owner._options.MetadataFailureRetryMinutes));
                    if (owner._metadataRetryAfter.Count > 8192)
                        foreach (var entry in owner._metadataRetryAfter.OrderBy(entry => entry.Value).Take(owner._metadataRetryAfter.Count - 8192))
                            owner._metadataRetryAfter.TryRemove(entry.Key, out _);
                }
                else if (result.Metadata is not null && result.RefreshMetadataClock)
                    owner._metadataRetryAfter.TryRemove($"{RetryKey(identity)}:{current.Record.Generation}", out _);
                void UpdateOptionalRetry(ConcurrentDictionary<string, DateTimeOffset> retries, bool failed, bool complete)
                {
                    if (failed) retries[RetryKey(identity)] = owner._clock.GetUtcNow().AddMinutes(Math.Max(1, owner._options.MetadataFailureRetryMinutes));
                    else if (complete) retries.TryRemove(RetryKey(identity), out _);
                    if (retries.Count > 8192)
                        foreach (var entry in retries.OrderBy(entry => entry.Value).Take(retries.Count - 8192)) retries.TryRemove(entry.Key, out _);
                }
                UpdateOptionalRetry(owner._availabilityRetryAfter, result.AvailabilityFailed, result.Metadata?.AvailabilityComplete == true);
                UpdateOptionalRetry(owner._optionalMetadataRetryAfter, result.OptionalMetadataFailed, result.Metadata?.OptionalMetadataComplete == true);
                var metadata = result.Metadata is null ? current.Metadata : result.Metadata with
                {
                    TmdbVotes = result.RefreshMetadataClock
                        ? ItemEnrichmentTmdbObservation.Observe(current.Metadata?.TmdbVotes,
                            result.Metadata.TmdbVotes?.TmdbScore, result.Metadata.TmdbVotes?.TmdbVoteCount, started)
                        : result.Metadata.TmdbVotes ?? current.Metadata?.TmdbVotes
                };
                var acceptedMetadata = metadata;
                var acceptedRatings = result.Ratings?.MatchesMetadata(acceptedMetadata) == true ? result.Ratings : null;
                var ratings = acceptedRatings ?? current.Ratings;
                if (acceptedRatings is not null)
                {
                    var changes = new[] { ratings?.ImdbScore != current.Ratings?.ImdbScore,
                        ratings?.ImdbVoteCount != current.Ratings?.ImdbVoteCount,
                        ratings?.RottenTomatoesScore != current.Ratings?.RottenTomatoesScore,
                        ratings?.RottenTomatoesAudienceScore != current.Ratings?.RottenTomatoesAudienceScore,
                        ratings?.MetacriticScore != current.Ratings?.MetacriticScore }.Count(changed => changed);
                    owner._telemetry.RecordDiagnostic("item_cache.result", details: new() { ItemId = identity.TmdbId, MediaType = identity.MediaType.ToString(),
                        Stage = "ratings", Outcome = changes > 0 ? "proposed_values_changed" : "proposed_values_unchanged", Count = changes,
                        DurationMilliseconds = Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds });
                }
                var record = current.Record with
                {
                    MetadataFetchedUtc = result.Metadata is null || !result.RefreshMetadataClock ? current.Record.MetadataFetchedUtc : started,
                    RatingsFetchedUtc = acceptedRatings is null ? current.Record.RatingsFetchedUtc
                        : result.RefreshRatingsClock ? started : current.Record.RatingsFetchedUtc ?? DateTimeOffset.MinValue,
                    TmdbObservedUtc = metadata?.TmdbVotes?.TmdbObservedUtc
                };
                var proposal = new ItemEnrichmentSnapshot(record, metadata, ratings)
                {
                    RatingsReadOrigin = current.RatingsReadOrigin, PreserveRatings = current.PreserveRatings,
                    FreshMovieCore = metadataDue && result.RefreshMetadataClock && !result.MetadataFailed
                        && identity.MediaType == PremiereMediaType.Movie && result.Metadata is { CoreMetadataOnly: false }
                        && result.FreshMovieCoreJson is { } rawCore
                        ? new(current.Record.Generation, current.Record.MetadataRevision, rawCore, started,
                            current.Record.MetadataInvalidatedUtc, current.RatingsReadBaseline.Json, current.RatingsReadBaseline.FetchedUtc) : null
                };
                if (!metadataOnly && coreContinuation is not null
                    && !result.MetadataFailed && !result.RatingsFailed && !result.AvailabilityFailed && !result.OptionalMetadataFailed
                    && proposal.Metadata is { CoreMetadataOnly: false })
                    proposalCohort?.Remember(flightKey, proposal);
                return proposal;
            }, cancellationToken);
            if (current.Metadata?.TmdbVotes?.TmdbObservedUtc is { } ownObserved
                && (outcome.Metadata?.TmdbVotes?.TmdbObservedUtc is null || ownObserved > outcome.Metadata.TmdbVotes.TmdbObservedUtc)
                && outcome.Metadata is { } outcomeMetadata)
                outcome = outcome with { Metadata = outcomeMetadata with { TmdbVotes = current.Metadata.TmdbVotes },
                    Record = outcome.Record with { TmdbObservedUtc = ownObserved } };
            if (!ratingsOnly && ratingsInputs is not null && outcome.Metadata is { } observedMetadata)
            {
                var observed = ItemEnrichmentTmdbObservation.Observe(observedMetadata.TmdbVotes,
                    ratingsInputs.TmdbScore, ratingsInputs.TmdbVoteCount, current.Record.LastSeenUtc);
                outcome = outcome with { Metadata = observedMetadata with { TmdbVotes = observed },
                    Record = outcome.Record with { TmdbObservedUtc = observed?.TmdbObservedUtc } };
            }
            // Retain accepted ratings without discarding the producer's metadata
            // or the revision/proofs required to publish that metadata safely.
            outcome = _records.AddOrUpdate(identity, outcome, (_, existing) => PreserveAcceptedRatings(outcome, existing));
            if (!ratingsOnly && !metadataOnly) outcome = _completed.AddOrUpdate(identity, outcome, (_, existing) =>
                PreserveAcceptedRatings(outcome, _records.GetValueOrDefault(identity) ?? existing));
            if (outcome.PreserveRatings) DropPendingRatings(identity);
            if (current.Record.Generation >= 0)
            {
                var metadataChanged = outcome.Record.MetadataFetchedUtc != current.Record.MetadataFetchedUtc
                    || JsonSerializer.Serialize(outcome.Metadata) != JsonSerializer.Serialize(current.Metadata);
                var ratingsChanged = !outcome.PreserveRatings && (outcome.Record.RatingsFetchedUtc != current.Record.RatingsFetchedUtc
                    || JsonSerializer.Serialize(outcome.Ratings) != JsonSerializer.Serialize(current.Ratings));
                if (ratingsChanged)
                    _ratingChanges.AddOrUpdate(identity, new RatingProjectionChange(current.Ratings, outcome.Ratings),
                        (_, previous) => new RatingProjectionChange(previous.Before, outcome.Ratings));
                if (metadataChanged || ratingsChanged)
                {
                    var write = new ItemEnrichmentWrite(identity, current.Record.Generation,
                        metadataChanged ? JsonSerializer.Serialize(outcome.Metadata) : null,
                        metadataChanged ? outcome.Record.MetadataFetchedUtc : null,
                        ratingsChanged ? JsonSerializer.Serialize(outcome.Ratings) : null,
                        ratingsChanged ? outcome.Record.RatingsFetchedUtc : null, outcome.Metadata?.TvmazeShowId,
                        outcome.Metadata?.TmdbVotes?.TmdbObservedUtc, current.Record.MetadataRevision)
                    { CoreContinuation = metadataChanged ? coreContinuation : null,
                        FreshMovieCore = metadataChanged ? outcome.FreshMovieCore : null,
                        RatingsOrigin = metadataChanged || ratingsChanged ? outcome.RatingsReadOrigin : null,
                        ExpectedRatingsBaseline = (metadataChanged || ratingsChanged) && (outcome.RatingsReadOrigin.Generation != current.Record.Generation
                            || outcome.RatingsReadOrigin.MetadataRevision != current.Record.MetadataRevision)
                            ? outcome.RatingsReadBaseline : null };
                    _writes.AddOrUpdate(identity, write,
                        (_, previous) => previous.ExpectedMetadataRevision != current.Record.MetadataRevision ? write : new(identity, current.Record.Generation,
                            metadataChanged ? JsonSerializer.Serialize(outcome.Metadata) : previous.MetadataJson,
                            metadataChanged ? outcome.Record.MetadataFetchedUtc : previous.MetadataFetchedUtc,
                            outcome.PreserveRatings ? null : ratingsChanged ? JsonSerializer.Serialize(outcome.Ratings) : previous.RatingsJson,
                            outcome.PreserveRatings ? null : ratingsChanged ? outcome.Record.RatingsFetchedUtc : previous.RatingsFetchedUtc,
                            outcome.Metadata?.TvmazeShowId ?? previous.TvmazeShowId,
                            outcome.Metadata?.TmdbVotes?.TmdbObservedUtc ?? previous.TmdbObservedUtc, current.Record.MetadataRevision)
                        { CoreContinuation = metadataChanged ? coreContinuation : previous.CoreContinuation,
                            FreshMovieCore = metadataChanged ? outcome.FreshMovieCore : previous.FreshMovieCore,
                            RatingsOrigin = outcome.PreserveRatings || ratingsChanged ? outcome.RatingsReadOrigin
                                : previous.RatingsJson is not null ? previous.RatingsOrigin : write.RatingsOrigin,
                            ExpectedRatingsBaseline = outcome.PreserveRatings || ratingsChanged || previous.RatingsJson is null
                                ? write.ExpectedRatingsBaseline : previous.ExpectedRatingsBaseline });
                }
            }
            // Accept may have established preservation while this producer was
            // queueing. Either side removes stale pending ratings, without an IO lock.
            if (Find(identity) is { PreserveRatings: true })
            {
                outcome = _records.AddOrUpdate(identity, outcome, (_, existing) => PreserveAcceptedRatings(outcome, existing));
                if (!ratingsOnly && !metadataOnly) outcome = _completed.AddOrUpdate(identity, outcome, (_, existing) =>
                    PreserveAcceptedRatings(outcome, _records.GetValueOrDefault(identity) ?? existing));
                DropPendingRatings(identity);
            }
            return outcome;
        }

        private static ItemEnrichmentSnapshot PreserveAcceptedRatings(ItemEnrichmentSnapshot incoming, ItemEnrichmentSnapshot accepted)
        {
            if (!accepted.PreserveRatings) return incoming.PreserveRatings ? accepted : incoming;
            var compatible = incoming.Record.Generation == accepted.Record.Generation
                && string.Equals(incoming.Metadata?.ImdbId, accepted.Metadata?.ImdbId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(incoming.Metadata?.WikidataId, accepted.Metadata?.WikidataId, StringComparison.OrdinalIgnoreCase)
                && incoming.Metadata?.TvdbId == accepted.Metadata?.TvdbId
                && string.Equals(incoming.Metadata?.Title, accepted.Metadata?.Title, StringComparison.OrdinalIgnoreCase);
            if (!compatible) return accepted;
            return incoming with
            {
                Ratings = accepted.Ratings, RatingsReadOrigin = accepted.RatingsReadOrigin, PreserveRatings = true,
                Record = incoming.Record with { RatingsJson = accepted.Record.RatingsJson, RatingsFetchedUtc = accepted.Record.RatingsFetchedUtc }
            };
        }

        private void DropPendingRatings(ItemEnrichmentIdentity identity)
        {
            _ratingChanges.TryRemove(identity, out _);
            while (_writes.TryGetValue(identity, out var pending) && pending.RatingsJson is not null)
            {
                if (pending.MetadataJson is null)
                {
                    if (((ICollection<KeyValuePair<ItemEnrichmentIdentity, ItemEnrichmentWrite>>)_writes).Remove(new(identity, pending))) return;
                }
                else if (_writes.TryUpdate(identity, pending with { RatingsJson = null, RatingsFetchedUtc = null }, pending)) return;
            }
        }

        private ItemEnrichmentWrite TagLookupWrite(ItemEnrichmentWrite write)
        {
            if (write.MetadataJson is not null && _lookupReservations.TryGetValue(write.Identity, out var reservation)
                && reservation.IsValueCreated && reservation.Value.IsCompletedSuccessfully
                && reservation.Value.Result is IItemEnrichmentLookupLease lease)
                return write with { ExpectedTvmazeReservationToken = lease.Token };
            return write;
        }

        private ItemEnrichmentWrite ContinueAcceptedCore(ItemEnrichmentWrite write)
        {
            if (write.MetadataJson is not null && write.CoreContinuation is { } baseline
                && write.ExpectedGeneration == baseline.Generation && write.ExpectedMetadataRevision == baseline.MetadataRevision
                && write.MetadataFetchedUtc == baseline.MetadataFetchedUtc
                && _acceptedCorePredecessors.TryGetValue(write.Identity, out var predecessor) && predecessor.Baseline == baseline)
                return write with
                {
                    ExpectedMetadataRevision = predecessor.AcceptedRevision, ExpectedRatingsBaseline = write.RatingsOrigin?.Baseline,
                    RatingsJson = write.RatingsOrigin is null ? null : write.RatingsJson,
                    RatingsFetchedUtc = write.RatingsOrigin is null ? null : write.RatingsFetchedUtc
                };
            return write;
        }

        private void RememberCorePredecessor(ItemEnrichmentWrite write, ItemEnrichmentRecord accepted)
        {
            if (write.ExpectedMetadataRevision is { } revision && accepted.Generation == write.ExpectedGeneration
                && accepted.MetadataRevision == revision + 1 && accepted.MetadataJson == write.MetadataJson
                && accepted.MetadataFetchedUtc == write.MetadataFetchedUtc
                && Deserialize<ItemEnrichmentMetadata>(accepted.MetadataJson) is { CoreMetadataOnly: true })
                _acceptedCorePredecessors[write.Identity] = new(new(accepted.Generation, revision,
                    accepted.MetadataJson!, accepted.MetadataFetchedUtc, accepted.MetadataInvalidatedUtc), accepted.MetadataRevision);
        }

        private sealed record AcceptedCorePredecessor(ItemEnrichmentCoreContinuation Baseline, long AcceptedRevision);

        private static ItemEnrichmentWrite? ContinueFreshMovieCore(ItemEnrichmentWrite write, ItemEnrichmentRecord accepted)
        {
            if (write.FreshMovieCore is not { } proof || write.Identity.MediaType != PremiereMediaType.Movie
                || write.ExpectedGeneration != proof.Generation || accepted.Generation != proof.Generation
                || write.ExpectedMetadataRevision != proof.MetadataRevision || accepted.MetadataRevision != proof.MetadataRevision + 1
                || write.MetadataFetchedUtc != proof.MetadataFetchedUtc || accepted.MetadataFetchedUtc is not { } acceptedClock
                || acceptedClock > proof.MetadataFetchedUtc || accepted.MetadataInvalidatedUtc != proof.MetadataInvalidatedUtc
                || proof.MetadataInvalidatedUtc is { } invalidated && (acceptedClock < invalidated || proof.MetadataFetchedUtc < invalidated)
                || Deserialize<ItemEnrichmentMetadata>(proof.MetadataJson) is not { CoreMetadataOnly: true } rawCore
                || Deserialize<ItemEnrichmentMetadata>(accepted.MetadataJson) is not { CoreMetadataOnly: true } acceptedCore
                || Deserialize<ItemEnrichmentMetadata>(write.MetadataJson) is not { CoreMetadataOnly: false } complete
                || CoreProofJson(rawCore) != CoreProofJson(acceptedCore)) return null;
            // Discovery observations have an independent monotonic clock. Keep
            // the winner's newer observation while retaining the full original
            // provider proposal and its successful optional clocks/ownership.
            if (accepted.TmdbObservedUtc is { } observed
                && (write.TmdbObservedUtc is null || observed >= write.TmdbObservedUtc))
                complete = complete with { TmdbVotes = acceptedCore.TmdbVotes };
            var origin = write.RatingsOrigin ?? new ItemEnrichmentRatingsOrigin(proof.Generation, proof.MetadataRevision,
                new(proof.RatingsJson, proof.RatingsFetchedUtc));
            return write with
            {
                MetadataJson = JsonSerializer.Serialize(complete), TmdbObservedUtc = complete.TmdbVotes?.TmdbObservedUtc,
                ExpectedMetadataRevision = accepted.MetadataRevision, FreshMovieCore = null, CoreContinuation = null,
                ExpectedRatingsBaseline = origin.Baseline, RatingsOrigin = origin,
                RatingsJson = write.RatingsOrigin is null ? null : write.RatingsJson,
                RatingsFetchedUtc = write.RatingsOrigin is null ? null : write.RatingsFetchedUtc
            };
        }

        private static string CoreProofJson(ItemEnrichmentMetadata core) => JsonSerializer.Serialize(core with
        {
            TmdbVotes = null,
            // These clocks timestamp the same newly verified TMDb payload in
            // different sessions. Retained optional-provider clocks remain exact.
            ArtworkFetchedUtc = core is { ArtworkComplete: true, ImageSource: "TMDb poster" } ? null : core.ArtworkFetchedUtc,
            AvailabilityFetchedUtc = core is { AvailabilityComplete: true, AvailabilityOwnedByWatchmode: false }
                ? null : core.AvailabilityFetchedUtc
        });

        public async Task FlushAsync(CancellationToken cancellationToken, bool rejectGenerationConflicts = true)
        {
            await _flushGate.WaitAsync(cancellationToken);
            var started = Stopwatch.GetTimestamp();
            var count = 0;
            var outcome = "empty";
            var writes = new List<ItemEnrichmentWrite>();
            var ratingChanges = new Dictionary<ItemEnrichmentIdentity, RatingProjectionChange>();
            try
            {
                foreach (var identity in _writes.Keys)
                    if (_writes.TryRemove(identity, out var write))
                    {
                        writes.Add(TagLookupWrite(ContinueAcceptedCore(write)));
                        if (_ratingChanges.TryRemove(identity, out var change))
                        {
                            if (JsonSerializer.Serialize(change.Proposed) == write.RatingsJson) ratingChanges[identity] = change;
                            else _ratingChanges.TryAdd(identity, change);
                        }
                    }
                count = writes.Count;
                if (count == 0) return;
                // Production returns accepted winners from the same transaction as the write.
                var latest = await owner._store.WriteAcceptedAsync(partition, writes, cancellationToken);
                var retries = new List<ItemEnrichmentWrite>();
                for (var index = 0; index < writes.Count; index++)
                {
                    var write = writes[index];
                    if (!latest.TryGetValue(write.Identity, out var accepted) || accepted.Generation != write.ExpectedGeneration) continue;
                    if (ContinueFreshMovieCore(write, accepted) is { } fullRetry)
                    {
                        // Retry the already completed proposal exactly once using
                        // ordinary store CAS. Any intervening writer still wins.
                        writes[index] = fullRetry;
                        retries.Add(fullRetry);
                        continue;
                    }
                    if (write.TmdbObservedUtc is { } observed && (accepted.TmdbObservedUtc is null || observed > accepted.TmdbObservedUtc)
                        && Deserialize<ItemEnrichmentMetadata>(accepted.MetadataJson) is { } acceptedMetadata
                        && Deserialize<ItemEnrichmentMetadata>(write.MetadataJson)?.TmdbVotes is { } observation
                        && accepted.MetadataFetchedUtc is { } fetched)
                    {
                        // An observation may lose revision CAS to a field repair.
                        // Retry only its newer votes on top of that accepted repair.
                        var rebased = acceptedMetadata with { TmdbVotes = observation };
                        retries.Add(new(write.Identity, accepted.Generation, JsonSerializer.Serialize(rebased), fetched,
                            TvmazeShowId: rebased.TvmazeShowId, TmdbObservedUtc: observed, ExpectedMetadataRevision: accepted.MetadataRevision));
                    }
                }
                if (retries.Count > 0)
                {
                    retries = retries.Select(TagLookupWrite).ToList();
                    var retried = await owner._store.WriteAcceptedAsync(partition, retries, cancellationToken);
                    latest = latest.Concat(retried).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Last().Value);
                }
                var conflicted = false;
                foreach (var write in writes)
                {
                    if (!latest.TryGetValue(write.Identity, out var record))
                    {
                        conflicted = true;
                        owner._telemetry.RecordDiagnostic("item_cache.write.item", details: new() { ItemId = write.Identity.TmdbId, MediaType = write.Identity.MediaType.ToString(),
                            Stage = write.Identity.MediaType.ToString(), Outcome = "concurrency_rejected", Reason = "missing_record",
                            DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                        continue;
                    }
                    var previous = Find(write.Identity);
                    RememberCorePredecessor(write, record);
                    if (previous is not null && previous.Record.MetadataInvalidatedUtc != record.MetadataInvalidatedUtc)
                        _discoveryChanged[write.Identity.MediaType] = 0;
                    conflicted |= record.Generation != write.ExpectedGeneration;
                    if (record.Generation == write.ExpectedGeneration && record.MetadataJson == write.MetadataJson && record.MetadataFetchedUtc == write.MetadataFetchedUtc)
                        _selfAcceptedRevisions[write.Identity] = record.MetadataRevision;
                    var generationMatches = record.Generation == write.ExpectedGeneration;
                    var metadataAccepted = write.MetadataJson is null || generationMatches
                        && record.MetadataJson == write.MetadataJson && record.MetadataFetchedUtc == write.MetadataFetchedUtc;
                    var ratingsAccepted = write.RatingsJson is null || generationMatches
                        && record.RatingsJson == write.RatingsJson && record.RatingsFetchedUtc == write.RatingsFetchedUtc;
                    var ratingsMerged = false;
                    var ratingsReused = false;
                    var externalAdvanced = false;
                    var tomatoesAdvanced = false;
                    var datasetAdvanced = false;
                    var providerWrite = false;
                    var acceptedRatings = ItemEnrichmentRatingsMerger.ReadTracked(record.RatingsJson);
                    if (generationMatches && acceptedRatings is not null && ItemEnrichmentRatingsMerger.ReadTracked(write.RatingsJson) is { } submittedRatings)
                    {
                        var hasChange = ratingChanges.TryGetValue(write.Identity, out var change);
                        var before = change?.Before;
                        static bool Advanced(DateTimeOffset? before, DateTimeOffset? submitted)
                            => submitted is { } clock && (before is null || clock > before);
                        (bool Matches, bool Accepted, bool Proposed) ValidateProvider(string provider, string stage, bool identityMatches,
                            DateTimeOffset? beforeClock,
                            DateTimeOffset? submittedClock, DateTimeOffset? acceptedClock,
                            ItemEnrichmentRatingValues? submittedValues, ItemEnrichmentRatingValues? acceptedValues, bool requiredMatches)
                        {
                            var matches = identityMatches && submittedClock == acceptedClock && submittedValues == acceptedValues && requiredMatches;
                            var proposed = hasChange && Advanced(beforeClock, submittedClock);
                            var accepted = proposed && matches;
                            var newer = identityMatches && acceptedClock is not null && (submittedClock is null || acceptedClock > submittedClock);
                            owner._telemetry.RecordDiagnostic("item_cache.write.component", details: new()
                            {
                                ItemId = write.Identity.TmdbId, MediaType = write.Identity.MediaType.ToString(), Provider = provider, Stage = stage,
                                Outcome = accepted ? "accepted_projection" : newer ? "retained_newer" : matches ? "reused" : "concurrency_rejected",
                                Reason = !identityMatches ? "provider_identity_changed" : accepted ? "provider_component_current"
                                    : matches ? "provider_component_carried"
                                    : newer ? "newer_provider_clock" : "equal_or_rejected_provider_clock",
                                RequestedFetchedUtc = submittedClock, AcceptedFetchedUtc = acceptedClock,
                                DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
                            });
                            return (matches, accepted, proposed);
                        }
                        var externalAccepted = ValidateProvider("omdb", "external_ratings", submittedRatings.MatchesImdb(acceptedRatings.ImdbId),
                            before?.MatchesImdb(submittedRatings.ImdbId) == true ? before.ExternalFetchedUtc : null,
                            submittedRatings.ExternalFetchedUtc, acceptedRatings.ExternalFetchedUtc, submittedRatings.ExternalValues, acceptedRatings.ExternalValues,
                            submittedRatings.ExternalRequired == acceptedRatings.ExternalRequired);
                        var tomatoesAccepted = ValidateProvider("rottentomatoes", "tomatoes",
                            submittedRatings.MatchesTomatoes(acceptedRatings.WikidataId, acceptedRatings.Title, acceptedRatings.Year),
                            before?.MatchesTomatoes(submittedRatings.WikidataId, submittedRatings.Title, submittedRatings.Year) == true ? before.TomatoesFetchedUtc : null,
                            submittedRatings.TomatoesFetchedUtc, acceptedRatings.TomatoesFetchedUtc, submittedRatings.TomatoesValues, acceptedRatings.TomatoesValues,
                            submittedRatings.TomatoesRequired == acceptedRatings.TomatoesRequired);
                        static ItemEnrichmentRatingValues? DatasetValues(ItemEnrichmentDatasetRating? dataset)
                            => dataset is null ? null : new(ImdbScore: dataset.ImdbScore, ImdbVoteCount: dataset.ImdbVoteCount);
                        var datasetAccepted = ValidateProvider("imdb_dataset", "dataset", submittedRatings.Dataset is null
                            || string.Equals(submittedRatings.Dataset.ImdbId, acceptedRatings.Dataset?.ImdbId, StringComparison.OrdinalIgnoreCase),
                            string.Equals(before?.Dataset?.ImdbId, submittedRatings.Dataset?.ImdbId, StringComparison.OrdinalIgnoreCase) ? before?.Dataset?.FetchedUtc : null,
                            submittedRatings.Dataset?.FetchedUtc, acceptedRatings.Dataset?.FetchedUtc,
                            DatasetValues(submittedRatings.Dataset), DatasetValues(acceptedRatings.Dataset),
                            submittedRatings.DatasetRequired == acceptedRatings.DatasetRequired);
                        if (write.ExpectedRatingsJson is null && hasChange)
                        {
                            providerWrite = true;
                            externalAdvanced = externalAccepted.Accepted;
                            tomatoesAdvanced = tomatoesAccepted.Accepted;
                            datasetAdvanced = datasetAccepted.Accepted;
                            // The store normalizes the aggregate clock from the selected
                            // provider clocks. Only provider projections prove this write.
                            ratingsAccepted = externalAdvanced || tomatoesAdvanced || datasetAdvanced;
                            var matches = externalAccepted.Matches && tomatoesAccepted.Matches && datasetAccepted.Matches;
                            ratingsMerged = ratingsAccepted && !matches;
                            ratingsReused = !externalAccepted.Proposed && !tomatoesAccepted.Proposed && !datasetAccepted.Proposed;
                        }
                    }
                    void RecordComponentWrite(string component, bool accepted, bool merged = false, bool reused = false)
                        => owner._telemetry.RecordDiagnostic("item_cache.write.component", details: new() { ItemId = write.Identity.TmdbId, MediaType = write.Identity.MediaType.ToString(),
                            Stage = component, Outcome = merged ? "merged_projection" : reused ? "reused" : accepted ? "accepted_projection" : "concurrency_rejected",
                            Reason = !generationMatches ? "metadata_generation_changed" : merged ? "provider_components_merged"
                                : reused ? "no_provider_success_submitted"
                                : accepted ? null : component == "ratings" && write.ExpectedRatingsJson is not null
                                    ? "projection_baseline_or_revision_changed" : "component_clock_or_revision_changed",
                            Sequence = record.MetadataRevision, DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                    if (write.MetadataJson is not null) RecordComponentWrite("metadata", metadataAccepted);
                    if (write.RatingsJson is not null) RecordComponentWrite("ratings", ratingsAccepted, ratingsMerged, ratingsReused);
                    if (ratingsAccepted && ratingChanges.TryGetValue(write.Identity, out var ratingChange))
                    {
                        var before = ratingChange.Before;
                        var proposed = ratingChange.Proposed;
                        var accepted = acceptedRatings ?? Deserialize<ItemEnrichmentRatings>(record.RatingsJson);
                        static (bool Changed, bool Cleared) FieldChange<T>(bool owned, T? before, T? proposed, T? accepted) where T : struct
                        {
                            var changed = owned && Nullable.Equals(proposed, accepted) && !Nullable.Equals(before, accepted);
                            return (changed, changed && before.HasValue && !accepted.HasValue);
                        }
                        var imdbOwned = !providerWrite || externalAdvanced || datasetAdvanced;
                        var tomatoesOwned = !providerWrite || externalAdvanced || tomatoesAdvanced;
                        var fields = new[]
                        {
                            FieldChange(imdbOwned, before?.ImdbScore, proposed?.ImdbScore, accepted?.ImdbScore),
                            FieldChange(imdbOwned, before?.ImdbVoteCount, proposed?.ImdbVoteCount, accepted?.ImdbVoteCount),
                            FieldChange(tomatoesOwned, before?.RottenTomatoesScore, proposed?.RottenTomatoesScore, accepted?.RottenTomatoesScore),
                            FieldChange(tomatoesOwned, before?.RottenTomatoesAudienceScore, proposed?.RottenTomatoesAudienceScore, accepted?.RottenTomatoesAudienceScore),
                            FieldChange(!providerWrite || externalAdvanced, before?.MetacriticScore, proposed?.MetacriticScore, accepted?.MetacriticScore)
                        };
                        var changed = fields.Count(field => field.Changed);
                        var cleared = fields.Count(field => field.Cleared);
                        owner._telemetry.RecordDiagnostic("item_cache.result", details: new() { ItemId = write.Identity.TmdbId, MediaType = write.Identity.MediaType.ToString(),
                            Stage = "ratings", Outcome = cleared > 0 ? "cleared" : changed > 0 ? "refreshed" : "reused",
                            Reason = ratingsMerged ? "merged_projection" : "accepted_projection", Count = cleared > 0 ? cleared : changed, Total = changed,
                            DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                    }
                    var anyComponentAccepted = write.MetadataJson is not null && metadataAccepted || write.RatingsJson is not null && ratingsAccepted;
                    owner._telemetry.RecordDiagnostic("item_cache.write.item", details: new() { ItemId = write.Identity.TmdbId, MediaType = write.Identity.MediaType.ToString(),
                        Stage = write.Identity.MediaType.ToString(), Outcome = metadataAccepted && (ratingsAccepted || ratingsReused)
                            ? ratingsMerged ? "merged_projection" : ratingsReused && write.MetadataJson is null ? "reused" : "accepted_projection"
                            : anyComponentAccepted ? "partial_projection" : "concurrency_rejected",
                        Reason = record.Generation != write.ExpectedGeneration ? "metadata_generation_changed"
                            : !metadataAccepted || !ratingsAccepted && !ratingsReused ? "component_revision_changed" : null, Sequence = record.MetadataRevision,
                        DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                    // A metadata refresh with no new ratings still carries cached
                    // source scores. Preserve a repair that won during that write.
                    var ratingsBaseline = write.ExpectedRatingsBaseline
                        ?? (write.MetadataJson is not null && write.RatingsJson is null ? write.RatingsOrigin?.Baseline : null);
                    var submittedRatingsLost = write.RatingsJson is not null
                        && (record.RatingsJson != write.RatingsJson || record.RatingsFetchedUtc != write.RatingsFetchedUtc);
                    var cachedRatingsLost = write.RatingsJson is null && ratingsBaseline is { } baseline
                        && (record.RatingsJson != baseline.Json || record.RatingsFetchedUtc != baseline.FetchedUtc);
                    var guardedRatingsLost = generationMatches && write.RatingsOrigin is not null
                        && (submittedRatingsLost || cachedRatingsLost);
                    Accept(record, guardedRatingsLost ? write.RatingsOrigin : null);
                }
                outcome = conflicted ? "generation_conflict" : "accepted_or_rebased";
                if (conflicted && rejectGenerationConflicts) throw new ItemEnrichmentInvalidatedException();
            }
            catch (ItemEnrichmentInvalidatedException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = "failed";
                foreach (var write in writes)
                {
                    _writes.AddOrUpdate(write.Identity, write, (_, newer) =>
                        newer.ExpectedGeneration != write.ExpectedGeneration || newer.ExpectedMetadataRevision != write.ExpectedMetadataRevision ? newer
                        : newer with
                        {
                            MetadataJson = newer.MetadataJson ?? write.MetadataJson,
                            MetadataFetchedUtc = newer.MetadataJson is null ? write.MetadataFetchedUtc : newer.MetadataFetchedUtc,
                            RatingsJson = newer.RatingsJson ?? write.RatingsJson,
                            RatingsFetchedUtc = newer.RatingsJson is null ? write.RatingsFetchedUtc : newer.RatingsFetchedUtc,
                            RatingsOrigin = newer.RatingsJson is null ? write.RatingsOrigin : newer.RatingsOrigin,
                            ExpectedRatingsBaseline = newer.RatingsJson is null ? write.ExpectedRatingsBaseline : newer.ExpectedRatingsBaseline,
                            TvmazeShowId = newer.MetadataJson is null ? write.TvmazeShowId : newer.TvmazeShowId,
                            ExpectedTvmazeReservationToken = newer.MetadataJson is null ? write.ExpectedTvmazeReservationToken : newer.ExpectedTvmazeReservationToken,
                            CoreContinuation = newer.MetadataJson is null ? write.CoreContinuation : newer.CoreContinuation,
                            FreshMovieCore = newer.MetadataJson is null ? write.FreshMovieCore : newer.FreshMovieCore
                        });
                    if (ratingChanges.TryGetValue(write.Identity, out var change)) _ratingChanges.TryAdd(write.Identity, change);
                }
                owner._logger.LogWarning(ex, "Item enrichment cache write failed; provider results remain provisional until database validation.");
            }
            finally
            {
                _flushGate.Release();
                owner._telemetry.RecordDiagnostic("item_cache.write", details: new() { Count = count, Outcome = outcome,
                    DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            }
        }

        private ItemEnrichmentSnapshot Accept(ItemEnrichmentRecord record, ItemEnrichmentRatingsOrigin? preservedRatingsOrigin = null)
        {
            RememberAcceptedTomatoes(record);
            var current = Find(record.Identity);
            var accepted = new ItemEnrichmentSnapshot(record, Deserialize<ItemEnrichmentMetadata>(record.MetadataJson), Deserialize<ItemEnrichmentRatings>(record.RatingsJson));
            var sameIdentity = current is not null && current.Record.Generation == record.Generation
                && string.Equals(current.Metadata?.ImdbId, accepted.Metadata?.ImdbId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.Metadata?.WikidataId, accepted.Metadata?.WikidataId, StringComparison.OrdinalIgnoreCase)
                && current.Metadata?.TvdbId == accepted.Metadata?.TvdbId
                && string.Equals(current.Metadata?.Title, accepted.Metadata?.Title, StringComparison.OrdinalIgnoreCase);
            preservedRatingsOrigin = sameIdentity
                ? preservedRatingsOrigin ?? (current!.PreserveRatings ? current.RatingsReadOrigin : null) : null;
            if (preservedRatingsOrigin is not null) accepted = accepted with
            { RatingsReadOrigin = preservedRatingsOrigin, PreserveRatings = true };
            _records[record.Identity] = accepted;
            if (_completed.ContainsKey(record.Identity)) _completed[record.Identity] = accepted;
            if (accepted.PreserveRatings) DropPendingRatings(record.Identity);
            if (record.Identity.MediaType == PremiereMediaType.Series && current?.Record.Generation != record.Generation)
                foreach (var key in FirstEpisodeDates.Keys.Where(key => key.TmdbId == record.Identity.TmdbId && key.Generation != record.Generation))
                    FirstEpisodeDates.TryRemove(key, out _);
            return accepted;
        }

        public async Task<ItemEnrichmentReconciliation> ReconcileAsync(IReadOnlyList<PremiereItem> items,
            bool includeConsumed, CancellationToken cancellationToken, DateOnly? windowStart = null, DateOnly? windowEnd = null,
            bool readOnly = false)
        {
            // readOnly never Accepts store rows into this session. A newer foreign
            // row is only projected locally for this call, so a mid-load readiness
            // probe can neither roll back concurrent enrichment nor consume the
            // evidence the final reconcile uses to classify the load as dirty.
            Dictionary<ItemEnrichmentIdentity, ItemEnrichmentSnapshot>? foreignView = readOnly ? new() : null;
            ItemEnrichmentSnapshot? ViewOf(ItemEnrichmentIdentity identity) =>
                foreignView is not null && foreignView.TryGetValue(identity, out var foreign) ? foreign : Find(identity);
            var started = Stopwatch.GetTimestamp();
            if (_storageUnavailable)
                return new(items, items.Select(item => item.MediaType).Concat(_discoveryChanged.Keys).ToHashSet());
            var itemIdentities = items.Where(item => item.TmdbId > 0 && item.VerificationState == PremiereVerificationState.Verified)
                .Select(item => new ItemEnrichmentIdentity(item.MediaType, item.TmdbId));
            var identities = (includeConsumed ? itemIdentities.Concat(_records.Keys) : itemIdentities).Distinct().ToArray();
            var dirty = _discoveryChanged.Keys.ToHashSet();
            void MarkDirty(ItemEnrichmentIdentity identity, string reason)
            {
                dirty.Add(identity.MediaType);
                owner._telemetry.RecordDiagnostic("item_cache.reconcile.item", details: new() { ItemId = identity.TmdbId, MediaType = identity.MediaType.ToString(),
                    Stage = identity.MediaType.ToString(), Outcome = "discovery_required", Reason = reason,
                    DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            }
            bool CanonicalDateOutsideWindow(PremiereItem item, ItemEnrichmentSnapshot current)
            {
                if (windowStart is not { } first || windowEnd is not { } last || first > last
                    || item.MediaType != PremiereMediaType.Series || item.Type != PremiereItemType.SeriesPremiere
                    || item.DateSemantics?.SourceKind != PremiereDateSourceKind.TmdbSeasonOneEpisodeOne
                    || current.Record.Generation < 0) return false;
                DateOnly? canonicalDate = null;
                var superseded = false;
                if (FirstEpisodeDates.TryGetValue((item.TmdbId, current.Record.Generation), out var validation)
                    && validation.IsValueCreated && validation.Value.IsCompletedSuccessfully
                    && validation.Value.Result is { Successful: true, Date: { } validatedDate } proof)
                {
                    // Vote/remember writes from this session preserve its proof.
                    // A later foreign metadata winner must establish the date anew.
                    var revisionMatches = proof.BaselineMetadataRevision == current.Record.MetadataRevision
                        || _selfAcceptedRevisions.TryGetValue(current.Record.Identity, out var ownRevision)
                        && ownRevision == current.Record.MetadataRevision;
                    if (proof.BaselineMetadataFetchedUtc == current.Record.MetadataFetchedUtc && revisionMatches)
                        canonicalDate = validatedDate;
                    else superseded = true;
                }
                if (canonicalDate is null && owner.MetadataFresh(current)
                    && current.Metadata is { FirstEpisodeDateChecked: true, FirstEpisodeDate: { } acceptedDate })
                    canonicalDate = acceptedDate;
                if (canonicalDate is null && superseded)
                    MarkDirty(current.Record.Identity, "canonical_proof_superseded");
                return canonicalDate is { } date && (date < first || date > last);
            }
            foreach (var mediaType in dirty)
                owner._telemetry.RecordDiagnostic("item_cache.reconcile.item", details: new() { Stage = mediaType.ToString(), MediaType = mediaType.ToString(),
                    Outcome = "discovery_required", Reason = "validation_unverified",
                    DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            try
            {
                var latest = await owner._store.ReadCurrentManyAsync(partition, identities, cancellationToken);
                foreach (var record in latest.Values)
                {
                    var previous = Find(record.Identity);
                    if (previous is not null && previous.Record.MetadataInvalidatedUtc != record.MetadataInvalidatedUtc)
                        MarkDirty(record.Identity, "invalidation_changed");
                    if (foreignView is null) Accept(record);
                    else if (previous is null || previous.Record.MetadataInvalidatedUtc != record.MetadataInvalidatedUtc
                        || (previous.Record.Generation, previous.Record.MetadataRevision).CompareTo((record.Generation, record.MetadataRevision)) < 0)
                        foreignView[record.Identity] = new ItemEnrichmentSnapshot(record,
                            Deserialize<ItemEnrichmentMetadata>(record.MetadataJson), Deserialize<ItemEnrichmentRatings>(record.RatingsJson));
                }
                var result = new List<PremiereItem>(items.Count);
                foreach (var item in items)
                {
                    var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
                    if (item.TmdbId <= 0 || item.VerificationState != PremiereVerificationState.Verified) { result.Add(item); continue; }
                    if (!latest.TryGetValue(identity, out var record)) { MarkDirty(identity, "missing_record"); result.Add(item); continue; }
                    var current = ViewOf(identity)!;
                    // This removes only a disproven premiere occurrence. Earlier
                    // invalidation observations and session failures remain dirty.
                    if (CanonicalDateOutsideWindow(item, current))
                    {
                        owner._telemetry.RecordDiagnostic("item_cache.reconcile.item", details: new()
                        {
                            ItemId = identity.TmdbId, MediaType = PremiereMediaType.Series.ToString(), Stage = "canonical_date",
                            Outcome = "excluded", Reason = "canonical_date_outside_window",
                            DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
                        });
                        continue;
                    }
                    if (item.EnrichmentVersion?.MetadataInvalidatedUtc != record.MetadataInvalidatedUtc)
                        MarkDirty(identity, "item_invalidation_mismatch");
                    if (item.Type == PremiereItemType.SeriesPremiere && item.DateSemantics?.SourceKind == PremiereDateSourceKind.TmdbSeasonOneEpisodeOne
                        && current.Metadata is { FirstEpisodeDateChecked: true, FirstEpisodeDate: { } date } && date != item.PremiereDate)
                        MarkDirty(identity, "canonical_date_changed");
                    var metadataChanged = item.EnrichmentVersion is not { } version
                        || version.MetadataFetchedUtc != record.MetadataFetchedUtc;
                    if (_selfAcceptedRevisions.TryGetValue(identity, out var ownRevision) && ownRevision == record.MetadataRevision)
                        metadataChanged = false;
                    result.Add(WithAcceptedTomatoes(current, item).Rebase(item, metadataChanged));
                }
                owner._telemetry.RecordDiagnostic("item_cache.reconcile", details: new() { Count = result.Count, Total = identities.Length,
                    Outcome = dirty.Count == 0 ? "current" : "discovery_required", DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                return new(result, dirty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                owner._logger.LogWarning(ex, "Could not validate item enrichment revisions; calendar results remain provisional.");
                owner._telemetry.RecordDiagnostic("item_cache.reconcile", details: new() { Count = items.Count, Total = identities.Length,
                    Outcome = "failed", DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                return new(items, identities.Select(identity => identity.MediaType).ToHashSet());
            }
        }

        // Capture only authoritative persisted rows, before seeding or provider
        // proposals mutate this session. A second occurrence may legitimately own
        // another RT title/year while sharing the same metadata and IMDb identity.
        private void RememberAcceptedTomatoes(ItemEnrichmentRecord record)
        {
            if (record.Generation < 0 || record.MetadataFetchedUtc is not { } metadataClock
                || record.MetadataInvalidatedUtc is { } invalidated && invalidated > metadataClock
                || Deserialize<ItemEnrichmentMetadata>(record.MetadataJson) is not { } metadata
                || ItemEnrichmentRatingsMerger.ReadTracked(record.RatingsJson) is not
                    { FreshnessVerified: true, TomatoesFetchedUtc: { } fetched, TomatoesValues: { } values } ratings
                || fetched <= DateTimeOffset.MinValue || !ratings.MatchesMetadata(metadata)) return;
            var binding = ComponentIdentity("rottentomatoes", wikidataId: ratings.WikidataId, title: ratings.Title, year: ratings.Year);
            var proof = new AcceptedTomatoes(record.Generation, record.MetadataRevision, ratings.WikidataId,
                ratings.Title, ratings.Year, values, fetched, ratings.TomatoesRequired);
            _acceptedTomatoes.AddOrUpdate((record.Identity, binding), proof, (_, previous) =>
                proof.Generation > previous.Generation || proof.Generation == previous.Generation
                    && (proof.MetadataRevision > previous.MetadataRevision || proof.MetadataRevision == previous.MetadataRevision
                        && proof.FetchedUtc >= previous.FetchedUtc) ? proof : previous);
        }

        private ItemEnrichmentSnapshot WithAcceptedTomatoes(ItemEnrichmentSnapshot current, PremiereItem item)
        {
            if (current.Record.MetadataFetchedUtc is not { } metadataClock
                || current.Record.MetadataInvalidatedUtc is { } invalidated && invalidated > metadataClock
                || ItemEnrichmentRatingsMerger.ReadTracked(current.Record.RatingsJson) is not { FreshnessVerified: true } ratings
                || current.Metadata is null || !ratings.MatchesMetadata(current.Metadata)) return current;
            var target = current.Metadata.Apply(item);
            var binding = ComponentIdentity("rottentomatoes", wikidataId: target.WikidataId,
                title: target.Title, year: target.PremiereDate.Year);
            if (!_acceptedTomatoes.TryGetValue((current.Record.Identity, binding), out var proof)
                || proof.Generation != current.Record.Generation || proof.MetadataRevision != current.Record.MetadataRevision
                || ratings.MatchesTomatoes(proof.WikidataId, proof.Title, proof.Year)) return current;
            // This is a row projection only. Current OMDb/dataset fields and their
            // clocks win; never publish the other occurrence back over the store.
            var scores = ItemEnrichmentRatingsMerger.Combine(ratings.ExternalValues, ratings.ExternalFetchedUtc,
                proof.Values, proof.FetchedUtc);
            return current with { Ratings = ratings with
            {
                WikidataId = proof.WikidataId, Title = proof.Title, Year = proof.Year,
                TomatoesValues = proof.Values, TomatoesFetchedUtc = proof.FetchedUtc, TomatoesRequired = proof.Required,
                RottenTomatoesScore = scores.Critic, RottenTomatoesAudienceScore = scores.Audience
            } };
        }

        private sealed record AcceptedTomatoes(long Generation, long MetadataRevision, string? WikidataId,
            string? Title, int? Year, ItemEnrichmentRatingValues Values, DateTimeOffset FetchedUtc, bool Required);
        private sealed record RatingProjectionChange(ItemEnrichmentRatings? Before, ItemEnrichmentRatings? Proposed);
    }

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return default; }
    }
}

internal sealed record ItemEnrichmentSnapshot(ItemEnrichmentRecord Record, ItemEnrichmentMetadata? Metadata, ItemEnrichmentRatings? Ratings)
{
    internal ItemEnrichmentFreshMovieCore? FreshMovieCore { get; init; }
    // A conflicting guarded proposal cannot be replayed by this load's later
    // duplicate, synchronization or hydration stages. New sessions start clean.
    internal bool PreserveRatings { get; init; }
    internal ItemEnrichmentRatingsOrigin RatingsReadOrigin { get; init; }
        = new(Record.Generation, Record.MetadataRevision, new(Record.RatingsJson, Record.RatingsFetchedUtc));
    internal ItemEnrichmentRatingsBaseline RatingsReadBaseline => RatingsReadOrigin.Baseline;
    public ItemEnrichmentVersion Version => new(Record.Generation, Record.MetadataRevision,
        Record.MetadataFetchedUtc, Record.RatingsFetchedUtc, Record.MetadataInvalidatedUtc);

    public PremiereItem Apply(PremiereItem discovered)
    {
        var applied = Ratings?.Apply(Metadata?.Apply(discovered) ?? discovered) ?? Metadata?.Apply(discovered) ?? discovered;
        var clocks = SuccessClocks();
        return applied with { LastUpdatedUtc = clocks.Length == 0 ? applied.LastUpdatedUtc : clocks.Min(), EnrichmentVersion = Version };
    }

    public PremiereItem Rebase(PremiereItem item, bool metadataChanged)
    {
        var applied = Record.RatingsFetchedUtc is null ? item : item with
        {
            ImdbScore = null, ImdbVoteCount = null, RottenTomatoesScore = null,
            RottenTomatoesAudienceScore = null, MetacriticScore = null
        };
        if (Metadata is not null)
        {
            applied = Metadata.Apply(applied, preferDiscoveryMetadata: !metadataChanged);
            if (!metadataChanged) applied = applied with
            {
                OriginCountries = item.OriginCountries.Length > 0 ? item.OriginCountries : applied.OriginCountries,
                GenreIds = item.GenreIds.Length > 0 ? item.GenreIds : applied.GenreIds,
                TmdbScore = Metadata.TmdbVotes?.TmdbScore ?? applied.TmdbScore,
                TmdbVoteCount = Metadata.TmdbVotes?.TmdbVoteCount ?? applied.TmdbVoteCount
            };
        }
        if (Ratings is not null) applied = Ratings.Apply(applied);
        var sources = applied.Sources.Where(source => !string.Equals(source.Kind, "schedule", StringComparison.OrdinalIgnoreCase))
            .Concat(item.Sources.Where(source => string.Equals(source.Kind, "schedule", StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(source => $"{source.Kind}:{source.Id}:{source.Name}", StringComparer.OrdinalIgnoreCase).ToArray();
        return applied with
        {
            Sources = sources, SourceNames = sources.Select(source => source.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            EnrichmentVersion = Version,
            LastUpdatedUtc = SuccessClocks().DefaultIfEmpty(applied.LastUpdatedUtc).Min()
        };
    }

    private DateTimeOffset[] SuccessClocks()
    {
        var ratingsClocks = Ratings is { ProviderFreshnessTracked: true } ratings
            ? new[] { ratings.ExternalRequired ? ratings.ExternalFetchedUtc : null,
                ratings.TomatoesRequired ? ratings.TomatoesFetchedUtc : null,
                ratings.DatasetRequired ? ratings.Dataset?.FetchedUtc : null }
            : new[] { Record.RatingsFetchedUtc };
        var optionalClocks = Metadata is { OptionalFreshnessTracked: true } metadata
            ? new[] { metadata.AvailabilityFetchedUtc, metadata.ArtworkFetchedUtc,
                Record.Identity.MediaType == PremiereMediaType.Series ? metadata.TvmazeFetchedUtc : null }
            : Array.Empty<DateTimeOffset?>();
        return ratingsClocks.Concat(optionalClocks).Append(Record.MetadataFetchedUtc).Where(clock => clock is { } known && known > DateTimeOffset.MinValue)
            .Select(clock => clock!.Value).ToArray();
    }
}

internal sealed record ItemEnrichmentRefresh(ItemEnrichmentMetadata? Metadata, ItemEnrichmentRatings? Ratings,
    bool RefreshMetadataClock = true, bool MetadataFailed = false, bool RatingsFailed = false,
    bool AvailabilityFailed = false, bool OptionalMetadataFailed = false, bool RefreshRatingsClock = true,
    string? FreshMovieCoreJson = null);
internal sealed record FirstEpisodeValidation(DateOnly? Date, bool Successful)
{
    public long? BaselineMetadataRevision { get; init; }
    public DateTimeOffset? BaselineMetadataFetchedUtc { get; init; }
}
internal sealed class ItemEnrichmentInvalidatedException() : Exception(
    "Item metadata changed while enrichment was running. The source remains partial and will be retried.");
internal sealed record ItemEnrichmentReconciliation(IReadOnlyList<PremiereItem> Items, IReadOnlySet<PremiereMediaType> DirtyMedia);
