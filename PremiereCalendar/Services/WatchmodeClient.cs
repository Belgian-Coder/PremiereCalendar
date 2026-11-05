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
    private readonly PremiereTelemetry? _telemetry;
    private readonly OptionalProviderResponseCache _responses;

    public WatchmodeClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<WatchmodeOptions> options,
        IIntegrationSettingsStore? settingsStore = null,
        ISingleFlightCoordinator? singleFlight = null,
        ProviderRequestThrottler? requestThrottler = null,
        PremiereTelemetry? telemetry = null,
        ProviderResponseCache? responseCache = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
        _singleFlight = singleFlight ?? new SingleFlightCoordinator();
        _requestThrottler = requestThrottler ?? new ProviderRequestThrottler();
        _telemetry = telemetry;
        _responses = new(cache, responseCache, timeProvider);
    }

    public async Task<IReadOnlyList<PremiereSource>> GetTitleSourcesAsync(
        PremiereMediaType mediaType,
        int tmdbId,
        string? imdbId,
        IReadOnlyList<string> regions,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
        => (await GetTitleSourcesWithStatusAsync(mediaType, tmdbId, imdbId, regions, cancellationToken, forceRefresh)).Value;

    public Task<ProviderRefreshResult<IReadOnlyList<PremiereSource>>> GetTitleSourcesWithStatusAsync(
        PremiereMediaType mediaType, int tmdbId, string? imdbId, IReadOnlyList<string> regions,
        CancellationToken cancellationToken, bool forceRefresh = false)
        => GetTitleSourcesWithRefreshPolicyAsync(mediaType, tmdbId, imdbId, regions, cancellationToken, forceRefresh, forceIdentityRefresh: forceRefresh);

    public async Task<ProviderRefreshResult<IReadOnlyList<PremiereSource>>> GetTitleSourcesWithRefreshPolicyAsync(
        PremiereMediaType mediaType, int tmdbId, string? imdbId, IReadOnlyList<string> regions,
        CancellationToken cancellationToken, bool forceRefresh = false, bool forceIdentityRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled || !settings.EnableAvailabilityEnrichment || tmdbId <= 0)
        {
            ProviderOutcomeDiagnostics.Record(_telemetry, "watchmode", "skipped", "disabled");
            return new([], true, new("disabled"));
        }
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            ProviderOutcomeDiagnostics.Record(_telemetry, "watchmode", "skipped", "not_configured");
            return new([], true, new("not_configured"));
        }

        using var enrichmentBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        enrichmentBudget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(
            _options.AvailabilityEnrichmentBudgetSeconds,
            1,
            Math.Max(1, _options.RequestTimeoutSeconds))));
        var enrichmentToken = enrichmentBudget.Token;

        var resolution = await FindTitleIdAsync(mediaType, tmdbId, imdbId, settings, enrichmentToken, forceIdentityRefresh);
        var titleId = resolution.Value;
        if (titleId is not > 0)
        {
            return new([], resolution.Successful, resolution.Failure);
        }

        var sourceRegions = NormalizeRegions(regions.Count > 0 ? regions : settings.Regions);
        var regionsKey = string.Join(',', sourceRegions);
        var partition = OptionalProviderResponseCache.Partition(_httpClient.BaseAddress, settings.ApiKey,
            $"sources:{titleId.Value}:{regionsKey}:{CacheDuration(settings).Ticks}");
        return await _responses.GetAsync<IReadOnlyList<PremiereSource>>("watchmode", partition, forceRefresh,
            CacheDuration(settings), async token =>
            {
                var sources = await GetTitleSourceDtosAsync(titleId.Value, settings.ApiKey.Trim(), sourceRegions, token);
                if (!sources.IsSuccess && sources.Value is not { Count: > 0 })
                    return new([], false, sources.Failure);
                if (sources.Value?.Any(source => source is null || string.IsNullOrWhiteSpace(source.Name)) == true)
                    return new([], false, new("invalid_response", 200));
                var mapped = (sources.Value ?? [])
                    .Select(ToPremiereSource)
                    .Where(source => source is not null)
                    .Select(source => source!)
                    .DistinctBy(source => $"{source.Kind}:{source.Id}:{source.Name}", StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new(mapped, resolution.Successful && sources.IsSuccess, sources.Failure ?? resolution.Failure);
            }, enrichmentToken, unavailableFallback: Array.Empty<PremiereSource>());
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

                var apiKey = settings.ApiKey.Trim();
                var query = ToQueryString(new Dictionary<string, string?>
                {
                    ["start_date"] = start.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    ["end_date"] = end.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    ["limit"] = "250"
                });
                var response = await GetJsonWithRetryResultAsync<WatchmodeReleasesResponse>(
                    $"releases/{query}",
                    apiKey,
                    token);
                if (!response.IsSuccess)
                {
                    throw new ProviderRequestException("watchmode", response.Failure ?? new("invalid_response"));
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
        var query = new Dictionary<string, string?>();
        if (sourceRegions.Length > 0)
        {
            query["regions"] = regionsKey;
        }

        var combined = await GetJsonWithRetryResultAsync<IReadOnlyList<WatchmodeTitleSource>>(
            $"title/{titleId.ToString(CultureInfo.InvariantCulture)}/sources/{ToQueryString(query)}",
            apiKey,
            cancellationToken);
        if (combined.IsSuccess || sourceRegions.Length <= 1
            || combined.Failure is { StatusCode: 401 or 403 or 429 } or { IsLocal: true })
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
            return new JsonFetchResult<IReadOnlyList<WatchmodeTitleSource>>([], IsSuccess: false, regionResults.Select(result => result.Failure).FirstOrDefault(failure => failure is not null));
        }

        return new JsonFetchResult<IReadOnlyList<WatchmodeTitleSource>>(
            successfulRegionResults
                .SelectMany(result => result.Value ?? [])
                .ToArray(),
            IsSuccess: successfulRegionResults.Length == sourceRegions.Length,
            regionResults.Select(result => result.Failure).FirstOrDefault(failure => failure is not null));
    }

    private async Task<JsonFetchResult<IReadOnlyList<WatchmodeTitleSource>>> GetTitleSourcesForRegionAsync(
        int titleId,
        string apiKey,
        string region,
        CancellationToken cancellationToken)
    {
        var regionQuery = ToQueryString(new Dictionary<string, string?>
        {
            ["regions"] = region
        });
        return await GetJsonWithRetryResultAsync<IReadOnlyList<WatchmodeTitleSource>>(
            $"title/{titleId.ToString(CultureInfo.InvariantCulture)}/sources/{regionQuery}",
            apiKey,
            cancellationToken);
    }

    private async Task<ProviderRefreshResult<int?>> FindTitleIdAsync(
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
        if (tmdbMatch.Value is > 0)
        {
            return tmdbMatch;
        }

        if (!tmdbMatch.Successful || string.IsNullOrWhiteSpace(imdbId)) return tmdbMatch;
        var imdbMatch = await SearchTitleIdAsync("imdb_id", imdbId, mediaType, settings, cancellationToken, forceRefresh);
        return new(imdbMatch.Value, tmdbMatch.Successful && imdbMatch.Successful, imdbMatch.Failure ?? tmdbMatch.Failure);
    }

    private async Task<ProviderRefreshResult<int?>> SearchTitleIdAsync(
        string field,
        string value,
        PremiereMediaType mediaType,
        WatchmodeSourceSettings settings,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var partition = OptionalProviderResponseCache.Partition(_httpClient.BaseAddress, settings.ApiKey,
            $"identity:{field}:{value}:{mediaType}:{CacheDuration(settings).Ticks}");
        return await _responses.GetAsync<int?>("watchmode", partition, forceRefresh,
            TimeSpan.FromDays(30), async token =>
            {
                var query = ToQueryString(new Dictionary<string, string?>
                {
                    ["search_field"] = field,
                    ["search_value"] = value,
                    ["types"] = mediaType == PremiereMediaType.Movie ? "movie" : "tv_series,tv_miniseries,tv_special"
                });
                var response = await GetJsonWithRetryResultAsync<WatchmodeSearchResponse>($"search/{query}", settings.ApiKey.Trim(), token);
                if (!response.IsSuccess || response.Value is not { HasTitleResults: true, TitleResults: not null }
                    || response.Value.TitleResults.Any(result => result is null || result.Id <= 0))
                    return new(null, false, response.Failure ?? new("invalid_response"));
                var titleId = response.Value.TitleResults.Select(result => (int?)result.Id).FirstOrDefault();
                ProviderOutcomeDiagnostics.Record(_telemetry, "watchmode", titleId is null ? "absent" : "completed", "verified_response");
                return new(titleId, true);
            }, cancellationToken, id => id is > 0 ? TimeSpan.FromDays(30) : CacheDuration(settings));
    }

    private async Task<JsonFetchResult<T>> GetJsonWithRetryResultAsync<T>(
        string path, string apiKey, CancellationToken cancellationToken)
    {
        var failures = new ProviderFailureCache(_cache, "watchmode", _httpClient.BaseAddress, apiKey);
        if (failures.Get(DateTimeOffset.UtcNow) is { } deferred) return Failed<T>(deferred);
        try
        {
            using var response = await GetAsync(path, apiKey, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var failure = ProviderFailure.FromResponse(response, DateTimeOffset.UtcNow);
                failures.Remember(failure, DateTimeOffset.UtcNow);
                return Failed<T>(failure);
            }
            var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
            return value is null ? Failed<T>(new("invalid_response")) : new(value, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or JsonException)
        {
            var failure = ProviderFailure.FromException(exception);
            failures.Remember(failure, DateTimeOffset.UtcNow);
            return Failed<T>(failure);
        }
    }

    private JsonFetchResult<T> Failed<T>(ProviderFailure failure)
    {
        _telemetry?.RecordDiagnostic("provider.outcome", _telemetry.CaptureDiagnosticContext(), new()
        {
            Provider = "watchmode", Outcome = "failed", Reason = failure.Reason,
            StatusCode = failure.StatusCode, RetryAtUtc = failure.RetryAtUtc, FromCache = failure.IsLocal
        });
        return new(default, false, failure);
    }

    private readonly record struct JsonFetchResult<T>(T? Value, bool IsSuccess, ProviderFailure? Failure = null);

    private async Task<HttpResponseMessage> GetAsync(
        string path,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var lease = await _requestThrottler.AcquireAsync(
            "watchmode",
            _options.MaxConcurrentRequests,
            cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
        return await _httpClient.SendAsync(request, cancellationToken);
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
            SeasonNumber: mediaType == PremiereMediaType.Series ? release.SeasonNumber : null,
            ExternalProviderId: release.Id > 0 ? release.Id.ToString(CultureInfo.InvariantCulture) : null,
            ReleaseYear: releaseDate.Year);
    }

    private static bool IsMovie(WatchmodeRelease release)
    {
        return string.Equals(release.TmdbType, "movie", StringComparison.OrdinalIgnoreCase)
            || string.Equals(release.Type, "movie", StringComparison.OrdinalIgnoreCase)
            || string.Equals(release.Type, "tv_movie", StringComparison.OrdinalIgnoreCase);
    }

    private static string CandidateKey(ExternalPremiereCandidate candidate)
    {
        return $"{candidate.MediaType}:{candidate.TmdbId}:{candidate.ImdbId}:{candidate.ExternalProviderId}:{candidate.PremiereDate:yyyyMMdd}:{candidate.Title}:{candidate.Source}";
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

}
