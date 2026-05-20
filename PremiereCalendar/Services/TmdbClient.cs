using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
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

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly TmdbOptions _options;
    private readonly IIntegrationSettingsStore? _settingsStore;
    private readonly TmdbRequestLimiter _requestLimiter;
    private readonly ILogger<TmdbClient> _logger;
    private readonly ISingleFlightCoordinator _singleFlight;

    public TmdbClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<TmdbOptions> options,
        ILogger<TmdbClient> logger,
        TmdbRequestLimiter? requestLimiter = null,
        IIntegrationSettingsStore? settingsStore = null,
        ISingleFlightCoordinator? singleFlight = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
        _requestLimiter = requestLimiter ?? new TmdbRequestLimiter(options);
        _logger = logger;
        _singleFlight = singleFlight ?? new SingleFlightCoordinator();
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

        var cacheKey = $"tmdb:discover-tv-networks:{start:yyyyMMdd}:{end:yyyyMMdd}:{NetworkKey(filteredNetworkIds)}";
        return GetOrCreateRequiredAsync(
            cacheKey,
            TimeSpan.FromHours(6),
            token => GetPagedAsync<TmdbTvDiscoverItem>(
                page => TmdbQueryBuilder.BuildDiscoverTvByNetworksPath(start, end, filteredNetworkIds, page),
                token),
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
                notFoundReturnsNull: true),
            cancellationToken,
            forceRefresh);
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
                notFoundReturnsNull: true),
            cancellationToken,
            forceRefresh);
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
            TimeSpan.FromDays(7),
            token => SendJsonAsync<TmdbFindResponse>(
                TmdbQueryBuilder.BuildFindByExternalIdPath(
                    Uri.EscapeDataString(externalId.Trim()),
                    externalSource.Trim()),
                "TMDb external ID lookup",
                token,
                notFoundReturnsNull: true),
            cancellationToken,
            forceRefresh);

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
        var response = await GetOrCreateRequiredAsync(
            $"tmdb:title-search:{mediaType}:{normalizedYear}:{normalizedQuery.ToLowerInvariant()}",
            TimeSpan.FromDays(7),
            token => SendJsonAsync<TmdbPagedResponse<TmdbTitleSearchResult>>(
                TmdbQueryBuilder.BuildSearchTitlePath(mediaType, normalizedQuery, year),
                "TMDb title search",
                token,
                notFoundReturnsNull: false),
            cancellationToken,
            forceRefresh);

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
                token),
            cancellationToken,
            forceRefresh);
    }

    private async Task<IReadOnlyList<T>> GetPagedAsync<T>(
        Func<int, string> pathFactory,
        CancellationToken cancellationToken)
    {
        var firstPage = await FetchDiscoverPageAsync<T>(pathFactory, 1, cancellationToken);
        if (firstPage is null)
        {
            return [];
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
                    return (Page: page, Response: await FetchDiscoverPageAsync<T>(pathFactory, page, cancellationToken));
                }
                finally
                {
                    gate.Release();
                }
            });

        var fetchedPages = await Task.WhenAll(remainingPageTasks);
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
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<T>? cached) && cached is not null)
        {
            yield return new TmdbDiscoverBatch<T>(1, 1, 1, cached.Count, cached);
            yield break;
        }

        var firstPage = await FetchDiscoverPageAsync<T>(pathFactory, 1, cancellationToken);
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
            _cache.Set(cacheKey, accumulated.ToArray(), cacheDuration);
            yield break;
        }

        var pageBatchSize = Math.Clamp(_options.PageBatchSize, 1, 50);
        for (var pageStart = 2; pageStart <= pagesToFetch; pageStart += pageBatchSize)
        {
            var pageEnd = Math.Min(pagesToFetch, pageStart + pageBatchSize - 1);
            var fetchedPages = await FetchPageRangeAsync<T>(pathFactory, pageStart, pageEnd, cancellationToken);
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

        _cache.Set(cacheKey, accumulated.ToArray(), cacheDuration);
    }

    private async Task<IReadOnlyList<TmdbPagedResponse<T>>> FetchPageRangeAsync<T>(
        Func<int, string> pathFactory,
        int pageStart,
        int pageEnd,
        CancellationToken cancellationToken)
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
                    return await FetchDiscoverPageAsync<T>(pathFactory, page, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var path = pathFactory(page);
        return _singleFlight.RunAsync(
            $"tmdb:discover-page:{path}",
            token => SendJsonAsync<TmdbPagedResponse<T>>(
                path,
                "TMDb discover",
                token,
                notFoundReturnsNull: false),
            cancellationToken);
    }

    private async Task<T?> GetOrCreateAsync<T>(
        string cacheKey,
        TimeSpan duration,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out T? cached))
        {
            return cached;
        }

        var flightKey = forceRefresh ? $"refresh:{cacheKey}" : $"cache:{cacheKey}";
        return await _singleFlight.RunAsync(
            flightKey,
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out T? flightCached))
                {
                    return flightCached;
                }

                var value = await factory(token);
                if (value is not null)
                {
                    _cache.Set(cacheKey, value, duration);
                }

                return value;
            },
            cancellationToken);
    }

    private async Task<T> GetOrCreateRequiredAsync<T>(
        string cacheKey,
        TimeSpan duration,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out T? cached) && cached is not null)
        {
            return cached;
        }

        var flightKey = forceRefresh ? $"refresh:{cacheKey}" : $"cache:{cacheKey}";
        return await _singleFlight.RunAsync(
            flightKey,
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out T? flightCached) && flightCached is not null)
                {
                    return flightCached;
                }

                var value = await factory(token);
                _cache.Set(cacheKey, value, duration);
                return value;
            },
            cancellationToken);
    }

    private async Task<T?> SendJsonAsync<T>(
        string path,
        string operation,
        CancellationToken cancellationToken,
        bool notFoundReturnsNull)
    {
        try
        {
            using var response = await SendWithRateLimitAndRetryAsync(path, operation, cancellationToken);

            if (notFoundReturnsNull && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ExternalApiException($"{operation} failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
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

    private static async Task<T?> ReadJsonContentAsync<T>(HttpContent content, CancellationToken cancellationToken)
    {
        await using var contentStream = await OpenDecodedContentStreamAsync(content, cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(contentStream, JsonOptions, cancellationToken);
    }

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
        const int maxAttempts = 3;
        var bearerToken = await GetBearerTokenAsync(cancellationToken);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var lease = await _requestLimiter.AcquireAsync(cancellationToken);
            if (!lease.IsAcquired)
            {
                throw new ExternalApiException($"{operation} was rate limited locally before the request was sent.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt == maxAttempts)
            {
                return response;
            }

            var delay = RetryAfterDelay(response) ?? TimeSpan.FromSeconds(Math.Min(4, attempt * 2));
            response.Dispose();
            _logger.LogWarning(
                "TMDb returned HTTP 429 for {Operation}. Waiting {RetryDelay} before retry {RetryAttempt}/{MaxAttempts}.",
                operation,
                delay,
                attempt + 1,
                maxAttempts);
            await Task.Delay(delay, cancellationToken);
        }

        throw new ExternalApiException($"{operation} was rate limited by TMDb.");
    }

    private static TimeSpan? RetryAfterDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : null;
        }

        return null;
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
}
