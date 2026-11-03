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
    private readonly PremiereTelemetry? _telemetry;

    public TraktClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<TraktOptions> options,
        IIntegrationSettingsStore? settingsStore = null,
        PremiereTelemetry? telemetry = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
        _telemetry = telemetry;
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
            ProviderOutcomeDiagnostics.Record(_telemetry, "trakt", "skipped", settings.Enabled ? "not_configured" : "disabled");
            return [];
        }

        if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<T>? cached) && cached is not null)
        {
            ProviderOutcomeDiagnostics.Record(_telemetry, "trakt", cached.Count == 0 ? "absent" : "completed", "cached_response", true);
            return cached;
        }

        var failures = new ProviderFailureCache(_cache, "trakt", _httpClient.BaseAddress, settings.ClientId);
        if (failures.Get(DateTimeOffset.UtcNow) is { } deferred) throw Failure(deferred);
        try
        {
            using var response = await SendCalendarRequestWithRetryAsync(path, settings, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var failure = ProviderFailure.FromResponse(response, DateTimeOffset.UtcNow);
                failures.Remember(failure, DateTimeOffset.UtcNow);
                throw Failure(failure);
            }

            var values = await response.Content.ReadFromJsonAsync<IReadOnlyList<T>>(
                JsonOptions,
                cancellationToken) ?? [];

            _cache.Set(cacheKey, values, TimeSpan.FromHours(6));
            ProviderOutcomeDiagnostics.Record(_telemetry, "trakt", values.Count == 0 ? "absent" : "completed", "verified_response");
            return values;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ProviderRequestException) { throw; }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or JsonException)
        {
            var failure = ProviderFailure.FromException(exception);
            failures.Remember(failure, DateTimeOffset.UtcNow);
            throw Failure(failure);
        }
    }

    private ProviderRequestException Failure(ProviderFailure failure)
    {
        _telemetry?.RecordDiagnostic("provider.outcome", _telemetry.CaptureDiagnosticContext(), new()
        {
            Provider = "trakt", Outcome = "failed", Reason = failure.Reason,
            StatusCode = failure.StatusCode, RetryAtUtc = failure.RetryAtUtc, FromCache = failure.IsLocal
        });
        return new("trakt", failure);
    }

    private async Task<HttpResponseMessage> SendCalendarRequestWithRetryAsync(
        string path,
        TraktSourceSettings settings,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("trakt-api-key", settings.ClientId.Trim());
        request.Headers.Add("trakt-api-version", string.IsNullOrWhiteSpace(_options.ApiVersion) ? "2" : _options.ApiVersion.Trim());
        request.Headers.UserAgent.Add(UserAgent);
        return await _httpClient.SendAsync(request, cancellationToken);
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
