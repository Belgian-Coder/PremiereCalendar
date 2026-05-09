using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class TvmazeClient : ITvmazeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly TvmazeOptions _options;
    private readonly IIntegrationSettingsStore? _settingsStore;
    private readonly ISingleFlightCoordinator _singleFlight;
    private readonly ProviderRequestThrottler _requestThrottler;

    public TvmazeClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<TvmazeOptions> options,
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

    public async Task<TvmazeShow?> LookupShowAsync(
        int? tvdbId,
        string? imdbId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return null;
        }

        var path = BuildLookupPath(tvdbId, imdbId);
        if (path is null)
        {
            return null;
        }

        var cacheKey = $"tvmaze:lookup:{path}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out TvmazeShow? cached))
        {
            return cached;
        }

        return await _singleFlight.RunAsync(
            FlightKey(cacheKey, forceRefresh),
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out TvmazeShow? flightCached))
                {
                    return flightCached;
                }

                try
                {
                    using var response = await GetAsync(path, token);
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        return null;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var show = await response.Content.ReadFromJsonAsync<TvmazeShow>(JsonOptions, token);
                    if (show is not null)
                    {
                        _cache.Set(cacheKey, show, TimeSpan.FromDays(7));
                    }

                    return show;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException)
                {
                    return null;
                }
                catch (JsonException)
                {
                    return null;
                }
            },
            cancellationToken);
    }

    public async Task<TvmazeShow?> SearchShowByNameAsync(
        string title,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var normalizedTitle = NormalizeTitle(title);
        if (normalizedTitle.Length == 0)
        {
            return null;
        }

        var cacheKey = $"tvmaze:search:{normalizedTitle}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out TvmazeShow? cached))
        {
            return cached;
        }

        return await _singleFlight.RunAsync(
            FlightKey(cacheKey, forceRefresh),
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out TvmazeShow? flightCached))
                {
                    return flightCached;
                }

                try
                {
                    var path = $"search/shows?q={Uri.EscapeDataString(title.Trim())}";
                    using var response = await GetAsync(path, token);
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var results = await response.Content.ReadFromJsonAsync<IReadOnlyList<TvmazeSearchResult>>(
                        JsonOptions,
                        token);

                    var show = results?
                        .Select(result => result.Show)
                        .FirstOrDefault(show => NormalizeTitle(show?.Name) == normalizedTitle);

                    if (show is not null)
                    {
                        _cache.Set(cacheKey, show, TimeSpan.FromDays(7));
                    }

                    return show;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException)
                {
                    return null;
                }
                catch (JsonException)
                {
                    return null;
                }
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<TvmazeShowImage>> GetShowImagesAsync(
        int showId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled || showId <= 0)
        {
            return [];
        }

        var cacheKey = $"tvmaze:images:{showId}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<TvmazeShowImage>? cached) && cached is not null)
        {
            return cached;
        }

        return await _singleFlight.RunAsync(
            FlightKey(cacheKey, forceRefresh),
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<TvmazeShowImage>? flightCached) && flightCached is not null)
                {
                    return flightCached;
                }

                try
                {
                    using var response = await GetAsync($"shows/{showId}/images", token);
                    if (!response.IsSuccessStatusCode)
                    {
                        return [];
                    }

                    var images = await response.Content.ReadFromJsonAsync<IReadOnlyList<TvmazeShowImage>>(
                        JsonOptions,
                        token) ?? [];

                    _cache.Set(cacheKey, images, TimeSpan.FromDays(7));
                    return images;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return [];
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException)
                {
                    return [];
                }
                catch (JsonException)
                {
                    return [];
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
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled || !settings.EnableScheduleDiscovery)
        {
            return [];
        }

        var countryKey = country ?? "";
        var cacheKey = $"tvmaze:schedule:{(webSchedule ? "web" : "broadcast")}:{date:yyyyMMdd}:{countryKey}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<TvmazeScheduleEpisode>? cached) && cached is not null)
        {
            return cached;
        }

        return await _singleFlight.RunAsync(
            FlightKey(cacheKey, forceRefresh),
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<TvmazeScheduleEpisode>? flightCached) && flightCached is not null)
                {
                    return flightCached;
                }

                try
                {
                    var path = BuildSchedulePath(date, country, webSchedule);
                    using var response = await GetWithRetryAsync(path, token);
                    if (!response.IsSuccessStatusCode)
                    {
                        return [];
                    }

                    var episodes = await response.Content.ReadFromJsonAsync<IReadOnlyList<TvmazeScheduleEpisode>>(
                        JsonOptions,
                        token) ?? [];

                    _cache.Set(cacheKey, episodes, TimeSpan.FromMinutes(60));
                    return episodes;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return [];
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException)
                {
                    return [];
                }
                catch (JsonException)
                {
                    return [];
                }
            },
            cancellationToken);
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = await GetAsync(path, cancellationToken);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt == maxAttempts)
            {
                return response;
            }

            var delay = RetryAfterDelay(response) ?? TimeSpan.FromSeconds(Math.Min(4, attempt * 2));
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }

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
