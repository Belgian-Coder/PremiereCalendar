using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class OmdbClient : IOmdbClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly OmdbOptions _options;
    private readonly IIntegrationSettingsStore? _settingsStore;
    private readonly ISingleFlightCoordinator _singleFlight;

    public OmdbClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<OmdbOptions> options,
        IIntegrationSettingsStore? settingsStore = null,
        ISingleFlightCoordinator? singleFlight = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
        _singleFlight = singleFlight ?? new SingleFlightCoordinator();
    }

    public async Task<OmdbItem?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        var cacheKey = $"omdb:imdb:{imdbId}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out OmdbItem? cached))
        {
            return cached;
        }

        return await _singleFlight.RunAsync(
            forceRefresh ? $"refresh:{cacheKey}" : $"cache:{cacheKey}",
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out OmdbItem? flightCached))
                {
                    return flightCached;
                }

                try
                {
                    var path = $"?apikey={Uri.EscapeDataString(settings.ApiKey)}&i={Uri.EscapeDataString(imdbId)}";
                    using var response = await _httpClient.GetAsync(path, token);

                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var item = await response.Content.ReadFromJsonAsync<OmdbItem>(JsonOptions, token);
                    if (item is not null)
                    {
                        _cache.Set(cacheKey, item, TimeSpan.FromDays(7));
                    }

                    return item;
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

    private async ValueTask<OmdbSourceSettings> GetEffectiveSettingsAsync(CancellationToken cancellationToken)
    {
        return _settingsStore is null
            ? new OmdbSourceSettings { Enabled = _options.Enabled, ApiKey = _options.ApiKey ?? "" }
            : (await _settingsStore.GetAsync(cancellationToken)).Sources.Omdb;
    }
}
