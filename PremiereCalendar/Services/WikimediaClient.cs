using System.Diagnostics;
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
    private readonly PremiereTelemetry? _telemetry;
    private readonly WikimediaClaimBatcher _claims;
    private readonly object _fenceGate = new();

    public WikimediaClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<WikimediaOptions> options,
        IIntegrationSettingsStore? settingsStore = null,
        PremiereTelemetry? telemetry = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
        _telemetry = telemetry;
        _claims = new WikimediaClaimBatcher(FetchEntityClaimsAsync, telemetry);
    }

    public async Task<string?> GetReusableImageUrlAsync(
        string wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var started = Stopwatch.GetTimestamp();
        var context = _telemetry?.CaptureDiagnosticContext();
        var outcome = "failed";
        var cacheHit = false;
        try
        {
            var enabled = _settingsStore is null
                ? _options.Enabled
                : (await _settingsStore.GetAsync(cancellationToken)).Sources.Wikimedia.Enabled;
            if (!enabled || string.IsNullOrWhiteSpace(wikidataId))
            {
                outcome = "disabled_or_invalid";
                return null;
            }

            wikidataId = wikidataId.Trim();
            var fence = GetCacheFence(wikidataId);
            var generation = fence.Capture(forceRefresh);
            var cacheKey = $"wikimedia:image:{wikidataId}";
            if (!forceRefresh && _cache.TryGetValue(cacheKey, out string? cached))
            {
                cacheHit = true;
                outcome = "cache_hit";
                return string.IsNullOrWhiteSpace(cached) ? null : cached;
            }

            var entity = await _claims.GetAsync(wikidataId, generation, cancellationToken);
            context = (context ?? new()) with { RequestId = entity.RequestId };
            var fileName = entity.Image;
            if (!fileName.IsSuccess)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(fileName.Value))
            {
                CacheResult(fence, generation, cacheKey, null);
                outcome = "absent";
                return null;
            }

            var imageUrl = await GetReusableCommonsImageUrlAsync(fileName.Value, cancellationToken, context);
            if (!imageUrl.IsSuccess)
            {
                return null;
            }

            CacheResult(fence, generation, cacheKey, imageUrl.Value);
            outcome = string.IsNullOrWhiteSpace(imageUrl.Value) ? "absent" : "found";
            return imageUrl.Value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
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
        finally
        {
            RecordCall("image", context, outcome, cacheHit, started);
        }
    }

    public async Task<string?> GetRottenTomatoesIdAsync(
        string wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
        => (await GetRottenTomatoesIdResultAsync(wikidataId, cancellationToken, forceRefresh)).Value;

    public async Task<WikimediaIdentifierResult> GetRottenTomatoesIdResultAsync(
        string wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var started = Stopwatch.GetTimestamp();
        var context = _telemetry?.CaptureDiagnosticContext();
        var outcome = "failed";
        var cacheHit = false;
        try
        {
            var enabled = _settingsStore is null
                ? _options.Enabled
                : (await _settingsStore.GetAsync(cancellationToken)).Sources.Wikimedia.Enabled;
            if (!enabled || string.IsNullOrWhiteSpace(wikidataId))
            {
                outcome = "disabled_or_invalid";
                return new(null, IsSuccess: false);
            }

            wikidataId = wikidataId.Trim();
            var fence = GetCacheFence(wikidataId);
            var generation = fence.Capture(forceRefresh);
            var cacheKey = $"wikimedia:rtid:{wikidataId}";
            if (!forceRefresh && _cache.TryGetValue(cacheKey, out string? cached))
            {
                cacheHit = true;
                outcome = "cache_hit";
                return new(string.IsNullOrWhiteSpace(cached) ? null : cached, IsSuccess: true);
            }

            var entity = await _claims.GetAsync(wikidataId, generation, cancellationToken);
            context = (context ?? new()) with { RequestId = entity.RequestId };
            var rottenTomatoesId = entity.Identifier;
            if (!rottenTomatoesId.IsSuccess)
            {
                return new(null, IsSuccess: false);
            }

            CacheResult(fence, generation, cacheKey, rottenTomatoesId.Value);
            outcome = string.IsNullOrWhiteSpace(rottenTomatoesId.Value) ? "absent" : "found";
            return new(rottenTomatoesId.Value, IsSuccess: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, IsSuccess: false);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch (HttpRequestException)
        {
            return new(null, IsSuccess: false);
        }
        catch (JsonException)
        {
            return new(null, IsSuccess: false);
        }
        finally
        {
            RecordCall("identifier", context, outcome, cacheHit, started);
        }
    }

    private void RecordCall(string stage, PerformanceDiagnosticContext? context, string outcome, bool cacheHit, long started)
    {
        _telemetry?.RecordDiagnostic("provider.wikimedia.lookup.completed", context, new()
        {
            Provider = "wikimedia", Stage = stage, Outcome = outcome, FromCache = cacheHit,
            DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
        });
    }

    private async Task<IReadOnlyDictionary<string, WikimediaEntityClaims>> FetchEntityClaimsAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var url = ids.Count == 1
            ? $"wiki/Special:EntityData/{Uri.EscapeDataString(ids[0])}.json"
            : $"w/api.php?action=wbgetentities&format=json&props=claims&ids={Uri.EscapeDataString(string.Join('|', ids))}";
        using var response = await GetEntityResponseAsync(url, ids.Count, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new Dictionary<string, WikimediaEntityClaims>();
        }

        var started = Stopwatch.GetTimestamp();
        var outcome = "malformed";
        var results = new Dictionary<string, WikimediaEntityClaims>(StringComparer.Ordinal);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.TryGetProperty("error", out _)
                || !document.RootElement.TryGetProperty("entities", out var entities)
                || entities.ValueKind != JsonValueKind.Object)
            {
                outcome = "unverified";
                return results;
            }
            foreach (var id in ids)
            {
                // An omitted/missing/malformed entity is not evidence that either claim is absent.
                if (!entities.TryGetProperty(id, out var entity) || entity.ValueKind != JsonValueKind.Object
                    || entity.TryGetProperty("missing", out _) || !entity.TryGetProperty("claims", out var claims)
                    || claims.ValueKind != JsonValueKind.Object) continue;
                results[id] = new(ReadStringClaim(claims, "P18"), ReadStringClaim(claims, "P1258"));
            }
            outcome = results.Count == ids.Count ? "parsed" : "partial";
            return results;
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            throw;
        }
        finally
        {
            _telemetry?.RecordDiagnostic("provider.wikimedia.parse.completed", details: new()
            {
                Provider = "wikimedia", Stage = "entity_parse", Outcome = outcome,
                Count = ids.Count, Total = results.Count,
                DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }
    }

    private async Task<HttpResponseMessage> GetEntityResponseAsync(string url, int count, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        int? status = null;
        try
        {
            var response = await GetWithRetryAsync(url, cancellationToken);
            status = (int)response.StatusCode;
            outcome = response.IsSuccessStatusCode ? "success" : "http_error";
            return response;
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            throw;
        }
        finally
        {
            _telemetry?.RecordDiagnostic("provider.wikimedia.fetch.completed", details: new()
            {
                Provider = "wikimedia", Stage = "entity_fetch", Outcome = outcome,
                Count = count, StatusCode = status,
                DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }
    }

    private static WikimediaClaimValue ReadStringClaim(JsonElement claims, string propertyId)
    {
        if (!claims.TryGetProperty(propertyId, out var propertyClaims)) return new(null, true);
        if (propertyClaims.ValueKind != JsonValueKind.Array) return new(null, false);
        var malformed = false;
        foreach (var claim in propertyClaims.EnumerateArray())
        {
            if (claim.ValueKind == JsonValueKind.Object && claim.TryGetProperty("mainsnak", out var mainSnak)
                && mainSnak.ValueKind == JsonValueKind.Object
                && mainSnak.TryGetProperty("datavalue", out var dataValue)
                && dataValue.ValueKind == JsonValueKind.Object
                && dataValue.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return new(value.GetString(), true);
            }
            if (claim.ValueKind != JsonValueKind.Object || !claim.TryGetProperty("mainsnak", out var snak)
                || snak.ValueKind != JsonValueKind.Object || !snak.TryGetProperty("snaktype", out var snakType)
                || snakType.ValueKind != JsonValueKind.String || snakType.GetString() is not ("novalue" or "somevalue")) malformed = true;
        }
        return new(null, !malformed);
    }

    private CacheFence GetCacheFence(string id)
    {
        lock (_fenceGate)
            return _cache.GetOrCreate($"wikimedia:fence:{id}", entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromDays(31);
                return new CacheFence();
            })!;
    }

    private void CacheResult(CacheFence fence, long generation, string key, string? value)
    {
        lock (fence)
            if (fence.Generation == generation)
                _cache.Set(key, string.IsNullOrWhiteSpace(value) ? "" : value,
                    string.IsNullOrWhiteSpace(value) ? TimeSpan.FromDays(7) : TimeSpan.FromDays(30));
    }

    private sealed class CacheFence
    {
        internal long Generation { get; private set; }
        internal long Capture(bool force)
        {
            lock (this)
            {
                if (force) Generation++;
                return Generation;
            }
        }
    }

    private async Task<WikimediaFetchResult<string>> GetReusableCommonsImageUrlAsync(
        string fileName,
        CancellationToken cancellationToken,
        PerformanceDiagnosticContext? context)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        try
        {
            using var scope = _telemetry?.UseDiagnosticContext(context);
            var result = await FetchReusableCommonsImageUrlAsync(fileName, cancellationToken);
            outcome = !result.IsSuccess ? "failed" : string.IsNullOrWhiteSpace(result.Value) ? "absent" : "found";
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            throw;
        }
        finally
        {
            _telemetry?.RecordDiagnostic("provider.wikimedia.commons.completed", context, new()
            {
                Provider = "wikimedia", Stage = "commons_license", Outcome = outcome,
                DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }
    }

    private async Task<WikimediaFetchResult<string>> FetchReusableCommonsImageUrlAsync(
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

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.TryGetProperty("error", out _)
            || !document.RootElement.TryGetProperty("query", out var query)
            || query.ValueKind != JsonValueKind.Object
            || !query.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Object)
        {
            return new WikimediaFetchResult<string>(null, IsSuccess: false);
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
