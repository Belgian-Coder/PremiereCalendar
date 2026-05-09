using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class WatchmodeClient : IWatchmodeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly WatchmodeOptions _options;
    private readonly IIntegrationSettingsStore? _settingsStore;
    private readonly ISingleFlightCoordinator _singleFlight;
    private readonly ProviderRequestThrottler _requestThrottler;
    private readonly object _rateLimitLock = new();
    private DateTimeOffset _rateLimitedUntilUtc = DateTimeOffset.MinValue;

    public WatchmodeClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<WatchmodeOptions> options,
        IIntegrationSettingsStore? settingsStore = null,
        ISingleFlightCoordinator? singleFlight = null,
        ProviderRequestThrottler? requestThrottler = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
        _singleFlight = singleFlight ?? new SingleFlightCoordinator();
        _requestThrottler = requestThrottler ?? new ProviderRequestThrottler();
    }

    public async Task<IReadOnlyList<PremiereSource>> GetTitleSourcesAsync(
        PremiereMediaType mediaType,
        int tmdbId,
        string? imdbId,
        IReadOnlyList<string> regions,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled
            || !settings.EnableAvailabilityEnrichment
            || string.IsNullOrWhiteSpace(settings.ApiKey)
            || tmdbId <= 0)
        {
            return [];
        }

        var titleId = await FindTitleIdAsync(mediaType, tmdbId, imdbId, settings, cancellationToken, forceRefresh);
        if (titleId is not > 0)
        {
            return [];
        }

        var sourceRegions = NormalizeRegions(regions.Count > 0 ? regions : settings.Regions);
        var regionsKey = string.Join(',', sourceRegions);
        var cacheKey = $"watchmode:sources:{titleId.Value}:{regionsKey}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<PremiereSource>? cached) && cached is not null)
        {
            return cached;
        }

        return await _singleFlight.RunAsync(
            FlightKey(cacheKey, forceRefresh),
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<PremiereSource>? flightCached) && flightCached is not null)
                {
                    return flightCached;
                }

                var sources = await GetTitleSourceDtosAsync(titleId.Value, settings.ApiKey.Trim(), sourceRegions, token);
                if (!sources.IsSuccess)
                {
                    return [];
                }

                var mapped = (sources.Value ?? [])
                    .Select(ToPremiereSource)
                    .Where(source => source is not null)
                    .Select(source => source!)
                    .DistinctBy(source => $"{source.Kind}:{source.Id}:{source.Name}", StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                _cache.Set(cacheKey, mapped, CacheDuration(settings));
                return mapped;
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<ExternalPremiereCandidate>> GetReleaseCandidatesAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled
            || !settings.EnableReleaseDiscovery
            || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return [];
        }

        var cacheKey = $"watchmode:releases:{start:yyyyMMdd}:{end:yyyyMMdd}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<ExternalPremiereCandidate>? cached) && cached is not null)
        {
            return cached;
        }

        return await _singleFlight.RunAsync(
            FlightKey(cacheKey, forceRefresh),
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<ExternalPremiereCandidate>? flightCached) && flightCached is not null)
                {
                    return flightCached;
                }

                var query = ToQueryString(new Dictionary<string, string?>
                {
                    ["apiKey"] = settings.ApiKey.Trim(),
                    ["start_date"] = start.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    ["end_date"] = end.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    ["limit"] = "250"
                });
                var response = await GetJsonWithRetryResultAsync<WatchmodeReleasesResponse>(
                    $"releases/{query}",
                    token);
                if (!response.IsSuccess)
                {
                    return [];
                }

                var candidates = (response.Value?.Releases ?? [])
                    .Select(ToExternalCandidate)
                    .Where(candidate => candidate is not null)
                    .Select(candidate => candidate!)
                    .DistinctBy(CandidateKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                _cache.Set(cacheKey, candidates, CacheDuration(settings));
                return candidates;
            },
            cancellationToken);
    }

    private async Task<JsonFetchResult<IReadOnlyList<WatchmodeTitleSource>>> GetTitleSourceDtosAsync(
        int titleId,
        string apiKey,
        string[] sourceRegions,
        CancellationToken cancellationToken)
    {
        var regionsKey = string.Join(',', sourceRegions);
        var query = new Dictionary<string, string?>
        {
            ["apiKey"] = apiKey
        };
        if (sourceRegions.Length > 0)
        {
            query["regions"] = regionsKey;
        }

        var combined = await GetJsonWithRetryResultAsync<IReadOnlyList<WatchmodeTitleSource>>(
            $"title/{titleId.ToString(CultureInfo.InvariantCulture)}/sources/{ToQueryString(query)}",
            cancellationToken);
        if (combined.IsSuccess || sourceRegions.Length <= 1)
        {
            return combined;
        }

        var regionTasks = sourceRegions
            .Select(region => GetTitleSourcesForRegionAsync(titleId, apiKey, region, cancellationToken))
            .ToArray();
        var regionResults = await Task.WhenAll(regionTasks);
        var successfulRegionResults = regionResults
            .Where(result => result.IsSuccess)
            .ToArray();
        if (successfulRegionResults.Length == 0)
        {
            return new JsonFetchResult<IReadOnlyList<WatchmodeTitleSource>>([], IsSuccess: false);
        }

        return new JsonFetchResult<IReadOnlyList<WatchmodeTitleSource>>(
            successfulRegionResults
                .SelectMany(result => result.Value ?? [])
                .ToArray(),
            IsSuccess: true);
    }

    private async Task<JsonFetchResult<IReadOnlyList<WatchmodeTitleSource>>> GetTitleSourcesForRegionAsync(
        int titleId,
        string apiKey,
        string region,
        CancellationToken cancellationToken)
    {
        var regionQuery = ToQueryString(new Dictionary<string, string?>
        {
            ["apiKey"] = apiKey,
            ["regions"] = region
        });
        return await GetJsonWithRetryResultAsync<IReadOnlyList<WatchmodeTitleSource>>(
            $"title/{titleId.ToString(CultureInfo.InvariantCulture)}/sources/{regionQuery}",
            cancellationToken);
    }

    private async Task<int?> FindTitleIdAsync(
        PremiereMediaType mediaType,
        int tmdbId,
        string? imdbId,
        WatchmodeSourceSettings settings,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var tmdbField = mediaType == PremiereMediaType.Movie ? "tmdb_movie_id" : "tmdb_tv_id";
        var tmdbMatch = await SearchTitleIdAsync(
            tmdbField,
            tmdbId.ToString(CultureInfo.InvariantCulture),
            mediaType,
            settings,
            cancellationToken,
            forceRefresh);
        if (tmdbMatch is > 0)
        {
            return tmdbMatch;
        }

        return string.IsNullOrWhiteSpace(imdbId)
            ? null
            : await SearchTitleIdAsync("imdb_id", imdbId, mediaType, settings, cancellationToken, forceRefresh);
    }

    private async Task<int?> SearchTitleIdAsync(
        string field,
        string value,
        PremiereMediaType mediaType,
        WatchmodeSourceSettings settings,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var cacheKey = $"watchmode:search:{field}:{value}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out int? cached) && cached is > 0)
        {
            return cached;
        }

        return await _singleFlight.RunAsync(
            FlightKey(cacheKey, forceRefresh),
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out int? flightCached) && flightCached is > 0)
                {
                    return flightCached;
                }

                var query = ToQueryString(new Dictionary<string, string?>
                {
                    ["apiKey"] = settings.ApiKey.Trim(),
                    ["search_field"] = field,
                    ["search_value"] = value,
                    ["types"] = mediaType == PremiereMediaType.Movie ? "movie" : "tv_series,tv_miniseries,tv_special"
                });
                var response = await GetJsonWithRetryAsync<WatchmodeSearchResponse>($"search/{query}", token);
                var titleId = response?.TitleResults
                    .Where(result => result.Id > 0)
                    .Select(result => (int?)result.Id)
                    .FirstOrDefault();

                if (titleId is > 0)
                {
                    _cache.Set(cacheKey, titleId, CacheDuration(settings));
                }

                return titleId;
            },
            cancellationToken);
    }

    private async Task<T?> GetJsonWithRetryAsync<T>(string path, CancellationToken cancellationToken)
    {
        return (await GetJsonWithRetryResultAsync<T>(path, cancellationToken)).Value;
    }

    private async Task<JsonFetchResult<T>> GetJsonWithRetryResultAsync<T>(string path, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        if (IsRateLimited())
        {
            return new JsonFetchResult<T>(default, IsSuccess: false);
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var response = await GetAsync(path, cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxAttempts)
                {
                    var delay = RetryAfterDelay(response) ?? TimeSpan.FromSeconds(Math.Min(4, attempt * 2));
                    MarkRateLimited(delay);
                    if (delay > MaxRetryAfterDelay())
                    {
                        return new JsonFetchResult<T>(default, IsSuccess: false);
                    }

                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (RetryAfterDelay(response) is { } retryAfter)
                    {
                        MarkRateLimited(retryAfter);
                    }

                    return new JsonFetchResult<T>(default, IsSuccess: false);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new JsonFetchResult<T>(default, IsSuccess: false);
                }

                return new JsonFetchResult<T>(
                    await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken),
                    IsSuccess: true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new JsonFetchResult<T>(default, IsSuccess: false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                return new JsonFetchResult<T>(default, IsSuccess: false);
            }
            catch (JsonException)
            {
                return new JsonFetchResult<T>(default, IsSuccess: false);
            }
        }

        return new JsonFetchResult<T>(default, IsSuccess: false);
    }

    private readonly record struct JsonFetchResult<T>(T? Value, bool IsSuccess);

    private bool IsRateLimited()
    {
        lock (_rateLimitLock)
        {
            return _rateLimitedUntilUtc > DateTimeOffset.UtcNow;
        }
    }

    private void MarkRateLimited(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        var limitedUntilUtc = DateTimeOffset.UtcNow.Add(delay);
        lock (_rateLimitLock)
        {
            if (limitedUntilUtc > _rateLimitedUntilUtc)
            {
                _rateLimitedUntilUtc = limitedUntilUtc;
            }
        }
    }

    private TimeSpan MaxRetryAfterDelay()
    {
        return TimeSpan.FromSeconds(Math.Clamp(_options.MaxRetryAfterDelaySeconds, 0, 60));
    }

    private async Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var lease = await _requestThrottler.AcquireAsync(
            "watchmode",
            _options.MaxConcurrentRequests,
            cancellationToken);
        return await _httpClient.GetAsync(path, cancellationToken);
    }

    private async ValueTask<WatchmodeSourceSettings> GetEffectiveSettingsAsync(CancellationToken cancellationToken)
    {
        return _settingsStore is null
            ? new WatchmodeSourceSettings
            {
                Enabled = _options.Enabled,
                ApiKey = _options.ApiKey ?? "",
                Regions = _options.Regions,
                EnableReleaseDiscovery = _options.EnableReleaseDiscovery,
                EnableAvailabilityEnrichment = _options.EnableAvailabilityEnrichment
            }
            : (await _settingsStore.GetAsync(cancellationToken)).Sources.Watchmode;
    }

    private TimeSpan CacheDuration(WatchmodeSourceSettings settings)
    {
        return TimeSpan.FromHours(Math.Max(1, settings.CacheHours ?? _options.CacheHours));
    }

    private static string FlightKey(string cacheKey, bool forceRefresh)
    {
        return forceRefresh ? $"refresh:{cacheKey}" : $"cache:{cacheKey}";
    }

    private static PremiereSource? ToPremiereSource(WatchmodeTitleSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Name))
        {
            return null;
        }

        return new PremiereSource
        {
            Name = source.Name.Trim(),
            Id = source.SourceId > 0 ? source.SourceId : null,
            Kind = source.Type?.Trim().ToLowerInvariant() switch
            {
                "sub" => "flatrate",
                "free" => "free",
                "rent" => "rent",
                "buy" => "buy",
                "tve" => "provider",
                _ => "provider"
            }
        };
    }

    private static ExternalPremiereCandidate? ToExternalCandidate(WatchmodeRelease release)
    {
        if (!TryParseDate(release.SourceReleaseDate, out var releaseDate)
            || release.TmdbId is not > 0
            || string.IsNullOrWhiteSpace(release.Title))
        {
            return null;
        }

        var mediaType = IsMovie(release)
            ? PremiereMediaType.Movie
            : PremiereMediaType.Series;

        return new ExternalPremiereCandidate(
            mediaType,
            releaseDate,
            release.Title,
            release.TmdbId,
            release.ImdbId,
            null,
            string.IsNullOrWhiteSpace(release.SourceName) ? "Watchmode" : release.SourceName.Trim(),
            IsSeriesEpisode: false,
            SeasonNumber: mediaType == PremiereMediaType.Series ? release.SeasonNumber : null);
    }

    private static bool IsMovie(WatchmodeRelease release)
    {
        return string.Equals(release.TmdbType, "movie", StringComparison.OrdinalIgnoreCase)
            || string.Equals(release.Type, "movie", StringComparison.OrdinalIgnoreCase)
            || string.Equals(release.Type, "tv_movie", StringComparison.OrdinalIgnoreCase);
    }

    private static string CandidateKey(ExternalPremiereCandidate candidate)
    {
        return $"{candidate.MediaType}:{candidate.TmdbId}:{candidate.ImdbId}:{candidate.PremiereDate:yyyyMMdd}:{candidate.Source}";
    }

    private static string[] NormalizeRegions(IReadOnlyList<string> regions)
    {
        return regions
            .Where(region => !string.IsNullOrWhiteSpace(region))
            .Select(region => region.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateOnly.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
    }

    private static string ToQueryString(IReadOnlyDictionary<string, string?> values)
    {
        var parameters = values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");

        return "?" + string.Join('&', parameters);
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
}
