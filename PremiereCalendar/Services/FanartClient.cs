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
    private readonly OptionalProviderResponseCache _responses;

    public FanartClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<FanartOptions> options,
        IIntegrationSettingsStore? settingsStore = null,
        PremiereTelemetry? telemetry = null,
        ProviderResponseCache? responseCache = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
        _telemetry = telemetry;
        _responses = new(cache, responseCache, timeProvider);
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
        var partition = OptionalProviderResponseCache.Partition(_httpClient.BaseAddress, settings.ApiKey, $"{kind}:{id}");
        return await _responses.GetAsync<T?>("fanart", partition, forceRefresh, TimeSpan.FromDays(7),
            token => FetchAsync<T>(path, artworkFields, settings.ApiKey, token), cancellationToken);
    }

    private async Task<ProviderRefreshResult<T?>> FetchAsync<T>(string path, string[] artworkFields, string apiKey, CancellationToken token)
    {
        var failures = new ProviderFailureCache(_cache, "fanart", _httpClient.BaseAddress, apiKey);
        if (failures.Get(DateTimeOffset.UtcNow) is { } deferred) return Failed<T>(deferred);
        var payloadFailures = new ProviderFailureCache(_cache, "fanart-payload", _httpClient.BaseAddress,
            OptionalProviderResponseCache.Partition(_httpClient.BaseAddress, apiKey, path));
        if (payloadFailures.Get(DateTimeOffset.UtcNow) is { } payloadDeferred) return Failed<T>(payloadDeferred);
        ProviderRefreshResult<T?> Invalid(string category)
        {
            var failure = new ProviderFailure("invalid_response", 200, DateTimeOffset.UtcNow.AddMinutes(1));
            payloadFailures.Remember(failure, DateTimeOffset.UtcNow);
            return Failed<T>(failure, category);
        }
        try
        {
            using var response = await ProviderHttpRetry.SendAsync(_httpClient,
                () => new HttpRequestMessage(HttpMethod.Get, $"{path}?api_key={Uri.EscapeDataString(apiKey)}"), token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new(default, true);
            if (!response.IsSuccessStatusCode)
            {
                var failure = ProviderFailure.FromResponse(response, DateTimeOffset.UtcNow);
                failures.Remember(failure, DateTimeOffset.UtcNow);
                return Failed<T>(failure);
            }
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalid("payload_non_object");
            // Fanart explicitly documents this legacy v3 missing-resource response.
            if (!root.EnumerateObject().Any() && IsLegacyV3Endpoint())
            {
                ProviderOutcomeDiagnostics.Record(_telemetry, "fanart", "absent", "legacy_empty_object");
                return new(default, true);
            }
            if (root.TryGetProperty("error", out _)) return Invalid("payload_error_object");
            if (!(HasIdentity(root) || artworkFields.Any(field => root.TryGetProperty(field, out var array) && array.ValueKind == JsonValueKind.Array)))
                return Invalid("payload_unrecognized_object");
            foreach (var field in artworkFields)
                if (root.TryGetProperty(field, out var array) && (array.ValueKind != JsonValueKind.Array
                    || array.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Object)))
                    return Invalid("payload_invalid_artwork");
            var value = root.Deserialize<T>(JsonOptions);
            return value is null ? Invalid("payload_null") : new(value, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (JsonException) { return Invalid("payload_invalid_json"); }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException)
        {
            var failure = ProviderFailure.FromException(exception);
            failures.Remember(failure, DateTimeOffset.UtcNow);
            return Failed<T>(failure);
        }
    }

    private bool IsLegacyV3Endpoint()
        => _httpClient.BaseAddress?.AbsolutePath.TrimEnd('/').EndsWith("/v3", StringComparison.OrdinalIgnoreCase) == true;

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

    private ProviderRefreshResult<T?> Failed<T>(ProviderFailure failure, string? payloadCategory = null)
    {
        _telemetry?.RecordDiagnostic("provider.outcome", _telemetry.CaptureDiagnosticContext(), new()
        {
            Provider = "fanart", Outcome = "failed", Reason = failure.Reason, Stage = payloadCategory,
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
