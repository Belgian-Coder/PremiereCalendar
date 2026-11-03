using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class FanartClient : IFanartClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly FanartOptions _options;
    private readonly IIntegrationSettingsStore? _settingsStore;
    private readonly PremiereTelemetry? _telemetry;

    public FanartClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<FanartOptions> options,
        IIntegrationSettingsStore? settingsStore = null,
        PremiereTelemetry? telemetry = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
        _telemetry = telemetry;
    }

    public async Task<FanartMovieArtwork?> GetMovieArtworkAsync(int tmdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        => (await GetMovieArtworkWithStatusAsync(tmdbId, cancellationToken, forceRefresh)).Value;

    public async Task<FanartTvArtwork?> GetTvArtworkAsync(int tvdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        => (await GetTvArtworkWithStatusAsync(tvdbId, cancellationToken, forceRefresh)).Value;

    public Task<ProviderRefreshResult<FanartMovieArtwork?>> GetMovieArtworkWithStatusAsync(int tmdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        => GetOrCreateAsync<FanartMovieArtwork>(tmdbId, "movie", $"movies/{tmdbId}", ["movieposter", "moviebackground"], cancellationToken, forceRefresh);

    public Task<ProviderRefreshResult<FanartTvArtwork?>> GetTvArtworkWithStatusAsync(int tvdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        => GetOrCreateAsync<FanartTvArtwork>(tvdbId, "tv", $"tv/{tvdbId}", ["tvposter", "showbackground", "tvthumb"], cancellationToken, forceRefresh);

    private async Task<ProviderRefreshResult<T?>> GetOrCreateAsync<T>(int id, string kind, string path, string[] artworkFields, CancellationToken cancellationToken, bool forceRefresh)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled || id <= 0) return new(default, true, new("disabled"));
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            ProviderOutcomeDiagnostics.Record(_telemetry, "fanart", "skipped", "not_configured");
            return new(default, true, new("not_configured"));
        }
        var cacheKey = $"fanart:{kind}:{id}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out T? cached)) return new(cached, true);
        var failures = new ProviderFailureCache(_cache, "fanart", _httpClient.BaseAddress, settings.ApiKey);
        if (failures.Get(DateTimeOffset.UtcNow) is { } deferred) return Failed<T>(deferred);
        try
        {
            var requestPath = $"{path}?api_key={Uri.EscapeDataString(settings.ApiKey)}";
            using var response = await ProviderHttpRetry.SendAsync(_httpClient,
                () => new HttpRequestMessage(HttpMethod.Get, requestPath), cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _cache.Set<T?>(cacheKey, default, TimeSpan.FromDays(7));
                return new(default, true);
            }
            if (!response.IsSuccessStatusCode)
            {
                var failure = ProviderFailure.FromResponse(response, DateTimeOffset.UtcNow);
                failures.Remember(failure, DateTimeOffset.UtcNow);
                return Failed<T>(failure);
            }
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)
                || !(HasIdentity(root) || artworkFields.Any(field => root.TryGetProperty(field, out var array) && array.ValueKind == JsonValueKind.Array)))
                return Failed<T>(new("invalid_response", 200));
            var value = root.Deserialize<T>(JsonOptions);
            if (value is null) return Failed<T>(new("invalid_response", 200));
            _cache.Set(cacheKey, value, TimeSpan.FromDays(7));
            return new(value, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or JsonException)
        {
            var failure = ProviderFailure.FromException(exception);
            failures.Remember(failure, DateTimeOffset.UtcNow);
            return Failed<T>(failure);
        }
    }

    private static bool HasIdentity(JsonElement root)
    {
        if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString())) return true;
        foreach (var field in new[] { "tmdb_id", "thetvdb_id" })
        {
            if (!root.TryGetProperty(field, out var id)) continue;
            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) && number > 0) return true;
            if (id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), out number) && number > 0) return true;
        }
        return false;
    }

    private ProviderRefreshResult<T?> Failed<T>(ProviderFailure failure)
    {
        _telemetry?.RecordDiagnostic("provider.outcome", _telemetry.CaptureDiagnosticContext(), new()
        {
            Provider = "fanart", Outcome = "failed", Reason = failure.Reason,
            StatusCode = failure.StatusCode, RetryAtUtc = failure.RetryAtUtc, FromCache = failure.IsLocal
        });
        return new(default, false, failure);
    }

    private async ValueTask<FanartSourceSettings> GetEffectiveSettingsAsync(CancellationToken cancellationToken)
    {
        return _settingsStore is null
            ? new FanartSourceSettings { Enabled = _options.Enabled, ApiKey = _options.ApiKey ?? "" }
            : (await _settingsStore.GetAsync(cancellationToken)).Sources.Fanart;
    }

}
