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

    public TheTvdbClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<TheTvdbOptions> options,
        IIntegrationSettingsStore? settingsStore = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
    }

    public async Task<IReadOnlyList<TheTvdbArtwork>> GetSeriesArtworkAsync(
        int tvdbId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.ApiKey) || tvdbId <= 0)
        {
            return [];
        }

        var cacheKey = $"thetvdb:artwork:{tvdbId}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out IReadOnlyList<TheTvdbArtwork>? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var token = await GetTokenAsync(settings.ApiKey, cancellationToken, forceRefresh);
            if (string.IsNullOrWhiteSpace(token))
            {
                return [];
            }

            using var response = await SendSeriesArtworkRequestAsync(tvdbId, token, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var refreshedToken = await GetTokenAsync(settings.ApiKey, cancellationToken, forceRefresh: true);
                if (string.IsNullOrWhiteSpace(refreshedToken))
                {
                    return [];
                }

                using var retryResponse = await SendSeriesArtworkRequestAsync(tvdbId, refreshedToken, cancellationToken);
                var retryArtworks = await ReadSeriesArtworkAsync(retryResponse, cancellationToken);
                if (retryArtworks is not null)
                {
                    _cache.Set(cacheKey, retryArtworks, TimeSpan.FromDays(7));
                    return retryArtworks;
                }

                return [];
            }

            var artworks = await ReadSeriesArtworkAsync(response, cancellationToken);
            if (artworks is null)
            {
                return [];
            }

            _cache.Set(cacheKey, artworks, TimeSpan.FromDays(7));
            return artworks;
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

    private async Task<HttpResponseMessage> SendSeriesArtworkRequestAsync(
        int tvdbId,
        string token,
        CancellationToken cancellationToken)
    {
        return await ProviderHttpRetry.SendAsync(_httpClient, () =>
        {
            var retryRequest = new HttpRequestMessage(HttpMethod.Get, $"series/{tvdbId}/artworks");
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return retryRequest;
        }, cancellationToken);
    }

    private static async Task<IReadOnlyList<TheTvdbArtwork>?> ReadSeriesArtworkAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<TheTvdbArtworkResponse>(
            JsonOptions,
            cancellationToken);

        return result?.Data ?? [];
    }

    private async Task<string?> GetTokenAsync(string apiKey, CancellationToken cancellationToken, bool forceRefresh)
    {
        var cacheKey = $"thetvdb:token:{HashKey(apiKey)}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        using var response = await ProviderHttpRetry.SendAsync(_httpClient, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "login")
            {
                Content = JsonContent.Create(new { apikey = apiKey }, options: JsonOptions)
            };
            return request;
        }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var login = await response.Content.ReadFromJsonAsync<TheTvdbLoginResponse>(JsonOptions, cancellationToken);
        var token = login?.Data?.Token;
        if (!string.IsNullOrWhiteSpace(token))
        {
            _cache.Set(cacheKey, token, TimeSpan.FromHours(12));
        }

        return token;
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
