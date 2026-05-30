using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class TraktClient : ITraktClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ProductInfoHeaderValue UserAgent = new("PremiereCalendar", "1.0");

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly TraktOptions _options;
    private readonly IIntegrationSettingsStore? _settingsStore;

    public TraktClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<TraktOptions> options,
        IIntegrationSettingsStore? settingsStore = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
    }

    public Task<IReadOnlyList<TraktMovieCalendarItem>> GetMovieCalendarAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        return GetCalendarAsync<TraktMovieCalendarItem>(
            $"trakt:movies:{start:yyyyMMdd}:{end:yyyyMMdd}",
            $"calendars/all/movies/{FormatDate(start)}/{DaysInclusive(start, end)}",
            cancellationToken,
            forceRefresh);
    }

    public Task<IReadOnlyList<TraktShowCalendarItem>> GetNewShowCalendarAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        return GetCalendarAsync<TraktShowCalendarItem>(
            $"trakt:new-shows:{start:yyyyMMdd}:{end:yyyyMMdd}",
            $"calendars/all/shows/new/{FormatDate(start)}/{DaysInclusive(start, end)}",
            cancellationToken,
            forceRefresh);
    }

    private async Task<IReadOnlyList<T>> GetCalendarAsync<T>(
        string cacheKey,
        string path,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!IsConfigured(settings))
        {
            return [];
        }

        if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<T>? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            using var response = await SendCalendarRequestWithRetryAsync(path, settings, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var values = await response.Content.ReadFromJsonAsync<IReadOnlyList<T>>(
                JsonOptions,
                cancellationToken) ?? [];

            _cache.Set(cacheKey, values, TimeSpan.FromHours(6));
            return values;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
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
    }

    private async Task<HttpResponseMessage> SendCalendarRequestWithRetryAsync(
        string path,
        TraktSourceSettings settings,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("trakt-api-key", settings.ClientId.Trim());
            request.Headers.Add("trakt-api-version", string.IsNullOrWhiteSpace(_options.ApiVersion) ? "2" : _options.ApiVersion.Trim());
            request.Headers.UserAgent.Add(UserAgent);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt == maxAttempts)
            {
                return response;
            }

            var delay = RetryAfterDelay(response) ?? TimeSpan.FromSeconds(Math.Min(4, attempt * 2));
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }

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

    private async ValueTask<TraktSourceSettings> GetEffectiveSettingsAsync(CancellationToken cancellationToken)
    {
        return _settingsStore is null
            ? new TraktSourceSettings { Enabled = _options.Enabled, ClientId = _options.ClientId ?? "" }
            : (await _settingsStore.GetAsync(cancellationToken)).Sources.Trakt;
    }

    private static bool IsConfigured(TraktSourceSettings settings)
    {
        return settings.Enabled && !string.IsNullOrWhiteSpace(settings.ClientId);
    }

    private static int DaysInclusive(DateOnly start, DateOnly end)
    {
        return Math.Max(1, end.DayNumber - start.DayNumber + 1);
    }

    private static string FormatDate(DateOnly date)
    {
        return date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }
}
