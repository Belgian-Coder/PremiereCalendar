using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

internal static class ProviderHttpRetry
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(250);

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = requestFactory();
                var response = await client.SendAsync(request, cancellationToken);
                if (attempt >= MaxAttempts || !IsTransient(response.StatusCode))
                {
                    return response;
                }

                var delay = RetryAfter(response) ?? TimeSpan.FromMilliseconds(50 * attempt);
                response.Dispose();
                await Task.Delay(Clamp(delay), timeProvider ?? TimeProvider.System, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), timeProvider ?? TimeProvider.System, cancellationToken);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout
        || statusCode == HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var value = response.Headers.RetryAfter;
        if (value?.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (value?.Date is { } date)
        {
            var deltaFromNow = date - DateTimeOffset.UtcNow;
            if (deltaFromNow > TimeSpan.Zero) return deltaFromNow;
        }

        return null;
    }

    private static TimeSpan Clamp(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value > MaxDelay ? MaxDelay : value;
}

public sealed class OmdbClient : IOmdbClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly OmdbOptions _options;
    private readonly IIntegrationSettingsStore? _settingsStore;
    private readonly ISingleFlightCoordinator _singleFlight;
    private readonly IOmdbCacheStore? _cacheStore;
    private readonly TimeProvider _timeProvider;

    public OmdbClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<OmdbOptions> options,
        IIntegrationSettingsStore? settingsStore = null,
        ISingleFlightCoordinator? singleFlight = null,
        IOmdbCacheStore? cacheStore = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
        _singleFlight = singleFlight ?? new SingleFlightCoordinator();
        _cacheStore = cacheStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

        var persisted = await GetPersistedAsync(imdbId, cancellationToken);
        if (!forceRefresh && IsFresh(persisted))
        {
            _cache.Set(cacheKey, persisted!.Item, CacheDuration(persisted.Item));
            return persisted.Item;
        }

        var state = _cacheStore is null
            ? new OmdbProviderCacheState(null, null, null)
            : await _cacheStore.GetProviderStateAsync(cancellationToken);
        if (state.RateLimitedUntilUtc is { } rateLimitedUntil
            && rateLimitedUntil > _timeProvider.GetUtcNow())
        {
            return persisted?.Item;
        }

        return await _singleFlight.RunAsync(
            forceRefresh ? $"refresh:{cacheKey}" : $"cache:{cacheKey}",
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out OmdbItem? flightCached))
                {
                    return flightCached;
                }

                var flightPersisted = persisted ?? await GetPersistedAsync(imdbId, token);
                if (!forceRefresh && IsFresh(flightPersisted))
                {
                    _cache.Set(cacheKey, flightPersisted!.Item, CacheDuration(flightPersisted.Item));
                    return flightPersisted.Item;
                }

                try
                {
                    var path = $"?apikey={Uri.EscapeDataString(settings.ApiKey)}&i={Uri.EscapeDataString(imdbId)}";
                    using var response = await ProviderHttpRetry.SendAsync(
                        _httpClient,
                        () => new HttpRequestMessage(HttpMethod.Get, path),
                        token,
                        _timeProvider);

                    if (!response.IsSuccessStatusCode)
                    {
                        var error = await ReadErrorMessageAsync(response, token);
                        await RecordFailureAsync(response, error, token);
                        if (flightPersisted is not null)
                        {
                            return flightPersisted.Item;
                        }

                        throw new ExternalApiException(
                            $"OMDb lookup for {imdbId} failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {error}");
                    }

                    var item = await response.Content.ReadFromJsonAsync<OmdbItem>(JsonOptions, token);
                    if (item is not null)
                    {
                        if (LooksLikeRateLimit(item.Error ?? ""))
                        {
                            await RecordOmdbPayloadFailureAsync(item.Error!, flightPersisted, token);
                            if (flightPersisted is not null)
                            {
                                return flightPersisted.Item;
                            }

                            throw new ExternalApiException($"OMDb lookup for {imdbId} failed: {item.Error}");
                        }

                        _cache.Set(cacheKey, item, CacheDuration(item));
                        if (_cacheStore is not null)
                        {
                            await _cacheStore.SetAsync(imdbId, item, _timeProvider.GetUtcNow(), token);
                        }
                    }

                    return item;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return flightPersisted?.Item;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException)
                {
                    if (flightPersisted is not null)
                    {
                        return flightPersisted.Item;
                    }

                    await MarkFailureAsync("OMDb HTTP request failed.", token);
                    return null;
                }
                catch (JsonException)
                {
                    if (flightPersisted is not null)
                    {
                        return flightPersisted.Item;
                    }

                    await MarkFailureAsync("OMDb returned invalid JSON.", token);
                    return null;
                }
            },
            cancellationToken);
    }

    private async Task<OmdbCacheEntry?> GetPersistedAsync(string imdbId, CancellationToken cancellationToken)
    {
        return _cacheStore is null
            ? null
            : await _cacheStore.GetAsync(imdbId, cancellationToken);
    }

    private bool IsFresh(OmdbCacheEntry? entry)
    {
        return entry is not null
            && _timeProvider.GetUtcNow() - entry.CachedAtUtc < CacheDuration(entry.Item);
    }

    private TimeSpan CacheDuration()
    {
        return TimeSpan.FromDays(Math.Max(1, _options.CacheDays));
    }

    private TimeSpan CacheDuration(OmdbItem item)
    {
        return HasUsefulPayload(item)
            ? CacheDuration()
            : TimeSpan.FromHours(Math.Max(1, _options.EmptyResponseCacheHours));
    }

    private static bool HasUsefulPayload(OmdbItem item)
    {
        if (string.Equals(item.Response, "False", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return HasUsefulValue(item.ImdbRating)
            || HasUsefulValue(item.ImdbVotes)
            || HasUsefulValue(item.Metascore)
            || HasUsefulValue(item.Plot)
            || HasUsefulValue(item.Poster)
            || item.Ratings.Any(rating => HasUsefulValue(rating.Value));
    }

    private static bool HasUsefulValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RecordFailureAsync(HttpResponseMessage response, string error, CancellationToken cancellationToken)
    {
        if (_cacheStore is null)
        {
            return;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
            || response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            || LooksLikeRateLimit(error))
        {
            await _cacheStore.MarkRateLimitedAsync(
                RetryAfterUtc(response) ?? _timeProvider.GetUtcNow().AddHours(Math.Max(1, _options.RateLimitBackoffHours)),
                error,
                cancellationToken);
            return;
        }

        await _cacheStore.MarkFailureAsync(error, cancellationToken);
    }

    private async Task RecordOmdbPayloadFailureAsync(
        string error,
        OmdbCacheEntry? persisted,
        CancellationToken cancellationToken)
    {
        if (_cacheStore is null)
        {
            return;
        }

        if (LooksLikeRateLimit(error))
        {
            await _cacheStore.MarkRateLimitedAsync(
                _timeProvider.GetUtcNow().AddHours(Math.Max(1, _options.RateLimitBackoffHours)),
                error,
                cancellationToken);
            return;
        }

        if (persisted is null)
        {
            await _cacheStore.MarkFailureAsync(error, cancellationToken);
        }
    }

    private async Task MarkFailureAsync(string error, CancellationToken cancellationToken)
    {
        if (_cacheStore is not null)
        {
            await _cacheStore.MarkFailureAsync(error, cancellationToken);
        }
    }

    private DateTimeOffset? RetryAfterUtc(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Date is { } date)
        {
            return date;
        }

        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return _timeProvider.GetUtcNow().Add(delta);
        }

        return null;
    }

    private static bool LooksLikeRateLimit(string error)
    {
        return error.Contains("limit", StringComparison.OrdinalIgnoreCase)
            || error.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || error.Contains("rate", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var item = await response.Content.ReadFromJsonAsync<OmdbItem>(JsonOptions, cancellationToken);
            if (!string.IsNullOrWhiteSpace(item?.Error))
            {
                return item.Error.Trim();
            }
        }
        catch (JsonException)
        {
        }

        return "OMDb returned an unsuccessful response.";
    }

    private async ValueTask<OmdbSourceSettings> GetEffectiveSettingsAsync(CancellationToken cancellationToken)
    {
        return _settingsStore is null
            ? new OmdbSourceSettings { Enabled = _options.Enabled, ApiKey = _options.ApiKey ?? "" }
            : (await _settingsStore.GetAsync(cancellationToken)).Sources.Omdb;
    }
}
