using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class TvmazeClient : ITvmazeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConditionalWeakTable<IMemoryCache, object> LookupPublicationGates = new();

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly TvmazeOptions _options;
    private readonly IIntegrationSettingsStore? _settingsStore;
    private readonly ISingleFlightCoordinator _singleFlight;
    private readonly ProviderRequestThrottler _requestThrottler;
    private readonly object _lookupPublicationGate;
    private readonly PremiereTelemetry? _telemetry;

    public TvmazeClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<TvmazeOptions> options,
        IIntegrationSettingsStore? settingsStore = null,
        ISingleFlightCoordinator? singleFlight = null,
        ProviderRequestThrottler? requestThrottler = null,
        PremiereTelemetry? telemetry = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
        _singleFlight = singleFlight ?? new SingleFlightCoordinator();
        _requestThrottler = requestThrottler ?? new ProviderRequestThrottler();
        _lookupPublicationGate = LookupPublicationGates.GetValue(cache, static _ => new object());
        _telemetry = telemetry;
    }

    public async Task<TvmazeShow?> LookupShowAsync(
        int? tvdbId,
        string? imdbId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
        => (await LookupShowWithStatusAsync(tvdbId, imdbId, cancellationToken, forceRefresh)).Value;

    public async Task<ProviderRefreshResult<TvmazeShow?>> LookupShowWithStatusAsync(
        int? tvdbId, string? imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return new(null, true, new("disabled"));
        }

        var path = BuildLookupPath(tvdbId, imdbId);
        if (path is null)
        {
            return new(null, true, new("not_applicable"));
        }

        var cacheKey = $"tvmaze:lookup:{path}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out TvmazeShow? cached))
        {
            return new(cached, true);
        }

        return await _singleFlight.RunAsync<ProviderRefreshResult<TvmazeShow?>>(
            FlightKey(cacheKey, forceRefresh),
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out TvmazeShow? flightCached))
                {
                    return new(flightCached, true);
                }

                try
                {
                    using var response = await GetAsync(path, token);
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        _cache.Set<TvmazeShow?>(cacheKey, null, TimeSpan.FromHours(6));
                        return new(null, true);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        return new(null, false, ProviderFailure.FromResponse(response, DateTimeOffset.UtcNow));
                    }

                    var show = await response.Content.ReadFromJsonAsync<TvmazeShow>(JsonOptions, token);
                    if (show is { Id: > 0 })
                    {
                        lock (_lookupPublicationGate)
                        {
                            token.ThrowIfCancellationRequested();
                            _cache.Set(cacheKey, show, TimeSpan.FromDays(7));
                        }
                    }

                    return show is { Id: > 0 } ? new(show, true) : new(null, false, new("invalid_response", 200));
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return new(null, false, new("timeout"));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException exception)
                {
                    return new(null, false, ProviderFailure.FromException(exception));
                }
                catch (JsonException)
                {
                    return new(null, false, new("invalid_response"));
                }
            },
            cancellationToken);
    }

    public async Task<TvmazeShow?> SearchShowByNameAsync(
        string title,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
        => (await SearchShowByNameWithStatusAsync(title, cancellationToken, forceRefresh)).Value;

    public async Task<ProviderRefreshResult<TvmazeShow?>> SearchShowByNameWithStatusAsync(
        string title, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(title))
        {
            return new(null, true, new(settings.Enabled ? "not_applicable" : "disabled"));
        }

        var normalizedTitle = NormalizeTitle(title);
        if (normalizedTitle.Length == 0)
        {
            return new(null, true, new("not_applicable"));
        }

        var cacheKey = $"tvmaze:search:{normalizedTitle}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out TvmazeShow? cached))
        {
            return new(cached, true);
        }

        return await _singleFlight.RunAsync<ProviderRefreshResult<TvmazeShow?>>(
            FlightKey(cacheKey, forceRefresh),
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out TvmazeShow? flightCached))
                {
                    return new(flightCached, true);
                }

                try
                {
                    var path = $"search/shows?q={Uri.EscapeDataString(title.Trim())}";
                    using var response = await GetAsync(path, token);
                    if (!response.IsSuccessStatusCode)
                    {
                        return new(null, false, ProviderFailure.FromResponse(response, DateTimeOffset.UtcNow));
                    }

                    var results = await response.Content.ReadFromJsonAsync<IReadOnlyList<TvmazeSearchResult>>(
                        JsonOptions,
                        token);

                    if (results is null || results.Any(result => result?.Show is not { Id: > 0 }))
                        return new(null, false, new("invalid_response", 200));

                    var show = results?
                        .Select(result => result.Show)
                        .FirstOrDefault(show => NormalizeTitle(show?.Name) == normalizedTitle);

                    if (show is not null)
                    {
                        _cache.Set(cacheKey, show, TimeSpan.FromDays(7));
                    }

                    if (results is not null && show is null)
                        _cache.Set<TvmazeShow?>(cacheKey, null, TimeSpan.FromHours(6));
                    return new(show, results is not null);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return new(null, false, new("timeout"));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException exception)
                {
                    return new(null, false, ProviderFailure.FromException(exception));
                }
                catch (JsonException)
                {
                    return new(null, false, new("invalid_response"));
                }
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<TvmazeShowImage>> GetShowImagesAsync(
        int showId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
        => (await GetShowImagesWithStatusAsync(showId, cancellationToken, forceRefresh)).Value;

    public async Task<ProviderRefreshResult<IReadOnlyList<TvmazeShowImage>>> GetShowImagesWithStatusAsync(int showId, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled || showId <= 0)
        {
            return new([], true, new("disabled"));
        }

        var cacheKey = $"tvmaze:images:{showId}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<TvmazeShowImage>? cached) && cached is not null)
        {
            return new(cached, true);
        }

        return await _singleFlight.RunAsync<ProviderRefreshResult<IReadOnlyList<TvmazeShowImage>>>(
            FlightKey(cacheKey, forceRefresh),
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<TvmazeShowImage>? flightCached) && flightCached is not null)
                {
                    return new(flightCached, true);
                }

                try
                {
                    using var response = await GetAsync($"shows/{showId}/images", token);
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        _cache.Set<IReadOnlyList<TvmazeShowImage>>(cacheKey, [], TimeSpan.FromHours(6));
                        return new([], true);
                    }
                    if (!response.IsSuccessStatusCode) return new([], false, ProviderFailure.FromResponse(response, DateTimeOffset.UtcNow));

                    var images = await response.Content.ReadFromJsonAsync<IReadOnlyList<TvmazeShowImage>>(
                        JsonOptions,
                        token);
                    if (images is null) return new([], false, new("invalid_response", 200));

                    _cache.Set(cacheKey, images, TimeSpan.FromDays(7));
                    return new(images, true);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return new([], false, new("timeout"));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException exception)
                {
                    return new([], false, ProviderFailure.FromException(exception));
                }
                catch (JsonException)
                {
                    return new([], false, new("invalid_response"));
                }
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<TvmazeScheduleEpisode>> GetScheduleAsync(
        DateOnly date,
        string? country,
        bool webSchedule,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var started = Stopwatch.GetTimestamp();
        var context = _telemetry?.CaptureDiagnosticContext();
        var outcome = "failed";
        var cache = "miss";
        var count = 0;
        try
        {
            var result = await GetScheduleCoreAsync(date, country, webSchedule, cancellationToken, forceRefresh);
            outcome = result.Outcome;
            cache = result.Cache;
            count = result.Episodes.Count;
            return result.Episodes;
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            throw;
        }
        finally
        {
            _telemetry?.RecordDiagnostic("provider.tvmaze.schedule.completed", context, new()
            {
                Provider = "tvmaze", Stage = "schedule", Outcome = outcome, Cache = cache, Count = count,
                Reason = forceRefresh ? "force_refresh" : null,
                DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }
    }

    private async Task<ScheduleResult> GetScheduleCoreAsync(
        DateOnly date, string? country, bool webSchedule, CancellationToken cancellationToken, bool forceRefresh)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled || !settings.EnableScheduleDiscovery)
        {
            return new([], "skipped", "none");
        }

        var countryKey = country ?? "";
        var cacheKey = $"tvmaze:schedule:{(webSchedule ? "web" : "broadcast")}:{date:yyyyMMdd}:{countryKey}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<TvmazeScheduleEpisode>? cached) && cached is not null)
        {
            return new(cached, cached.Count == 0 ? "absent" : "completed", "hit");
        }

        return await _singleFlight.RunAsync(
            FlightKey(cacheKey, forceRefresh),
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<TvmazeScheduleEpisode>? flightCached) && flightCached is not null)
                {
                    return new ScheduleResult(flightCached, flightCached.Count == 0 ? "absent" : "completed", "hit");
                }

                try
                {
                    var path = BuildSchedulePath(date, country, webSchedule);
                    using var response = await GetWithRetryAsync(path, token);
                    if (!response.IsSuccessStatusCode)
                    {
                        return new ScheduleResult([], "failed", "miss");
                    }

                    var episodes = await response.Content.ReadFromJsonAsync<IReadOnlyList<TvmazeScheduleEpisode>>(
                        JsonOptions,
                        token) ?? [];

                    token.ThrowIfCancellationRequested();
                    var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(60);
                    PrimeScheduleLookups(episodes, expiresAtUtc, token);
                    _cache.Set(cacheKey, episodes, expiresAtUtc);
                    return new ScheduleResult(episodes, episodes.Count == 0 ? "absent" : "completed", "miss");
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return new ScheduleResult([], "timeout", "miss");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException)
                {
                    return new ScheduleResult([], "failed", "miss");
                }
                catch (JsonException)
                {
                    return new ScheduleResult([], "failed", "miss");
                }
            },
            cancellationToken);
    }

    private sealed record ScheduleResult(IReadOnlyList<TvmazeScheduleEpisode> Episodes, string Outcome, string Cache);

    private void PrimeScheduleLookups(IReadOnlyList<TvmazeScheduleEpisode> episodes, DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var context = _telemetry?.CaptureDiagnosticContext();
        var candidates = new Dictionary<string, TvmazeShow?>(StringComparer.Ordinal);
        var seeded = 0;
        var outcome = "failed";
        try
        {
            foreach (var episode in episodes)
            {
                var show = episode?.Show ?? episode?.Embedded?.Show;
                if (show is null || show.Id <= 0 || string.IsNullOrWhiteSpace(show.Name)) continue;
                if (show.Externals?.TheTvdb is > 0)
                    AddCandidate(BuildLookupPath(show.Externals.TheTvdb, null)!, show);
                var imdbId = show.Externals?.Imdb;
                if (imdbId is { Length: >= 9 } && imdbId.StartsWith("tt", StringComparison.Ordinal)
                    && imdbId.Skip(2).All(char.IsAsciiDigit) && imdbId.Skip(2).Any(character => character != '0'))
                    AddCandidate(BuildLookupPath(null, imdbId)!, show);
            }

            // Lookup responses and schedule seeding share this per-cache gate across
            // transient clients. No network or parsing work runs while it is held.
            lock (_lookupPublicationGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var (key, show) in candidates)
                {
                    if (show is null || _cache.TryGetValue(key, out _)) continue;
                    _cache.Set(key, show, expiresAtUtc);
                    seeded++;
                }
            }
            outcome = candidates.Count == 0 ? "absent" : "completed";
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            _telemetry?.RecordDiagnostic("provider.tvmaze.schedule_prime.completed", context, new()
            {
                Provider = "tvmaze", Stage = "schedule_prime", Outcome = outcome,
                Count = seeded, Total = candidates.Count,
                DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }

        void AddCandidate(string path, TvmazeShow show)
        {
            var key = $"tvmaze:lookup:{path}";
            if (!candidates.TryGetValue(key, out var previous)) candidates.Add(key, show);
            else if (previous?.Id != show.Id) candidates[key] = null;
        }
    }

    public Task<IReadOnlyList<TvmazeShowUpdate>> GetShowUpdatesAsync(
        TvmazeUpdateWindow since,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
        => GetShowUpdatesCoreAsync(since, cancellationToken, forceRefresh, requireSuccess: false);

    public Task<IReadOnlyList<TvmazeShowUpdate>> GetShowUpdatesCheckedAsync(
        TvmazeUpdateWindow since, CancellationToken cancellationToken)
        // A coverage watermark must refer to a real fetch, not a cached response
        // whose sliding window ended up to an hour earlier.
        => GetShowUpdatesCoreAsync(since, cancellationToken, forceRefresh: true, requireSuccess: true);

    private async Task<IReadOnlyList<TvmazeShowUpdate>> GetShowUpdatesCoreAsync(
        TvmazeUpdateWindow since, CancellationToken cancellationToken, bool forceRefresh, bool requireSuccess)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return [];
        }

        var sinceKey = since.ToString().ToLowerInvariant();
        var cacheKey = $"tvmaze:updates:shows:{sinceKey}";
        if (!forceRefresh
            && _cache.TryGetValue(cacheKey, out IReadOnlyList<TvmazeShowUpdate>? cached)
            && cached is not null)
        {
            return cached;
        }

        return await _singleFlight.RunAsync(
            FlightKey(cacheKey, forceRefresh) + (requireSuccess ? ":checked" : ":optional"),
            async token =>
            {
                if (!forceRefresh
                    && _cache.TryGetValue(cacheKey, out IReadOnlyList<TvmazeShowUpdate>? flightCached)
                    && flightCached is not null)
                {
                    return flightCached;
                }

                try
                {
                    using var response = await GetWithRetryAsync($"updates/shows?since={sinceKey}", token);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (requireSuccess) throw new ExternalApiException("TVmaze update check failed.");
                        return [];
                    }

                    var values = await response.Content.ReadFromJsonAsync<Dictionary<string, long>>(
                        JsonOptions,
                        token);
                    if (requireSuccess && values is null)
                        throw new ExternalApiException("TVmaze updates returned an empty response.");
                    values ??= [];
                    if (requireSuccess && values.Keys.Any(key => !int.TryParse(key, out var id) || id <= 0))
                        throw new ExternalApiException("TVmaze updates contained an invalid show identity.");
                    var updates = values
                        .Select(pair => int.TryParse(pair.Key, out var showId) && showId > 0
                            ? new TvmazeShowUpdate(showId, DateTimeOffset.FromUnixTimeSeconds(pair.Value))
                            : null)
                        .Where(update => update is not null)
                        .Select(update => update!)
                        .OrderBy(update => update.ShowId)
                        .ToArray();

                    _cache.Set(cacheKey, updates, TimeSpan.FromMinutes(60));
                    return updates;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    if (requireSuccess) throw;
                    return [];
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException)
                {
                    if (requireSuccess) throw;
                    return [];
                }
                catch (JsonException)
                {
                    if (requireSuccess) throw;
                    return [];
                }
            },
            cancellationToken);
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        return await GetAsync(path, cancellationToken);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var lease = await _requestThrottler.AcquireAsync(
            "tvmaze",
            _options.MaxConcurrentRequests,
            cancellationToken);
        return await _httpClient.GetAsync(path, cancellationToken);
    }

    private static string? BuildLookupPath(int? tvdbId, string? imdbId)
    {
        if (tvdbId is > 0)
        {
            return $"lookup/shows?thetvdb={tvdbId.Value}";
        }

        return string.IsNullOrWhiteSpace(imdbId)
            ? null
            : $"lookup/shows?imdb={Uri.EscapeDataString(imdbId)}";
    }

    private static string BuildSchedulePath(DateOnly date, string? country, bool webSchedule)
    {
        var path = webSchedule ? "schedule/web" : "schedule";
        var parameters = new List<string>
        {
            $"date={Uri.EscapeDataString(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))}"
        };

        if (!string.IsNullOrWhiteSpace(country) || webSchedule)
        {
            parameters.Add($"country={Uri.EscapeDataString(country ?? "")}");
        }

        return $"{path}?{string.Join('&', parameters)}";
    }

    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }

        return new string(title
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private async ValueTask<TvmazeSourceSettings> GetEffectiveSettingsAsync(CancellationToken cancellationToken)
    {
        return _settingsStore is null
            ? new TvmazeSourceSettings
            {
                Enabled = _options.Enabled,
                EnableScheduleDiscovery = _options.EnableScheduleDiscovery,
                ScheduleCountries = _options.ScheduleCountries
            }
            : (await _settingsStore.GetAsync(cancellationToken)).Sources.Tvmaze;
    }

    private static string FlightKey(string cacheKey, bool forceRefresh)
    {
        return forceRefresh ? $"refresh:{cacheKey}" : $"cache:{cacheKey}";
    }
}
