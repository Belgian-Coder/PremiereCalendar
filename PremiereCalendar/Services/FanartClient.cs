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

    public FanartClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<FanartOptions> options,
        IIntegrationSettingsStore? settingsStore = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
    }

    public async Task<FanartMovieArtwork?> GetMovieArtworkAsync(
        int tmdbId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        return tmdbId <= 0 || !IsConfigured(settings)
            ? null
            : await GetOrCreateAsync<FanartMovieArtwork>(
                $"fanart:movie:{tmdbId}",
                $"movies/{tmdbId}",
                settings.ApiKey,
                "Fanart.tv movie artwork",
                cancellationToken,
                forceRefresh);
    }

    public async Task<FanartTvArtwork?> GetTvArtworkAsync(
        int tvdbId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        return tvdbId <= 0 || !IsConfigured(settings)
            ? null
            : await GetOrCreateAsync<FanartTvArtwork>(
                $"fanart:tv:{tvdbId}",
                $"tv/{tvdbId}",
                settings.ApiKey,
                "Fanart.tv TV artwork",
                cancellationToken,
                forceRefresh);
    }

    private async Task<T?> GetOrCreateAsync<T>(
        string cacheKey,
        string path,
        string apiKey,
        string operation,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out T? cached))
        {
            return cached;
        }

        try
        {
            var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            using var response = await _httpClient.GetAsync(
                $"{path}{separator}api_key={Uri.EscapeDataString(apiKey)}",
                cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound
                || response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                return default;
            }

            var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
            if (value is not null)
            {
                _cache.Set(cacheKey, value, TimeSpan.FromDays(7));
            }

            return value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private async ValueTask<FanartSourceSettings> GetEffectiveSettingsAsync(CancellationToken cancellationToken)
    {
        return _settingsStore is null
            ? new FanartSourceSettings { Enabled = _options.Enabled, ApiKey = _options.ApiKey ?? "" }
            : (await _settingsStore.GetAsync(cancellationToken)).Sources.Fanart;
    }

    private static bool IsConfigured(FanartSourceSettings settings)
    {
        return settings.Enabled && !string.IsNullOrWhiteSpace(settings.ApiKey);
    }
}
