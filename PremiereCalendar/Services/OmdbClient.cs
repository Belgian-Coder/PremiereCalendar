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
    public static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        var request = requestFactory();
        return SendAndDisposeRequestAsync(client, request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendAndDisposeRequestAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using (request)
        {
            return await client.SendAsync(request, cancellationToken);
        }
    }
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
    private readonly PremiereTelemetry? _telemetry;
    private readonly ProviderResponseCache? _responseCache;

    public OmdbClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<OmdbOptions> options,
        IIntegrationSettingsStore? settingsStore = null,
        ISingleFlightCoordinator? singleFlight = null,
        IOmdbCacheStore? cacheStore = null,
        TimeProvider? timeProvider = null,
        PremiereTelemetry? telemetry = null,
        ProviderResponseCache? responseCache = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _settingsStore = settingsStore;
        _singleFlight = singleFlight ?? new SingleFlightCoordinator();
        _cacheStore = cacheStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _telemetry = telemetry;
        _responseCache = responseCache;
    }

    public async Task<OmdbItem?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var result = await GetByImdbIdWithStatusAsync(imdbId, cancellationToken, forceRefresh);
        if (result.Value is null && result.Failure is { IsLocal: false, Reason: "rate_limited" or "authentication_denied" } failure)
            throw new ExternalApiException(failure.Reason == "rate_limited"
                ? "OMDb lookup failed: Request limit reached." : "OMDb lookup failed: authentication denied.");
        return result.Value;
    }

    public async Task<ProviderRefreshResult<OmdbItem?>> GetByImdbIdWithStatusAsync(
        string imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(imdbId))
        {
            ProviderOutcomeDiagnostics.Record(_telemetry, "omdb", "skipped", "disabled");
            return new(null, true, new("disabled"));
        }
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            ProviderOutcomeDiagnostics.Record(_telemetry, "omdb", "skipped", "not_configured");
            return new(null, true, new("not_configured"));
        }

        if (ProviderRefreshScope.Current is { } refresh)
        {
            var cacheKey = $"omdb:imdb:{imdbId}";
            return await refresh.RunAsync($"omdb:{_httpClient.BaseAddress}:{imdbId}:outcome", forceRefresh,
                async token =>
                {
                    var result = await GetByImdbIdCoreAsync(imdbId, settings, token, forceRefresh);
                    return new ProviderRefreshResult<ProviderRefreshResult<OmdbItem?>>(result, result.Successful);
                }, cancellationToken,
                result => result.Value is not null && IsVerifiedResponse(result.Value)
                    && _cache.TryGetValue(cacheKey, out OmdbItem? latest) && ReferenceEquals(latest, result.Value));
        }
        return await GetByImdbIdCoreAsync(imdbId, settings, cancellationToken, forceRefresh);
    }

    private async Task<ProviderRefreshResult<OmdbItem?>> GetByImdbIdCoreAsync(
        string imdbId, OmdbSourceSettings settings, CancellationToken cancellationToken, bool forceRefresh)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = $"omdb:imdb:{imdbId}";
        _cache.TryGetValue(cacheKey, out OmdbItem? previousMemory);
        if (previousMemory is not null && !IsVerifiedResponse(previousMemory)) previousMemory = null;
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out OmdbItem? cached)
            && cached is not null && IsVerifiedResponse(cached))
        {
            ProviderOutcomeDiagnostics.Record(_telemetry, "omdb", cached.Response == "False" ? "absent" : "completed", "cached_response", true);
            return new(cached, true);
        }

        var persisted = await GetPersistedAsync(imdbId, cancellationToken);
        if (!forceRefresh && IsFresh(persisted)
            && IsVerifiedResponse(persisted!.Item))
        {
            _cache.Set(cacheKey, persisted!.Item, persisted.CachedAtUtc.Add(CacheDuration(persisted.Item)));
            ProviderOutcomeDiagnostics.Record(_telemetry, "omdb", persisted.Item.Response == "False" ? "absent" : "completed", "cached_response", true);
            return new(persisted.Item, true);
        }

        var failures = new ProviderFailureCache(_cache, "omdb", _httpClient.BaseAddress, settings.ApiKey);
        if (failures.Get(_timeProvider.GetUtcNow()) is { } deferred)
            return Failed(persisted?.Item ?? previousMemory, deferred);

        var state = _cacheStore is null
            ? new OmdbProviderCacheState(null, null, null)
            : await _cacheStore.GetProviderStateAsync(cancellationToken);
        if (state.RateLimitedUntilUtc is { } rateLimitedUntil
            && rateLimitedUntil > _timeProvider.GetUtcNow())
        {
            return Failed(persisted?.Item ?? previousMemory, new("local_backoff", null, rateLimitedUntil, true));
        }

        var partition = OptionalProviderResponseCache.Partition(_httpClient.BaseAddress, settings.ApiKey, imdbId.Trim().ToLowerInvariant());
        var outcomeKey = $"payload-outcome-v1:{partition}";
        var outcomeMemoryKey = $"omdb:payload-outcome:{partition}";
        var payloadFailures = new ProviderFailureCache(_cache, "omdb-payload", _httpClient.BaseAddress, partition);
        if (!forceRefresh && payloadFailures.Get(_timeProvider.GetUtcNow()) is { } payloadDeferred)
        {
            _cache.TryGetValue(outcomeMemoryKey, out OmdbPayloadOutcome? memoryOutcome);
            if (!IsSuperseded(memoryOutcome, persisted, partition))
                return Failed(persisted?.Item ?? previousMemory, payloadDeferred);
            payloadFailures.Clear();
            _cache.Remove(outcomeMemoryKey);
        }

        return await _singleFlight.RunAsync(
            forceRefresh ? $"refresh:{cacheKey}" : $"cache:{cacheKey}",
            async token =>
            {
                using var publication = _responseCache?.BeginOperation("omdb", outcomeKey, forceRefresh);
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out OmdbItem? flightCached)
                    && flightCached is not null && IsVerifiedResponse(flightCached))
                {
                    return new ProviderRefreshResult<OmdbItem?>(flightCached, flightCached is not null && IsVerifiedResponse(flightCached));
                }

                var flightPersisted = persisted ?? await GetPersistedAsync(imdbId, token);
                if (!forceRefresh && IsFresh(flightPersisted)
                    && IsVerifiedResponse(flightPersisted!.Item))
                {
                    _cache.Set(cacheKey, flightPersisted!.Item, flightPersisted.CachedAtUtc.Add(CacheDuration(flightPersisted.Item)));
                    return new ProviderRefreshResult<OmdbItem?>(flightPersisted.Item, IsVerifiedResponse(flightPersisted.Item));
                }

                if (!forceRefresh && _responseCache is not null)
                {
                    var outcome = await _responseCache.ReadAsync<OmdbPayloadOutcome>("omdb", outcomeKey, TimeSpan.FromHours(24), token);
                    if (outcome.Status == ProviderResponseCacheReadStatus.StorageUnavailable)
                        return Failed(flightPersisted?.Item ?? previousMemory,
                            new("storage_unavailable", RetryAtUtc: outcome.RetryAtUtc, IsLocal: true));
                    // A failed best-effort clear must not revive a cooldown superseded
                    // by a verified good write, including writes with tied clocks.
                    if (outcome.Response?.Value is { Failure: { StatusCode: 200, Reason: "invalid_imdb_id" or "provider_data_unavailable" } failure } savedOutcome
                        && !IsSuperseded(savedOutcome, flightPersisted, partition)
                        && failure.RetryAtUtc > _timeProvider.GetUtcNow())
                    {
                        return Failed(flightPersisted?.Item ?? previousMemory, failure with { Reason = "local_backoff", IsLocal = true });
                    }
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
                        var failure = ProviderFailure.FromResponse(response, _timeProvider.GetUtcNow(),
                            TimeSpan.FromHours(Math.Max(1, _options.RateLimitBackoffHours)));
                        if (LooksLikeRateLimit(error)) failure = failure with
                        {
                            Reason = "rate_limited",
                            RetryAtUtc = RetryAfterUtc(response) ?? _timeProvider.GetUtcNow().AddHours(Math.Max(1, _options.RateLimitBackoffHours))
                        };
                        failures.Remember(failure, _timeProvider.GetUtcNow());
                        await RecordFailureAsync(response, failure.Reason, token);
                        return Failed(flightPersisted?.Item ?? previousMemory, failure);
                    }

                    using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        var invalid = new ProviderFailure("invalid_response", 200, _timeProvider.GetUtcNow().AddMinutes(1));
                        payloadFailures.Remember(invalid, _timeProvider.GetUtcNow());
                        _cache.Remove(outcomeMemoryKey);
                        await MarkFailureAsync("invalid_response", token);
                        return Failed(flightPersisted?.Item ?? previousMemory, invalid, "payload_non_object");
                    }
                    var item = document.RootElement.Deserialize<OmdbItem>(JsonOptions);
                    if (item is null || item.Ratings is null || item.Ratings.Any(rating => rating is null))
                    {
                        var invalid = new ProviderFailure("invalid_response", 200, _timeProvider.GetUtcNow().AddMinutes(1));
                        payloadFailures.Remember(invalid, _timeProvider.GetUtcNow());
                        _cache.Remove(outcomeMemoryKey);
                        await MarkFailureAsync("invalid_response", token);
                        return Failed(flightPersisted?.Item ?? previousMemory, invalid, "payload_invalid_fields");
                    }
                    if (!IsVerifiedResponse(item))
                    {
                        var error = item.Error ?? "";
                        var failure = LooksLikeRateLimit(error)
                            ? new ProviderFailure("rate_limited", 200, _timeProvider.GetUtcNow().AddHours(Math.Max(1, _options.RateLimitBackoffHours)))
                            : error.Contains("key", StringComparison.OrdinalIgnoreCase)
                                ? new ProviderFailure("authentication_denied", 200, _timeProvider.GetUtcNow().AddMinutes(15))
                            : string.Equals(item.Response, "False", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(error.Trim(), "Incorrect IMDb ID.", StringComparison.OrdinalIgnoreCase)
                                ? new ProviderFailure("invalid_imdb_id", 200, _timeProvider.GetUtcNow().AddHours(24))
                            : string.Equals(item.Response, "False", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(error.Trim(), "Error getting data.", StringComparison.OrdinalIgnoreCase)
                                ? new ProviderFailure("provider_data_unavailable", 200, _timeProvider.GetUtcNow().AddHours(1))
                                : new ProviderFailure("invalid_response", 200, _timeProvider.GetUtcNow().AddMinutes(1));
                        var payloadCategory = PayloadFailureCategory(item, failure);
                        if (failure.Reason is "invalid_response" or "invalid_imdb_id" or "provider_data_unavailable")
                        {
                            // OMDb can reject IDs that TMDb still identifies, or be unable
                            // to retrieve their data. Neither proves title absence. Keep
                            // prior metadata and defer this ID only; force can retry sooner.
                            async Task<bool> PublishFailure(bool current)
                            {
                                if (!current) return false;
                                var payloadOutcome = new OmdbPayloadOutcome(failure, flightPersisted?.SuccessObservationId);
                                var now = _timeProvider.GetUtcNow();
                                payloadFailures.Remember(failure, now);
                                var memoryDeadline = payloadFailures.Get(now)?.RetryAtUtc ?? failure.RetryAtUtc;
                                _cache.Set(outcomeMemoryKey, payloadOutcome, memoryDeadline!.Value - now);
                                if (_responseCache is not null && failure.Reason is "invalid_imdb_id" or "provider_data_unavailable")
                                    await _responseCache.SaveAsync("omdb", outcomeKey, payloadOutcome, token,
                                        lifetime: failure.RetryAtUtc!.Value - _timeProvider.GetUtcNow());
                                await MarkFailureAsync(failure.Reason, token);
                                return true;
                            }
                            if (publication is null) await PublishFailure(true);
                            else await publication.PublishAsync(PublishFailure, token, successfulForceCommit: false);
                        }
                        else
                        {
                            failures.Remember(failure, _timeProvider.GetUtcNow());
                            if (_cacheStore is not null && failure.RetryAtUtc is { } until)
                                await _cacheStore.MarkRateLimitedAsync(until, failure.Reason, token);
                        }
                        return Failed(flightPersisted?.Item ?? previousMemory, failure, payloadCategory);
                    }

                    async Task<bool> PublishSuccess(bool current)
                    {
                        if (!current) return false;
                        if (_cacheStore is not null)
                            await _cacheStore.SetWithObservationAsync(imdbId, item, _timeProvider.GetUtcNow(),
                                $"{partition}:{Guid.NewGuid():N}", token);
                        payloadFailures.Clear();
                        _cache.Remove(outcomeMemoryKey);
                        if (_responseCache is not null)
                            await _responseCache.SaveAsync("omdb", outcomeKey, new OmdbPayloadOutcome(null), token, lifetime: TimeSpan.FromHours(24));
                        _cache.Set(cacheKey, item, CacheDuration(item));
                        return true;
                    }
                    if (publication is null) await PublishSuccess(true);
                    else await publication.PublishAsync(PublishSuccess, token);
                    ProviderOutcomeDiagnostics.Record(_telemetry, "omdb", item.Response == "False" ? "absent" : "completed", "verified_response");
                    return new ProviderRefreshResult<OmdbItem?>(item, true);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return Failed(flightPersisted?.Item ?? previousMemory, new("timeout"));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException exception)
                {
                    var failure = ProviderFailure.FromException(exception);
                    failures.Remember(failure, _timeProvider.GetUtcNow());
                    await MarkFailureAsync(failure.Reason, token);
                    return Failed(flightPersisted?.Item ?? previousMemory, failure);
                }
                catch (JsonException)
                {
                    var failure = new ProviderFailure("invalid_response", 200, _timeProvider.GetUtcNow().AddMinutes(1));
                    payloadFailures.Remember(failure, _timeProvider.GetUtcNow());
                    _cache.Remove(outcomeMemoryKey);
                    await MarkFailureAsync("invalid_response", token);
                    return Failed(flightPersisted?.Item ?? previousMemory, failure, "payload_invalid_json");
                }
            },
            cancellationToken);
    }

    private ProviderRefreshResult<OmdbItem?> Failed(OmdbItem? previous, ProviderFailure failure, string? payloadCategory = null)
    {
        _telemetry?.RecordDiagnostic("provider.outcome", _telemetry.CaptureDiagnosticContext(), new()
        {
            Provider = "omdb", Outcome = "failed", Reason = failure.Reason, Stage = payloadCategory,
            StatusCode = failure.StatusCode, RetryAtUtc = failure.RetryAtUtc, FromCache = failure.IsLocal
        });
        return new(previous, false, failure);
    }

    private sealed record OmdbPayloadOutcome(ProviderFailure? Failure, string? BaselineSuccessObservationId = null);

    private static bool IsSuperseded(OmdbPayloadOutcome? outcome, OmdbCacheEntry? good, string partition)
        => outcome?.Failure is { StatusCode: 200, Reason: "invalid_imdb_id" or "provider_data_unavailable" }
            && good?.SuccessObservationId is { } observation
            && observation.StartsWith(partition + ":", StringComparison.Ordinal)
            && observation != outcome.BaselineSuccessObservationId;

    private static bool IsVerifiedResponse(OmdbItem item)
        => item.Ratings is not null && item.Ratings.All(rating => rating is not null)
            && (string.Equals(item.Response, "False", StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Error?.Trim(), "Movie not found!", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Response, "True", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(item.Error));

    private static string PayloadFailureCategory(OmdbItem item, ProviderFailure failure)
    {
        if (string.IsNullOrWhiteSpace(item.Response)) return "payload_missing_response";
        if (!string.Equals(item.Response, "False", StringComparison.OrdinalIgnoreCase))
            return "payload_invalid_response_marker";

        // Only fixed categories enter diagnostics. Provider error text can contain
        // request details and must never become a diagnostic identifier.
        if (failure.Reason == "rate_limited") return "payload_rate_limited";
        if (failure.Reason == "authentication_denied") return "payload_authentication_denied";
        if (string.IsNullOrWhiteSpace(item.Error)) return "payload_missing_error";
        if (string.Equals(item.Error.Trim(), "Incorrect IMDb ID.", StringComparison.OrdinalIgnoreCase))
            return "payload_invalid_imdb_id";
        if (string.Equals(item.Error.Trim(), "Error getting data.", StringComparison.OrdinalIgnoreCase))
            return "payload_data_error";
        return "payload_unrecognized_error";
    }

    private async Task<OmdbCacheEntry?> GetPersistedAsync(string imdbId, CancellationToken cancellationToken)
    {
        var persisted = _cacheStore is null
            ? null
            : await _cacheStore.GetAsync(imdbId, cancellationToken);
        return persisted is not null && IsVerifiedResponse(persisted.Item) ? persisted : null;
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
            || response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
            || LooksLikeRateLimit(error))
        {
            await _cacheStore.MarkRateLimitedAsync(
                RetryAfterUtc(response) ?? (error == "authentication_denied" ? _timeProvider.GetUtcNow().AddMinutes(15)
                    : _timeProvider.GetUtcNow().AddHours(Math.Max(1, _options.RateLimitBackoffHours))),
                error,
                cancellationToken);
            return;
        }

        await _cacheStore.MarkFailureAsync(error, cancellationToken);
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
