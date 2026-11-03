using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class TheTvdbClient : ITheTvdbClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly TheTvdbOptions _options;
    private readonly IIntegrationSettingsStore? _settingsStore;
    private readonly PremiereTelemetry? _telemetry;

    public TheTvdbClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<TheTvdbOptions> options,
        IIntegrationSettingsStore? settingsStore = null,
        PremiereTelemetry? telemetry = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
        _telemetry = telemetry;
    }

    public async Task<IReadOnlyList<TheTvdbArtwork>> GetSeriesArtworkAsync(int tvdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        => (await GetSeriesArtworkWithStatusAsync(tvdbId, cancellationToken, forceRefresh)).Value;

    public async Task<ProviderRefreshResult<IReadOnlyList<TheTvdbArtwork>>> GetSeriesArtworkWithStatusAsync(int tvdbId, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled || tvdbId <= 0) return new([], true, new("disabled"));
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            ProviderOutcomeDiagnostics.Record(_telemetry, "thetvdb", "skipped", "not_configured");
            return new([], true, new("not_configured"));
        }
        var cacheKey = $"thetvdb:artwork:{tvdbId}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<TheTvdbArtwork>? cached) && cached is not null)
            return new(cached, true);
        var failures = new ProviderFailureCache(_cache, "thetvdb", _httpClient.BaseAddress, settings.ApiKey);
        if (failures.Get(DateTimeOffset.UtcNow) is { } deferred) return Failed(deferred);
        try
        {
            // Refreshing artwork does not invalidate an otherwise valid login token.
            var token = await GetTokenAsync(settings.ApiKey, cancellationToken, forceRefresh: false);
            using var response = await SendSeriesArtworkRequestAsync(tvdbId, token, cancellationToken, allowAuthenticationRefresh: true);
            ProviderRefreshResult<IReadOnlyList<TheTvdbArtwork>> result;
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var refreshed = await GetTokenAsync(settings.ApiKey, cancellationToken, forceRefresh: true);
                using var retry = await SendSeriesArtworkRequestAsync(tvdbId, refreshed, cancellationToken, allowAuthenticationRefresh: false);
                result = await ReadSeriesArtworkAsync(retry, cancellationToken);
            }
            else result = await ReadSeriesArtworkAsync(response, cancellationToken);
            if (result.Successful) _cache.Set(cacheKey, result.Value, TimeSpan.FromDays(7));
            else if (result.Failure is { } failure) failures.Remember(failure, DateTimeOffset.UtcNow);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or JsonException)
        {
            var failure = ProviderFailure.FromException(exception);
            failures.Remember(failure, DateTimeOffset.UtcNow);
            return Failed(failure);
        }
    }

    private Task<HttpResponseMessage> SendSeriesArtworkRequestAsync(int tvdbId, string token, CancellationToken cancellationToken, bool allowAuthenticationRefresh)
        => ProviderHttpRetry.SendAsync(_httpClient, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"series/{tvdbId}/artworks");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Options.Set(ProviderRequestOptions.AllowAuthenticationRefresh, allowAuthenticationRefresh);
            return request;
        }, cancellationToken);

    private async Task<ProviderRefreshResult<IReadOnlyList<TheTvdbArtwork>>> ReadSeriesArtworkAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new([], true);
        if (!response.IsSuccessStatusCode) return Failed(ProviderFailure.FromResponse(response, DateTimeOffset.UtcNow));
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data))
            return Failed(new("invalid_response", 200));
        // The v4 API returns a SeriesExtendedRecord containing artworks. Keep
        // direct-array compatibility with older adapters and saved fixtures.
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (!data.TryGetProperty("artworks", out data)) return Failed(new("invalid_response", 200));
        }
        if (data.ValueKind != JsonValueKind.Array) return Failed(new("invalid_response", 200));
        return new(data.Deserialize<IReadOnlyList<TheTvdbArtwork>>(JsonOptions) ?? [], true);
    }

    private async Task<string> GetTokenAsync(string apiKey, CancellationToken cancellationToken, bool forceRefresh)
    {
        var cacheKey = $"thetvdb:token:{HashKey(apiKey)}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached)) return cached;
        using var response = await ProviderHttpRetry.SendAsync(_httpClient, () => new HttpRequestMessage(HttpMethod.Post, "login")
        { Content = JsonContent.Create(new { apikey = apiKey }, options: JsonOptions) }, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ProviderRequestException("thetvdb", ProviderFailure.FromResponse(response, DateTimeOffset.UtcNow));
        var login = await response.Content.ReadFromJsonAsync<TheTvdbLoginResponse>(JsonOptions, cancellationToken);
        var token = login?.Data?.Token;
        if (string.IsNullOrWhiteSpace(token)) throw new ProviderRequestException("thetvdb", new("invalid_response", 200));
        _cache.Set(cacheKey, token, TimeSpan.FromHours(12));
        return token;
    }

    private ProviderRefreshResult<IReadOnlyList<TheTvdbArtwork>> Failed(ProviderFailure failure)
    {
        _telemetry?.RecordDiagnostic("provider.outcome", _telemetry.CaptureDiagnosticContext(), new()
        {
            Provider = "thetvdb", Outcome = "failed", Reason = failure.Reason,
            StatusCode = failure.StatusCode, RetryAtUtc = failure.RetryAtUtc, FromCache = failure.IsLocal
        });
        return new([], false, failure);
    }

    private async ValueTask<TheTvdbSourceSettings> GetEffectiveSettingsAsync(CancellationToken cancellationToken)
    {
        return _settingsStore is null
            ? new TheTvdbSourceSettings { Enabled = _options.Enabled, ApiKey = _options.ApiKey ?? "" }
            : (await _settingsStore.GetAsync(cancellationToken)).Sources.TheTvdb;
    }

    private static string HashKey(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())))[..12];
    }
}
