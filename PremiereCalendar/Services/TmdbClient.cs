using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class TmdbClient : ITmdbClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int TmdbMaximumPage = 500;
    private const int MaximumNotFoundEntries = 256;
    private static readonly TimeSpan NotFoundLifetime = TimeSpan.FromMinutes(15);
    private static readonly ConditionalWeakTable<IMemoryCache, ReuseState> ReuseStates = new();

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly TmdbOptions _options;
    private readonly IIntegrationSettingsStore? _settingsStore;
    private readonly TmdbRequestLimiter _requestLimiter;
    private readonly ILogger<TmdbClient> _logger;
    private readonly ISingleFlightCoordinator _singleFlight;
    private readonly ProviderResponseCache? _responseCache;
    private readonly PremiereTelemetry? _telemetry;
    private readonly object _reuseLock;
    private readonly Dictionary<string, DiscoveryFlight> _discoveryFlights;
    private readonly Dictionary<string, ReusePublicationState> _reusePublications;
    private readonly Dictionary<string, LinkedListNode<NotFoundEntry>> _notFound;
    private readonly LinkedList<NotFoundEntry> _notFoundLru;

    public TmdbClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<TmdbOptions> options,
        ILogger<TmdbClient> logger,
        TmdbRequestLimiter? requestLimiter = null,
        IIntegrationSettingsStore? settingsStore = null,
        ISingleFlightCoordinator? singleFlight = null,
        ProviderResponseCache? responseCache = null,
        PremiereTelemetry? telemetry = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
        _requestLimiter = requestLimiter ?? new TmdbRequestLimiter(options, telemetry);
        _logger = logger;
        _singleFlight = singleFlight ?? new SingleFlightCoordinator();
        _responseCache = responseCache;
        _telemetry = telemetry;
        var reuse = ReuseStates.GetValue(cache, static _ => new ReuseState());
        _reuseLock = reuse.Gate;
        _discoveryFlights = reuse.DiscoveryFlights;
        _reusePublications = reuse.Publications;
        _notFound = reuse.NotFound;
        _notFoundLru = reuse.NotFoundLru;
    }

    public async Task<IReadOnlyList<TmdbTvDiscoverItem>> DiscoverTvAsync(
        DateOnly start,
        DateOnly end,
        TmdbDiscoverFilters filters,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var items = new List<TmdbTvDiscoverItem>();
        await foreach (var batch in StreamDiscoverTvAsync(start, end, filters, cancellationToken, forceRefresh)
            .WithCancellation(cancellationToken))
        {
            items.AddRange(batch.Results);
        }

        return items;
    }

    public IAsyncEnumerable<TmdbDiscoverBatch<TmdbTvDiscoverItem>> StreamDiscoverTvAsync(
        DateOnly start,
        DateOnly end,
        TmdbDiscoverFilters filters,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        EnsureConfigured();

        var cacheKey = $"tmdb:discover-tv:{start:yyyyMMdd}:{end:yyyyMMdd}:{DiscoverFilterKey(filters)}";
        return StreamPagedAsync<TmdbTvDiscoverItem>(
            cacheKey,
            TimeSpan.FromHours(6),
            page => TmdbQueryBuilder.BuildDiscoverTvPath(start, end, filters, page),
            cancellationToken,
            forceRefresh);
    }

    public async Task<IReadOnlyList<TmdbMovieDiscoverItem>> DiscoverMoviesAsync(
        DateOnly start,
        DateOnly end,
        TmdbDiscoverFilters filters,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var items = new List<TmdbMovieDiscoverItem>();
        await foreach (var batch in StreamDiscoverMoviesAsync(start, end, filters, cancellationToken, forceRefresh)
            .WithCancellation(cancellationToken))
        {
            items.AddRange(batch.Results);
        }

        return items;
    }

    public IAsyncEnumerable<TmdbDiscoverBatch<TmdbMovieDiscoverItem>> StreamDiscoverMoviesAsync(
        DateOnly start,
        DateOnly end,
        TmdbDiscoverFilters filters,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        EnsureConfigured();

        var cacheKey = $"tmdb:discover-movie:{start:yyyyMMdd}:{end:yyyyMMdd}:{DiscoverFilterKey(filters)}";
        return StreamPagedAsync<TmdbMovieDiscoverItem>(
            cacheKey,
            TimeSpan.FromHours(6),
            page => TmdbQueryBuilder.BuildDiscoverMoviePath(start, end, filters, page),
            cancellationToken,
            forceRefresh);
    }

    public Task<IReadOnlyList<TmdbTvDiscoverItem>> DiscoverTvByNetworksAsync(
        DateOnly start,
        DateOnly end,
        IReadOnlyList<int> networkIds,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        EnsureConfigured();

        var filteredNetworkIds = networkIds
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .ToArray();

        if (filteredNetworkIds.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<TmdbTvDiscoverItem>>([]);
        }

        var cacheKey = DiscoverPagingKey($"tmdb:discover-tv-networks:{start:yyyyMMdd}:{end:yyyyMMdd}:{NetworkKey(filteredNetworkIds)}");
        return GetOrCreateRequiredAsync(
            cacheKey,
            TimeSpan.FromHours(6),
            token => GetPagedAsync<TmdbTvDiscoverItem>(
                page => TmdbQueryBuilder.BuildDiscoverTvByNetworksPath(start, end, filteredNetworkIds, page),
                token, forceRefresh: forceRefresh),
            cancellationToken,
            forceRefresh);
    }

    public Task<TmdbDetailsWithExtras?> GetTvDetailsAsync(int id, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        EnsureConfigured();

        return GetOrCreateAsync(
            $"tmdb:tv-details:{id}",
            TimeSpan.FromHours(12),
            token => SendJsonAsync<TmdbDetailsWithExtras>(
                TmdbQueryBuilder.BuildTvDetailsPath(id),
                "TMDb TV details",
                token,
                notFoundReturnsNull: true, verifyNotFound: true),
            cancellationToken,
            forceRefresh,
            persistentRequestKey: $"details-v1:{_options.BaseUrl}:{TmdbQueryBuilder.BuildTvDetailsPath(id)}");
    }

    public Task<TmdbSeasonDetails?> GetTvSeasonDetailsAsync(
        int id,
        int seasonNumber,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        EnsureConfigured();

        if (id <= 0 || seasonNumber <= 0)
        {
            return Task.FromResult<TmdbSeasonDetails?>(null);
        }

        return GetOrCreateAsync(
            $"tmdb:tv-season-details:{id}:{seasonNumber}",
            TimeSpan.FromHours(12),
            token => SendJsonAsync<TmdbSeasonDetails>(
                TmdbQueryBuilder.BuildTvSeasonDetailsPath(id, seasonNumber),
                "TMDb TV season details",
                token,
                notFoundReturnsNull: true, verifyNotFound: true),
            cancellationToken,
            forceRefresh,
            persistentRequestKey: $"season-v1:{_options.BaseUrl}:{TmdbQueryBuilder.BuildTvSeasonDetailsPath(id, seasonNumber)}");
    }

    public Task<TmdbDetailsWithExtras?> GetMovieDetailsAsync(int id, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        EnsureConfigured();

        return GetOrCreateAsync(
            $"tmdb:movie-details:{id}",
            TimeSpan.FromHours(12),
            token => SendJsonAsync<TmdbDetailsWithExtras>(
                TmdbQueryBuilder.BuildMovieDetailsPath(id),
                "TMDb movie details",
                token,
                notFoundReturnsNull: true, verifyNotFound: true),
            cancellationToken,
            forceRefresh,
            persistentRequestKey: $"details-v1:{_options.BaseUrl}:{TmdbQueryBuilder.BuildMovieDetailsPath(id)}");
    }

    public async Task<int?> FindTmdbIdByExternalIdAsync(
        PremiereMediaType mediaType,
        string externalId,
        string externalSource,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        EnsureConfigured();

        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(externalSource))
        {
            return null;
        }

        var cacheKey = $"tmdb:find:{mediaType}:{externalSource}:{externalId}";
        var response = await GetOrCreateAsync(
            cacheKey,
            TimeSpan.FromDays(90),
            token => SendJsonAsync<TmdbFindResponse>(
                TmdbQueryBuilder.BuildFindByExternalIdPath(
                    Uri.EscapeDataString(externalId.Trim()),
                    externalSource.Trim()),
                "TMDb external ID lookup",
                token,
                notFoundReturnsNull: true),
            cancellationToken,
            forceRefresh,
            persistentRequestKey: $"find-v1:{_options.BaseUrl}:{externalSource.Trim()}:{externalId.Trim()}",
            lifetimeSelector: result => result.MovieResults.Count > 0 || result.TvResults.Count > 0
                ? TimeSpan.FromDays(90) : TimeSpan.FromHours(6));

        var result = mediaType == PremiereMediaType.Movie
            ? response?.MovieResults.FirstOrDefault()
            : response?.TvResults.FirstOrDefault();

        return result?.Id > 0 ? result.Id : null;
    }

    public async Task<IReadOnlyList<TmdbTitleSearchResult>> SearchTitlesAsync(
        PremiereMediaType mediaType,
        string query,
        int? year,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        EnsureConfigured();

        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var normalizedQuery = query.Trim();
        var normalizedYear = year is > 0 ? year.Value.ToString(CultureInfo.InvariantCulture) : "any";
        var response = await GetOrCreateAsync(
            $"tmdb:title-search:{mediaType}:{normalizedYear}:{normalizedQuery.ToLowerInvariant()}",
            TimeSpan.FromDays(90),
            token => SendJsonAsync<TmdbPagedResponse<TmdbTitleSearchResult>>(
                TmdbQueryBuilder.BuildSearchTitlePath(mediaType, normalizedQuery, year),
                "TMDb title search",
                token,
                notFoundReturnsNull: false),
            cancellationToken,
            forceRefresh,
            persistentRequestKey: $"search-v1:{_options.BaseUrl}:{TmdbQueryBuilder.BuildSearchTitlePath(mediaType, normalizedQuery, year)}",
            lifetimeSelector: result => result.Results.Count > 0 ? TimeSpan.FromDays(90) : TimeSpan.FromHours(6));

        return response?.Results ?? [];
    }

    public async Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(
        PremiereMediaType mediaType,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        EnsureConfigured();

        var cacheKey = $"tmdb:genres:{mediaType}";
        var response = await GetOrCreateRequiredAsync(
            cacheKey,
            TimeSpan.FromDays(7),
            token => SendJsonAsync<TmdbGenreList>(
                mediaType == PremiereMediaType.Movie
                    ? TmdbQueryBuilder.BuildMovieGenresPath()
                    : TmdbQueryBuilder.BuildTvGenresPath(),
                "TMDb genres",
                token,
                notFoundReturnsNull: false),
            cancellationToken,
            forceRefresh);

        return response?.Genres ?? [];
    }

    public async Task<IReadOnlyList<TmdbConfigurationLanguage>> GetLanguagesAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        EnsureConfigured();

        return await GetOrCreateRequiredAsync(
            "tmdb:configuration-languages",
            TimeSpan.FromDays(7),
            token => SendJsonAsync<IReadOnlyList<TmdbConfigurationLanguage>>(
                TmdbQueryBuilder.BuildLanguagesPath(),
                "TMDb languages",
                token,
                notFoundReturnsNull: false),
            cancellationToken,
            forceRefresh) ?? [];
    }

    public async Task<IReadOnlyList<TmdbConfigurationCountry>> GetCountriesAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        EnsureConfigured();

        return await GetOrCreateRequiredAsync(
            "tmdb:configuration-countries",
            TimeSpan.FromDays(7),
            token => SendJsonAsync<IReadOnlyList<TmdbConfigurationCountry>>(
                TmdbQueryBuilder.BuildCountriesPath(),
                "TMDb countries",
                token,
                notFoundReturnsNull: false),
            cancellationToken,
            forceRefresh) ?? [];
    }

    public async Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(
        PremiereMediaType mediaType,
        string region,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        EnsureConfigured();

        var normalizedRegion = string.IsNullOrWhiteSpace(region) ? "ALL" : region.Trim().ToUpperInvariant();
        var response = await GetOrCreateRequiredAsync(
            $"tmdb:watch-providers:{mediaType}:{normalizedRegion}",
            TimeSpan.FromDays(7),
            token => SendJsonAsync<TmdbWatchProviderList>(
                TmdbQueryBuilder.BuildWatchProvidersPath(
                    mediaType,
                    string.Equals(normalizedRegion, "ALL", StringComparison.OrdinalIgnoreCase) ? "" : normalizedRegion),
                "TMDb watch providers",
                token,
                notFoundReturnsNull: false),
            cancellationToken,
            forceRefresh);

        return response?.Results ?? [];
    }

    public Task<TmdbCertificationResponse?> GetCertificationsAsync(
        PremiereMediaType mediaType,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        EnsureConfigured();

        return GetOrCreateAsync(
            $"tmdb:certifications:{mediaType}",
            TimeSpan.FromDays(7),
            token => SendJsonAsync<TmdbCertificationResponse>(
                mediaType == PremiereMediaType.Movie
                    ? TmdbQueryBuilder.BuildMovieCertificationsPath()
                    : TmdbQueryBuilder.BuildTvCertificationsPath(),
                "TMDb certifications",
                token,
                notFoundReturnsNull: false),
            cancellationToken,
            forceRefresh);
    }

    public async Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(
        string query,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        EnsureConfigured();

        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var normalizedQuery = query.Trim();
        var response = await GetOrCreateRequiredAsync(
            $"tmdb:keyword-search:{normalizedQuery.ToLowerInvariant()}",
            TimeSpan.FromDays(7),
            token => SendJsonAsync<TmdbKeywordResponse>(
                TmdbQueryBuilder.BuildSearchKeywordPath(normalizedQuery),
                "TMDb keyword search",
                token,
                notFoundReturnsNull: false),
            cancellationToken,
            forceRefresh);

        return response?.Results ?? [];
    }

    public Task<IReadOnlyList<TmdbChangedItem>> GetChangedMovieIdsAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        return GetChangedIdsAsync(PremiereMediaType.Movie, start, end, cancellationToken, forceRefresh);
    }

    public Task<IReadOnlyList<TmdbChangedItem>> GetChangedTvIdsAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        return GetChangedIdsAsync(PremiereMediaType.Series, start, end, cancellationToken, forceRefresh);
    }

    public async Task<DateTimeOffset?> GetLastMetadataChangeUtcAsync(
        PremiereMediaType mediaType, int id, DateOnly start, DateOnly end,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        if (id <= 0 || end < start) throw new ArgumentOutOfRangeException(nameof(id));
        var media = mediaType == PremiereMediaType.Movie ? "movie" : "tv";
        // Inspect history only for cached IDs returned by the cheap change list.
        // This is not a full-catalog detail sweep.
        var history = await SendJsonAsync<TmdbChangeHistory>(
            $"{media}/{id}/changes?start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}",
            "TMDb item change history", cancellationToken, notFoundReturnsNull: true);
        if (history is null) return null;
        if (history.Changes is null) throw new ExternalApiException("TMDb change history omitted its changes collection.");
        DateTimeOffset? latest = null;
        foreach (var field in history.Changes)
        {
            if (field.Key is "cast" or "crew" or "guest_stars" or "aggregate_credits") continue;
            foreach (var item in field.Items)
            {
                var text = item.Time.EndsWith(" UTC", StringComparison.Ordinal)
                    ? item.Time[..^4] + "Z" : item.Time;
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                    throw new ExternalApiException("TMDb change history contained an invalid timestamp.");
                if (latest is null || timestamp > latest) latest = timestamp;
            }
        }
        return latest ?? DateTimeOffset.MinValue;
    }

    private Task<IReadOnlyList<TmdbChangedItem>> GetChangedIdsAsync(
        PremiereMediaType mediaType,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        EnsureConfigured();

        if (end < start)
        {
            return Task.FromResult<IReadOnlyList<TmdbChangedItem>>([]);
        }

        var mediaKey = mediaType == PremiereMediaType.Movie ? "movie" : "tv";
        return GetOrCreateRequiredAsync(
            $"tmdb:changes:{mediaKey}:{start:yyyyMMdd}:{end:yyyyMMdd}",
            TimeSpan.FromHours(1),
            token => GetPagedAsync<TmdbChangedItem>(
                page => TmdbQueryBuilder.BuildChangesPath(mediaType, start, end, page),
                token, requireComplete: true, forceRefresh: forceRefresh),
            cancellationToken,
            forceRefresh);
    }

    private async Task<IReadOnlyList<T>> GetPagedAsync<T>(
        Func<int, string> pathFactory,
        CancellationToken cancellationToken,
        bool requireComplete = false,
        bool forceRefresh = false)
    {
        var flightScope = forceRefresh ? Guid.NewGuid().ToString("N") : null;
        var firstPage = await FetchDiscoverPageAsync<T>(pathFactory, 1, cancellationToken, flightScope);
        if (firstPage is null)
        {
            if (requireComplete) throw new ExternalApiException("TMDb changes returned no first page.");
            return [];
        }

        if (requireComplete && (firstPage.Page != 1 || firstPage.TotalPages < 0 || !firstPage.HasResults || firstPage.Results is null
            || (firstPage.TotalPages == 0 && firstPage.Results.Count != 0)))
            throw new ExternalApiException("TMDb changes returned an invalid first page.");
        var totalPages = Math.Clamp(Math.Max(1, firstPage.TotalPages), 1, TmdbMaximumPage);
        var configuredMaxPages = IsBroadDiscoverQuery(firstPage, pathFactory)
            ? _options.MaxUnfilteredPagesPerQuery
            : _options.MaxPagesPerQuery;
        var maxPages = Math.Clamp(configuredMaxPages, 1, TmdbMaximumPage);
        var pagesToFetch = Math.Min(totalPages, maxPages);
        if (firstPage.TotalPages > maxPages)
        {
            if (requireComplete) throw new ExternalApiException("TMDb changes exceeded the complete-page safety limit.");
            _logger.LogWarning(
                "TMDb discover query returned {TotalPages} pages; fetching configured cap of {MaxPagesPerQuery} pages. Increase Tmdb:MaxPagesPerQuery to avoid truncation.",
                firstPage.TotalPages,
                maxPages);
        }

        var results = new List<T>(firstPage.Results);
        if (pagesToFetch <= 1)
        {
            return results;
        }

        var concurrency = Math.Clamp(_options.PageFetchConcurrency, 1, pagesToFetch);
        using var gate = new SemaphoreSlim(concurrency);
        var remainingPageTasks = Enumerable.Range(2, pagesToFetch - 1)
            .Select(async page =>
            {
                await gate.WaitAsync(cancellationToken);

                try
                {
                    return (Page: page, Response: await FetchDiscoverPageAsync<T>(pathFactory, page, cancellationToken, flightScope));
                }
                finally
                {
                    gate.Release();
                }
            });

        var fetchedPages = await Task.WhenAll(remainingPageTasks);
        if (requireComplete && fetchedPages.Any(page => page.Response is null || page.Response.Page != page.Page
            || page.Response.TotalPages != firstPage.TotalPages || !page.Response.HasResults || page.Response.Results is null))
            throw new ExternalApiException("TMDb changes contained an incomplete page.");
        foreach (var fetchedPage in fetchedPages.OrderBy(page => page.Page))
        {
            if (fetchedPage.Response is not null)
            {
                results.AddRange(fetchedPage.Response.Results);
            }
        }

        return results;
    }

    private async IAsyncEnumerable<TmdbDiscoverBatch<T>> StreamPagedAsync<T>(
        string cacheKey,
        TimeSpan cacheDuration,
        Func<int, string> pathFactory,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        bool forceRefresh)
    {
        cancellationToken.ThrowIfCancellationRequested();
        cacheKey = SourceCacheKey(DiscoverPagingKey(cacheKey));
        DiscoveryFlight? flight = null;
        ReusePublication? publication = null;
        long waiterId = 0;
        IReadOnlyList<T>? cached = null;
        lock (_reuseLock)
        {
            if (forceRefresh || !_cache.TryGetValue(cacheKey, out cached) || cached is null)
            {
                publication = BeginReusePublication(cacheKey, forceRefresh);
                if (forceRefresh || !_discoveryFlights.TryGetValue(cacheKey, out flight) || !flight.CanJoin)
                {
                    flight = new DiscoveryFlight();
                    if (!forceRefresh) _discoveryFlights[cacheKey] = flight;
                }
                flight.References++;
                waiterId = flight.AddWaiter(ProviderExecutionContext.CapturePriorityAccessor(), cancellationToken);
            }
        }
        if (flight is null)
        {
            yield return new TmdbDiscoverBatch<T>(1, 1, 1, cached!.Count, cached);
            yield break;
        }

        try
        {
            var firstPage = await flight.GetOrAdd(1, token => FetchDiscoverPageAsync<T>(pathFactory, 1, token, flight.Id))
                .WaitAsync(cancellationToken);
            if (firstPage is null)
            {
                yield break;
            }

            var totalPages = Math.Clamp(Math.Max(1, firstPage.TotalPages), 1, TmdbMaximumPage);
            var configuredMaxPages = IsBroadDiscoverQuery(firstPage, pathFactory)
                ? _options.MaxUnfilteredPagesPerQuery
                : _options.MaxPagesPerQuery;
            var maxPages = Math.Clamp(configuredMaxPages, 1, TmdbMaximumPage);
            var pagesToFetch = Math.Min(totalPages, maxPages);
            if (firstPage.TotalPages > maxPages)
            {
                _logger.LogWarning(
                    "TMDb discover query returned {TotalPages} pages; fetching configured cap of {MaxPagesPerQuery} pages.",
                    firstPage.TotalPages,
                    maxPages);
            }

            var accumulated = new List<T>(firstPage.Results);
            yield return new TmdbDiscoverBatch<T>(
                firstPage.Page > 0 ? firstPage.Page : 1,
                firstPage.Page > 0 ? firstPage.Page : 1,
                totalPages,
                firstPage.TotalResults,
                firstPage.Results);

            if (pagesToFetch <= 1)
            {
                publication!.TryPublish(() => _cache.Set(cacheKey, accumulated.ToArray(), cacheDuration));
                yield break;
            }

            var pageBatchSize = Math.Clamp(_options.PageBatchSize, 1, 50);
            for (var pageStart = 2; pageStart <= pagesToFetch; pageStart += pageBatchSize)
            {
                var pageEnd = Math.Min(pagesToFetch, pageStart + pageBatchSize - 1);
                var rangeStart = pageStart;
                var fetchedPages = await flight.GetOrAdd(rangeStart,
                    token => FetchPageRangeAsync<T>(pathFactory, rangeStart, pageEnd, token, flight.Id))
                    .WaitAsync(cancellationToken);
                var results = fetchedPages
                    .SelectMany(page => page.Results)
                    .ToArray();

                accumulated.AddRange(results);
                yield return new TmdbDiscoverBatch<T>(
                    pageStart,
                    pageEnd,
                    totalPages,
                    firstPage.TotalResults,
                    results);
            }

            publication!.TryPublish(() => _cache.Set(cacheKey, accumulated.ToArray(), cacheDuration));
        }
        finally
        {
            flight.RemoveWaiter(waiterId);
            publication!.Dispose();
            var release = false;
            lock (_reuseLock)
            {
                if (--flight.References == 0)
                {
                    if (_discoveryFlights.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, flight))
                        _discoveryFlights.Remove(cacheKey);
                    release = true;
                }
            }
            if (release) await flight.StopAsync();
        }
    }

    private async Task<IReadOnlyList<TmdbPagedResponse<T>>> FetchPageRangeAsync<T>(
        Func<int, string> pathFactory,
        int pageStart,
        int pageEnd,
        CancellationToken cancellationToken,
        string? flightScope = null)
    {
        var pageCount = Math.Max(0, pageEnd - pageStart + 1);
        if (pageCount == 0)
        {
            return [];
        }

        var concurrency = Math.Clamp(_options.PageFetchConcurrency, 1, pageCount);
        using var gate = new SemaphoreSlim(concurrency);
        var tasks = Enumerable.Range(pageStart, pageCount)
            .Select(async page =>
            {
                await gate.WaitAsync(cancellationToken);

                try
                {
                    return await FetchDiscoverPageAsync<T>(pathFactory, page, cancellationToken, flightScope);
                }
                finally
                {
                    gate.Release();
                }
            });

        var pages = await Task.WhenAll(tasks);
        return pages
            .Where(page => page is not null)
            .Select(page => page!)
            .OrderBy(page => page.Page)
            .ToArray();
    }

    private static bool IsBroadDiscoverQuery<T>(TmdbPagedResponse<T> firstPage, Func<int, string> pathFactory)
    {
        if (firstPage.TotalPages <= 1)
        {
            return false;
        }

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
            new Uri($"https://local/{pathFactory(1).TrimStart('/')}").Query);

        var filterKeys = query.Keys
            .Where(key => !key.Equals("page", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("sort_by", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("include_adult", StringComparison.OrdinalIgnoreCase)
                && !key.EndsWith("_date.gte", StringComparison.OrdinalIgnoreCase)
                && !key.EndsWith("_date.lte", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("air_date.gte", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("air_date.lte", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return filterKeys.Length == 0;
    }

    private Task<TmdbPagedResponse<T>?> FetchDiscoverPageAsync<T>(
        Func<int, string> pathFactory,
        int page,
        CancellationToken cancellationToken,
        string? flightScope = null)
    {
        var path = pathFactory(page);
        return _singleFlight.RunAsync(
            SourceCacheKey($"tmdb:discover-page:{flightScope ?? "normal"}:{path}"),
            token => SendJsonAsync<TmdbPagedResponse<T>>(
                path,
                "TMDb discover",
                token,
                notFoundReturnsNull: false),
            cancellationToken);
    }

    private Task<T?> GetOrCreateAsync<T>(
        string cacheKey,
        TimeSpan duration,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken cancellationToken,
        bool forceRefresh,
        string? persistentRequestKey = null,
        Func<T, TimeSpan>? lifetimeSelector = null)
        => TraceLookupAsync(cacheKey, forceRefresh, () => GetOrCreateScopedAsync(
            SourceCacheKey(cacheKey), duration, factory, cancellationToken, forceRefresh, persistentRequestKey, lifetimeSelector));

    private Task<T?> GetOrCreateScopedAsync<T>(
        string cacheKey,
        TimeSpan duration,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken cancellationToken,
        bool forceRefresh,
        string? persistentRequestKey,
        Func<T, TimeSpan>? lifetimeSelector)
    {
        if (typeof(T) != typeof(TmdbDetailsWithExtras) || ProviderRefreshScope.Current is not { } refresh)
            return GetOrCreateCoreAsync(cacheKey, duration, factory, cancellationToken, forceRefresh, persistentRequestKey, lifetimeSelector);

        return refresh.RunAsync($"tmdb:{_options.BaseUrl}:{cacheKey}", forceRefresh,
            async token =>
            {
                var value = await GetOrCreateCoreAsync(cacheKey, duration, factory, token, forceRefresh, persistentRequestKey, lifetimeSelector);
                return new ProviderRefreshResult<T?>(value, IsVerifiedDetail(value));
            }, cancellationToken,
            value => IsVerifiedDetail(value) && _cache.TryGetValue(cacheKey, out T? latest) && ReferenceEquals(latest, value));

        bool IsVerifiedDetail(T? value) => value is TmdbDetailsWithExtras { Id: > 0 } details
            && cacheKey.EndsWith($":{details.Id}", StringComparison.Ordinal);
    }

    private async Task<T?> GetOrCreateCoreAsync<T>(
        string cacheKey,
        TimeSpan duration,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken cancellationToken,
        bool forceRefresh,
        string? persistentRequestKey = null,
        Func<T, TimeSpan>? lifetimeSelector = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var memoryStarted = Stopwatch.GetTimestamp();
        if (!forceRefresh && IsNotFoundCached(cacheKey))
        {
            RecordMemoryLookup(cacheKey, "verified_not_found", memoryStarted);
            return default;
        }
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out T? cached))
        {
            RecordMemoryLookup(cacheKey, "hit", memoryStarted);
            return cached;
        }

        RecordMemoryLookup(cacheKey, forceRefresh ? "bypassed" : "miss", memoryStarted);

        var flightKey = forceRefresh ? $"refresh:{cacheKey}" : $"cache:{cacheKey}";
        return await _singleFlight.RunAsync(
            flightKey,
            async token =>
            {
                var flightMemoryStarted = Stopwatch.GetTimestamp();
                if (!forceRefresh && IsNotFoundCached(cacheKey))
                    return default;
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out T? flightCached))
                {
                    RecordMemoryLookup(cacheKey, "shared_hit", flightMemoryStarted);
                    return flightCached;
                }

                using var memoryPublication = BeginReusePublication(cacheKey, forceRefresh);
                using var publication = persistentRequestKey is null ? null : _responseCache?.BeginOperation("tmdb", persistentRequestKey, forceRefresh);
                if (!forceRefresh && persistentRequestKey is not null && _responseCache is not null)
                {
                    var persisted = await _responseCache.GetAsync<T>("tmdb", persistentRequestKey, duration, token);
                    if (persisted is not null)
                    {
                        return await publication!.PublishAsync(current =>
                        {
                            if (_cache.TryGetValue(cacheKey, out T? latest)) return Task.FromResult(latest);
                            if (current) memoryPublication.TryPublish(() => _cache.Set(cacheKey, persisted.Value, persisted.ExpiresAtUtc));
                            return Task.FromResult<T?>(persisted.Value);
                        }, token);
                    }
                }

                T? value;
                try { value = await factory(token); }
                catch (VerifiedNotFoundException)
                {
                    if (publication is not null)
                    {
                        return await publication.PublishAsync(async current =>
                        {
                            if (current && memoryPublication.TryPublish(() => RememberNotFound(cacheKey)))
                            {
                                // A typed tombstone invalidates an older persisted success. The
                                // short negative itself stays bounded in this client, not on disk.
                                await _responseCache!.SaveAsync("tmdb", persistentRequestKey!, new TmdbResponseInvalidation(), token);
                            }
                            else if (!forceRefresh && _cache.TryGetValue(cacheKey, out T? latest)) return latest;
                            return default;
                        }, token);
                    }
                    if (!memoryPublication.TryPublish(() => RememberNotFound(cacheKey))
                        && !forceRefresh && _cache.TryGetValue(cacheKey, out T? latest)) return latest;
                    return default;
                }
                if (value is not null)
                {
                    var effectiveDuration = lifetimeSelector?.Invoke(value) ?? duration;
                    if (publication is not null)
                    {
                        return await publication.PublishAsync(async current =>
                        {
                            if (current && memoryPublication.TryPublish(() =>
                            {
                                ForgetNotFound(cacheKey);
                                _cache.Set(cacheKey, value, effectiveDuration);
                            }))
                            {
                                await _responseCache!.SaveAsync("tmdb", persistentRequestKey!, value, token,
                                    invalidateOversized: forceRefresh, lifetime: effectiveDuration);
                            }
                            else if (!forceRefresh && _cache.TryGetValue(cacheKey, out T? latest)) return latest;
                            return value;
                        }, token);
                    }

                    if (!memoryPublication.TryPublish(() =>
                    {
                        ForgetNotFound(cacheKey);
                        _cache.Set(cacheKey, value, effectiveDuration);
                    }) && !forceRefresh && _cache.TryGetValue(cacheKey, out T? latest)) return latest;
                }

                return value;
            },
            cancellationToken);
    }

    private Task<T> GetOrCreateRequiredAsync<T>(
        string cacheKey,
        TimeSpan duration,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken,
        bool forceRefresh)
        => TraceLookupAsync(cacheKey, forceRefresh, () => GetOrCreateRequiredCoreAsync(
            SourceCacheKey(cacheKey), duration, factory, cancellationToken, forceRefresh));

    private async Task<T> GetOrCreateRequiredCoreAsync<T>(
        string cacheKey,
        TimeSpan duration,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var memoryStarted = Stopwatch.GetTimestamp();
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out T? cached) && cached is not null)
        {
            RecordMemoryLookup(cacheKey, "hit", memoryStarted);
            return cached;
        }

        RecordMemoryLookup(cacheKey, forceRefresh ? "bypassed" : "miss", memoryStarted);

        var flightKey = forceRefresh ? $"refresh:{cacheKey}" : $"cache:{cacheKey}";
        return await _singleFlight.RunAsync(
            flightKey,
            async token =>
            {
                var flightMemoryStarted = Stopwatch.GetTimestamp();
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out T? flightCached) && flightCached is not null)
                {
                    RecordMemoryLookup(cacheKey, "shared_hit", flightMemoryStarted);
                    return flightCached;
                }

                using var publication = BeginReusePublication(cacheKey, forceRefresh);
                var value = await factory(token);
                if (!publication.TryPublish(() => _cache.Set(cacheKey, value, duration))
                    && !forceRefresh && _cache.TryGetValue(cacheKey, out T? latest) && latest is not null) return latest;
                return value;
            },
            cancellationToken);
    }

    private async Task<T?> SendJsonAsync<T>(
        string path,
        string operation,
        CancellationToken cancellationToken,
        bool notFoundReturnsNull,
        bool verifyNotFound = false)
    {
        try
        {
            using var response = await SendWithRateLimitAndRetryAsync(path, operation, cancellationToken);

            if (notFoundReturnsNull && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (verifyNotFound)
                {
                    try
                    {
                        var error = await ReadJsonContentAsync<JsonElement>(response.Content, cancellationToken);
                        if (error.ValueKind == JsonValueKind.Object
                            && error.TryGetProperty("status_code", out var code) && code.ValueKind == JsonValueKind.Number
                            && code.TryGetInt32(out var status)
                            && status is 6 or 34
                            && error.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                            throw new VerifiedNotFoundException();
                    }
                    catch (JsonException) { /* An unverified 404 is never retained as a negative. */ }
                }
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                var failure = $"{operation} failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.";
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    // Keep the retry time so callers can defer (RetryDeadline) instead of treating this as a permanent miss.
                    var retryAt = ProviderFailure.FromResponse(response, DateTimeOffset.UtcNow).RetryAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(1);
                    throw new ExternalApiException(failure, new ProviderRetryAfterException("tmdb", retryAt, response.StatusCode));
                }
                throw new ExternalApiException(failure);
            }

            var value = await ReadJsonContentAsync<T>(response.Content, cancellationToken);
            if (value is null)
            {
                throw new ExternalApiException($"{operation} returned an empty response.");
            }

            return value;
        }
        catch (ExternalApiException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ExternalApiException($"{operation} returned invalid JSON.", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new ExternalApiException($"{operation} returned invalid compressed JSON.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalApiException($"{operation} could not reach TMDb.", ex);
        }
    }

    private async Task<T?> ReadJsonContentAsync<T>(HttpContent content, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        try
        {
            await using var contentStream = await OpenDecodedContentStreamAsync(content, cancellationToken);
            var value = await JsonSerializer.DeserializeAsync<T>(contentStream, JsonOptions, cancellationToken);
            outcome = value is null ? "empty" : "success";
            return value;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            _telemetry?.RecordDiagnostic("provider.parse.completed", details: new()
            {
                Provider = "tmdb", Stage = "json_decode", Outcome = outcome,
                DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }
    }

    private async Task<T> TraceLookupAsync<T>(string cacheKey, bool forceRefresh, Func<Task<T>> operation)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        try
        {
            var value = await operation();
            outcome = value is null ? "absent" : "success";
            return value;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            _telemetry?.RecordDiagnostic("provider.lookup.completed", details: new()
            {
                Provider = "tmdb", Stage = "lookup", Outcome = outcome,
                Reason = forceRefresh ? "forced" : "normal", CacheKey = DiagnosticCacheKey(cacheKey),
                DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }
    }

    private void RecordMemoryLookup(string cacheKey, string outcome, long started)
        => _telemetry?.RecordDiagnostic("provider.memory_cache.completed", details: new()
        {
            Provider = "tmdb", Stage = "memory_cache", Outcome = outcome,
            CacheKey = DiagnosticCacheKey(cacheKey),
            DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
        });

    private static string DiagnosticCacheKey(string cacheKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)));

    private static async Task<Stream> OpenDecodedContentStreamAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        foreach (var encoding in content.Headers.ContentEncoding.Reverse())
        {
            stream = encoding.ToLowerInvariant() switch
            {
                "gzip" or "x-gzip" => new GZipStream(stream, CompressionMode.Decompress),
                "deflate" => new DeflateStream(stream, CompressionMode.Decompress),
                "br" => new BrotliStream(stream, CompressionMode.Decompress),
                "identity" => stream,
                _ => stream
            };
        }

        return stream;
    }

    private async Task<HttpResponseMessage> SendWithRateLimitAndRetryAsync(
        string path,
        string operation,
        CancellationToken cancellationToken)
    {
        var bearerToken = await GetBearerTokenAsync(cancellationToken);
        using var lease = await _requestLimiter.AcquireAsync(cancellationToken);
        if (!lease.IsAcquired)
        {
            throw new ExternalApiException($"{operation} was rate limited locally before the request was sent.",
                new ProviderRetryAfterException("tmdb", DateTimeOffset.UtcNow.AddSeconds(15)));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private void EnsureConfigured()
    {
        // Configuration is checked immediately before sending requests because the token can be edited
        // from the Settings page and persisted in the local database.
    }

    private async ValueTask<string> GetBearerTokenAsync(CancellationToken cancellationToken)
    {
        var token = _settingsStore is null
            ? _options.BearerToken
            : (await _settingsStore.GetAsync(cancellationToken)).Sources.Tmdb.BearerToken;

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ExternalApiException("Missing TMDb bearer token. Configure it on the Settings page.");
        }

        return token.Trim();
    }

    private static string DiscoverFilterKey(TmdbDiscoverFilters filters)
    {
        return string.Join(
            ';',
            filters.SortBy,
            filters.OriginalLanguage,
            string.Join('|', filters.OriginCountries),
            string.Join('|', filters.GenreIds),
            filters.WatchRegion,
            string.Join('|', filters.WatchProviderIds),
            string.Join('|', filters.WatchMonetizationTypes),
            filters.MinVoteAverage,
            filters.MaxVoteAverage,
            filters.MinVoteCount,
            filters.RuntimeMinMinutes,
            filters.RuntimeMaxMinutes,
            string.Join('|', filters.KeywordIds),
            filters.UseEpisodeAirDate,
            string.Join('|', filters.NetworkIds),
            string.Join('|', filters.TvStatusIds),
            string.Join('|', filters.TvTypeIds),
            string.Join('|', filters.MovieReleaseTypes),
            filters.MovieCertificationCountry,
            string.Join('|', filters.MovieCertifications));
    }

    private static string NetworkKey(IReadOnlyList<int> networkIds)
    {
        return networkIds.Count == 0 ? "all" : string.Join('|', networkIds);
    }

    private string SourceCacheKey(string key) => $"tmdb-source:{_httpClient.BaseAddress?.AbsoluteUri ?? _options.BaseUrl}:{key}";

    private string DiscoverPagingKey(string key) => $"pages:{Math.Clamp(_options.MaxPagesPerQuery, 1, TmdbMaximumPage)}:"
        + $"{Math.Clamp(_options.MaxUnfilteredPagesPerQuery, 1, TmdbMaximumPage)}:{Math.Clamp(_options.PageBatchSize, 1, 50)}:{key}";

    private sealed class ReuseState
    {
        public object Gate { get; } = new();
        public Dictionary<string, DiscoveryFlight> DiscoveryFlights { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ReusePublicationState> Publications { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, LinkedListNode<NotFoundEntry>> NotFound { get; } = new(StringComparer.Ordinal);
        public LinkedList<NotFoundEntry> NotFoundLru { get; } = new();
    }

    // Retain completed batches only while callers are enumerating this query. A slow
    // subscriber replays the same pages, and the last subscriber cancels pending I/O.
    private sealed class DiscoveryFlight
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Dictionary<int, Task> _batches = new();
        private readonly ConcurrentDictionary<long, PriorityWaiter> _waiters = new();
        private long _nextWaiterId;
        private int _failed;
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public bool CanJoin => Volatile.Read(ref _failed) == 0;
        public int References;

        public Task<T> GetOrAdd<T>(int pageStart, Func<CancellationToken, Task<T>> factory)
        {
            lock (_gate)
            {
                if (!_batches.TryGetValue(pageStart, out var task))
                    _batches[pageStart] = task = RunBatchAsync(factory);
                return (Task<T>)task;
            }
        }

        public long AddWaiter(Func<ProviderWorkPriority> priority, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextWaiterId);
            _waiters[id] = new PriorityWaiter(priority, cancellationToken);
            return id;
        }

        public void RemoveWaiter(long id) => _waiters.TryRemove(id, out _);

        private async Task<T> RunBatchAsync<T>(Func<CancellationToken, Task<T>> factory)
        {
            // Admission queues retain this accessor and reevaluate it when selecting
            // work, so a foreground subscriber can promote an already queued page.
            using var priority = ProviderExecutionContext.Push(CurrentPriority);
            try { return await factory(_cancellation.Token); }
            catch
            {
                // Existing subscribers retain their ordered batches, but a later
                // caller must make a fresh attempt rather than replay this failure.
                Volatile.Write(ref _failed, 1);
                throw;
            }
        }

        private ProviderWorkPriority CurrentPriority() => _waiters
            .Where(pair => !pair.Value.Token.IsCancellationRequested)
            .Select(pair => pair.Value.Priority()).DefaultIfEmpty(ProviderWorkPriority.Maintenance).Min();

        private sealed record PriorityWaiter(Func<ProviderWorkPriority> Priority, CancellationToken Token);

        public async Task StopAsync()
        {
            try { await _cancellation.CancelAsync(); }
            catch (Exception) { /* Cleanup must not replace caller cancellation or a provider failure. */ }
            Task[] tasks;
            lock (_gate) tasks = _batches.Values.ToArray();
            try { await Task.WhenAll(tasks); }
            catch (Exception) { /* Observe every pending page before releasing its cancellation source. */ }
            _cancellation.Dispose();
        }
    }

    private ReusePublication BeginReusePublication(string key, bool forceRefresh)
    {
        lock (_reuseLock)
        {
            if (!_reusePublications.TryGetValue(key, out var state))
                _reusePublications[key] = state = new ReusePublicationState();
            state.References++;
            if (forceRefresh) state.Generation++;
            return new ReusePublication(this, key, state, forceRefresh);
        }
    }

    private sealed class ReusePublicationState
    {
        public long Generation;
        public int References;
    }

    private sealed class ReusePublication(TmdbClient owner, string key, ReusePublicationState state, bool forceRefresh) : IDisposable
    {
        private long _generation = state.Generation;

        public bool TryPublish(Action publish)
        {
            lock (owner._reuseLock)
            {
                if (state.Generation != _generation) return false;
                publish();
                // Fence normal operations that began while the refresh was fetching.
                if (forceRefresh) _generation = ++state.Generation;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._reuseLock)
            {
                if (--state.References == 0) owner._reusePublications.Remove(key);
            }
        }
    }

    private bool IsNotFoundCached(string key)
    {
        lock (_reuseLock)
        {
            if (!_notFound.TryGetValue(key, out var entry)) return false;
            if (entry.Value.ExpiresUtc <= DateTimeOffset.UtcNow)
            {
                ForgetNotFound(key);
                return false;
            }
            _notFoundLru.Remove(entry);
            _notFoundLru.AddFirst(entry);
            return true;
        }
    }

    private void RememberNotFound(string key)
    {
        lock (_reuseLock)
        {
            ForgetNotFound(key);
            _cache.Remove(key);
            _notFound[key] = _notFoundLru.AddFirst(new NotFoundEntry(key, DateTimeOffset.UtcNow + NotFoundLifetime));
            while (_notFound.Count > MaximumNotFoundEntries)
                ForgetNotFound(_notFoundLru.Last!.Value.Key);
        }
    }

    private void ForgetNotFound(string key)
    {
        lock (_reuseLock)
        {
            if (_notFound.Remove(key, out var entry)) _notFoundLru.Remove(entry);
        }
    }

    private sealed record NotFoundEntry(string Key, DateTimeOffset ExpiresUtc);
    private sealed class VerifiedNotFoundException : Exception { }
    private sealed record TmdbResponseInvalidation(bool Invalidated = true);
}
