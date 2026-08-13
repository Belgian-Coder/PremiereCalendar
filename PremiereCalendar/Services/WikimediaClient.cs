using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class WikimediaClient : IWikimediaClient
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly WikimediaOptions _options;
    private readonly IIntegrationSettingsStore? _settingsStore;

    public WikimediaClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<WikimediaOptions> options,
        IIntegrationSettingsStore? settingsStore = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
    }

    public async Task<string?> GetReusableImageUrlAsync(
        string wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var enabled = _settingsStore is null
            ? _options.Enabled
            : (await _settingsStore.GetAsync(cancellationToken)).Sources.Wikimedia.Enabled;
        if (!enabled || string.IsNullOrWhiteSpace(wikidataId))
        {
            return null;
        }

        var cacheKey = $"wikimedia:image:{wikidataId}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out string? cached))
        {
            return string.IsNullOrWhiteSpace(cached) ? null : cached;
        }

        try
        {
            var fileName = await GetP18FileNameAsync(wikidataId.Trim(), cancellationToken);
            if (!fileName.IsSuccess)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(fileName.Value))
            {
                _cache.Set(cacheKey, "", TimeSpan.FromDays(7));
                return null;
            }

            var imageUrl = await GetReusableCommonsImageUrlAsync(fileName.Value, cancellationToken);
            if (!imageUrl.IsSuccess)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(imageUrl.Value))
            {
                _cache.Set(cacheKey, imageUrl.Value, TimeSpan.FromDays(30));
            }
            else
            {
                _cache.Set(cacheKey, "", TimeSpan.FromDays(7));
            }

            return imageUrl.Value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
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
    }

    public async Task<string?> GetRottenTomatoesIdAsync(
        string wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var enabled = _settingsStore is null
            ? _options.Enabled
            : (await _settingsStore.GetAsync(cancellationToken)).Sources.Wikimedia.Enabled;
        if (!enabled || string.IsNullOrWhiteSpace(wikidataId))
        {
            return null;
        }

        var cacheKey = $"wikimedia:rtid:{wikidataId}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out string? cached))
        {
            return string.IsNullOrWhiteSpace(cached) ? null : cached;
        }

        try
        {
            var rottenTomatoesId = await GetWikidataStringClaimAsync(wikidataId.Trim(), "P1258", cancellationToken);
            if (!rottenTomatoesId.IsSuccess)
            {
                return null;
            }

            _cache.Set(
                cacheKey,
                string.IsNullOrWhiteSpace(rottenTomatoesId.Value) ? "" : rottenTomatoesId.Value,
                string.IsNullOrWhiteSpace(rottenTomatoesId.Value) ? TimeSpan.FromDays(7) : TimeSpan.FromDays(30));
            return rottenTomatoesId.Value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
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
    }

    private async Task<WikimediaFetchResult<string>> GetP18FileNameAsync(string wikidataId, CancellationToken cancellationToken)
    {
        return await GetWikidataStringClaimAsync(wikidataId, "P18", cancellationToken);
    }

    private async Task<WikimediaFetchResult<string>> GetWikidataStringClaimAsync(
        string wikidataId,
        string propertyId,
        CancellationToken cancellationToken)
    {
        using var response = await GetWithRetryAsync(
            $"wiki/Special:EntityData/{Uri.EscapeDataString(wikidataId)}.json",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new WikimediaFetchResult<string>(null, IsSuccess: false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("entities", out var entities)
            || !entities.TryGetProperty(wikidataId, out var entity)
            || !entity.TryGetProperty("claims", out var claims)
            || !claims.TryGetProperty(propertyId, out var propertyClaims)
            || propertyClaims.ValueKind != JsonValueKind.Array)
        {
            return new WikimediaFetchResult<string>(null, IsSuccess: true);
        }

        foreach (var claim in propertyClaims.EnumerateArray())
        {
            if (claim.TryGetProperty("mainsnak", out var mainSnak)
                && mainSnak.TryGetProperty("datavalue", out var dataValue)
                && dataValue.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return new WikimediaFetchResult<string>(value.GetString(), IsSuccess: true);
            }
        }

        return new WikimediaFetchResult<string>(null, IsSuccess: true);
    }

    private async Task<WikimediaFetchResult<string>> GetReusableCommonsImageUrlAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.CommonsApiUrl}?action=query&format=json&prop=imageinfo&iiprop=url%7Cextmetadata&titles=File:{Uri.EscapeDataString(fileName)}";
        using var response = await GetWithRetryAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new WikimediaFetchResult<string>(null, IsSuccess: false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("query", out var query)
            || !query.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Object)
        {
            return new WikimediaFetchResult<string>(null, IsSuccess: true);
        }

        foreach (var page in pages.EnumerateObject())
        {
            if (!page.Value.TryGetProperty("imageinfo", out var imageInfos)
                || imageInfos.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var imageInfo in imageInfos.EnumerateArray())
            {
                if (!HasReusableMetadata(imageInfo))
                {
                    continue;
                }

                if (imageInfo.TryGetProperty("url", out var imageUrl)
                    && imageUrl.ValueKind == JsonValueKind.String)
                {
                    return new WikimediaFetchResult<string>(imageUrl.GetString(), IsSuccess: true);
                }
            }
        }

        return new WikimediaFetchResult<string>(null, IsSuccess: true);
    }

    private static bool HasReusableMetadata(JsonElement imageInfo)
    {
        if (!imageInfo.TryGetProperty("extmetadata", out var metadata))
        {
            return false;
        }

        var license = MetadataValue(metadata, "LicenseShortName");
        var usageTerms = MetadataValue(metadata, "UsageTerms");
        var restrictions = MetadataValue(metadata, "Restrictions");

        return (!string.IsNullOrWhiteSpace(license) || !string.IsNullOrWhiteSpace(usageTerms))
            && (string.IsNullOrWhiteSpace(restrictions)
                || !restrictions.Contains("trademark", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        return await _httpClient.GetAsync(url, cancellationToken);
    }

    private static string? MetadataValue(JsonElement metadata, string propertyName)
    {
        return metadata.TryGetProperty(propertyName, out var property)
            && property.TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private readonly record struct WikimediaFetchResult<T>(T? Value, bool IsSuccess);
}
