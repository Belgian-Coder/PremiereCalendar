using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class SimklClient : ISimklClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly SimklOptions _options;
    private readonly ISimklSyncStateStore _stateStore;
    private readonly IIntegrationSettingsStore? _settingsStore;

    public SimklClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<SimklOptions> options,
        ISimklSyncStateStore stateStore,
        IIntegrationSettingsStore? settingsStore = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _stateStore = stateStore;
        _settingsStore = settingsStore;
    }

    public async Task<SimklPinCodeResult> RequestPinCodeAsync(CancellationToken cancellationToken)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            return new SimklPinCodeResult(false, Error: "SIMKL client ID is missing.");
        }

        var path = $"oauth/pin?client_id={Uri.EscapeDataString(settings.ClientId.Trim())}";
        var body = await SendPublicTextWithRetryAsync(HttpMethod.Get, path, cancellationToken);
        if (body is null)
        {
            return new SimklPinCodeResult(false, Error: "Could not request a SIMKL PIN.");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var result = GetString(root, "result");
            var userCode = GetString(root, "user_code");
            if (!string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(userCode))
            {
                return new SimklPinCodeResult(
                    false,
                    Error: GetString(root, "message") ?? "SIMKL did not return a PIN code.");
            }

            return new SimklPinCodeResult(
                true,
                UserCode: userCode,
                VerificationUrl: GetString(root, "verification_url") ?? "https://simkl.com/pin/",
                ExpiresInSeconds: GetInt32(root, "expires_in", 0),
                IntervalSeconds: Math.Max(1, GetInt32(root, "interval", 5)));
        }
        catch (JsonException)
        {
            return new SimklPinCodeResult(false, Error: "SIMKL PIN response was not valid JSON.");
        }
    }

    public async Task<SimklPinStatusResult> CheckPinCodeAsync(string userCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userCode))
        {
            return new SimklPinStatusResult(SimklPinStatus.Failed, Message: "SIMKL PIN code is missing.");
        }

        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            return new SimklPinStatusResult(SimklPinStatus.Disabled, Message: "SIMKL client ID is missing.");
        }

        var path = $"oauth/pin/{Uri.EscapeDataString(userCode.Trim())}?client_id={Uri.EscapeDataString(settings.ClientId.Trim())}";
        var body = await SendPublicTextWithRetryAsync(HttpMethod.Get, path, cancellationToken);
        if (body is null)
        {
            return new SimklPinStatusResult(SimklPinStatus.Failed, Message: "Could not check SIMKL authorization.");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var result = GetString(root, "result");
            var message = GetString(root, "message");
            var accessToken = GetString(root, "access_token");

            if (string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(accessToken)
                    ? new SimklPinStatusResult(SimklPinStatus.Failed, Message: "SIMKL did not return an access token.")
                    : new SimklPinStatusResult(SimklPinStatus.Authorized, accessToken);
            }

            if (message?.Contains("pending", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new SimklPinStatusResult(SimklPinStatus.Pending, Message: message);
            }

            if (message?.Contains("slow", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new SimklPinStatusResult(SimklPinStatus.SlowDown, Message: message);
            }

            return new SimklPinStatusResult(SimklPinStatus.Failed, Message: message ?? "SIMKL authorization failed.");
        }
        catch (JsonException)
        {
            return new SimklPinStatusResult(SimklPinStatus.Failed, Message: "SIMKL authorization response was not valid JSON.");
        }
    }

    public async Task<SimklSyncResult> SyncLibraryAsync(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!IsConfigured(settings))
        {
            return new SimklSyncResult(SimklSyncStatus.Disabled);
        }

        var state = await _stateStore.GetAsync(cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;
        var minimumCheckAge = TimeSpan.FromMinutes(Math.Max(0, settings.MinimumActivityCheckMinutes ?? _options.MinimumActivityCheckMinutes));
        if (!forceRefresh
            && minimumCheckAge > TimeSpan.Zero
            && state.LastCheckedUtc is { } lastCheckedUtc
            && nowUtc - lastCheckedUtc < minimumCheckAge)
        {
            return new SimklSyncResult(SimklSyncStatus.Throttled, state.LastActivitiesAllUtc);
        }

        var activitiesJson = await SendTextWithRetryAsync(HttpMethod.Post, "sync/activities", settings, cancellationToken);
        if (activitiesJson is null)
        {
            await _stateStore.SaveAsync(state with { LastCheckedUtc = nowUtc }, cancellationToken);
            return new SimklSyncResult(SimklSyncStatus.Failed, state.LastActivitiesAllUtc, "Could not fetch Simkl activities.");
        }

        var activitiesAllUtc = ExtractActivitiesAllUtc(activitiesJson);
        if (string.IsNullOrWhiteSpace(activitiesAllUtc))
        {
            await _stateStore.SaveAsync(state with { LastCheckedUtc = nowUtc }, cancellationToken);
            return new SimklSyncResult(SimklSyncStatus.Failed, state.LastActivitiesAllUtc, "Simkl activities response did not include an all timestamp.");
        }

        if (state.InitialSyncCompleted
            && string.Equals(state.LastActivitiesAllUtc, activitiesAllUtc, StringComparison.Ordinal)
            && !forceRefresh)
        {
            await _stateStore.SaveAsync(state with { LastCheckedUtc = nowUtc, LastActivitiesJson = activitiesJson }, cancellationToken);
            return new SimklSyncResult(SimklSyncStatus.Unchanged, activitiesAllUtc);
        }

        if (!state.InitialSyncCompleted || string.IsNullOrWhiteSpace(state.LastActivitiesAllUtc))
        {
            var initialSucceeded = await FetchInitialLibrariesSequentiallyAsync(settings, cancellationToken);
            if (!initialSucceeded)
            {
                await _stateStore.SaveAsync(state with { LastCheckedUtc = nowUtc }, cancellationToken);
                return new SimklSyncResult(SimklSyncStatus.Failed, state.LastActivitiesAllUtc, "Initial Simkl sync failed.");
            }

            await _stateStore.SaveAsync(
                new SimklSyncState(activitiesAllUtc, activitiesJson, InitialSyncCompleted: true, LastCheckedUtc: nowUtc),
                cancellationToken);
            return new SimklSyncResult(SimklSyncStatus.InitialSyncCompleted, activitiesAllUtc);
        }

        var deltaPath = $"sync/all-items/?date_from={Uri.EscapeDataString(state.LastActivitiesAllUtc)}";
        var delta = await SendTextWithRetryAsync(HttpMethod.Get, deltaPath, settings, cancellationToken);
        if (delta is null)
        {
            await _stateStore.SaveAsync(state with { LastCheckedUtc = nowUtc }, cancellationToken);
            return new SimklSyncResult(SimklSyncStatus.Failed, state.LastActivitiesAllUtc, "Simkl delta sync failed.");
        }

        await _stateStore.SaveAsync(
            new SimklSyncState(activitiesAllUtc, activitiesJson, InitialSyncCompleted: true, LastCheckedUtc: nowUtc),
            cancellationToken);
        return new SimklSyncResult(SimklSyncStatus.DeltaSyncCompleted, activitiesAllUtc);
    }

    private async Task<bool> FetchInitialLibrariesSequentiallyAsync(
        SimklSourceSettings settings,
        CancellationToken cancellationToken)
    {
        foreach (var path in new[] { "sync/shows", "sync/movies", "sync/anime" })
        {
            var body = await SendTextWithRetryAsync(HttpMethod.Get, path, settings, cancellationToken);
            if (body is null)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<string?> SendPublicTextWithRetryAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxAttempts)
                {
                    var delay = RetryAfterDelay(response) ?? TimeSpan.FromSeconds(Math.Min(4, attempt * 2));
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                return await response.Content.ReadAsStringAsync(cancellationToken);
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
        }

        return null;
    }

    private async Task<string?> SendTextWithRetryAsync(
        HttpMethod method,
        string path,
        SimklSourceSettings settings,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken.Trim());
            request.Headers.Add("simkl-api-key", settings.ClientId.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxAttempts)
                {
                    var delay = RetryAfterDelay(response) ?? TimeSpan.FromSeconds(Math.Min(4, attempt * 2));
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                return await response.Content.ReadAsStringAsync(cancellationToken);
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
        }

        return null;
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int GetInt32(JsonElement root, string propertyName, int fallback)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => fallback
        };
    }

    private async ValueTask<SimklSourceSettings> GetEffectiveSettingsAsync(CancellationToken cancellationToken)
    {
        return _settingsStore is null
            ? new SimklSourceSettings
            {
                Enabled = _options.Enabled,
                ClientId = _options.ClientId ?? "",
                ClientSecret = _options.ClientSecret ?? "",
                AccessToken = _options.AccessToken ?? "",
                MinimumActivityCheckMinutes = _options.MinimumActivityCheckMinutes
            }
            : (await _settingsStore.GetAsync(cancellationToken)).Sources.Simkl;
    }

    private static bool IsConfigured(SimklSourceSettings settings)
    {
        return settings.Enabled
            && !string.IsNullOrWhiteSpace(settings.ClientId)
            && !string.IsNullOrWhiteSpace(settings.AccessToken);
    }

    private static string? ExtractActivitiesAllUtc(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("all", out var all)
                && all.ValueKind == JsonValueKind.String
                    ? all.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan? RetryAfterDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : null;
        }

        return null;
    }
}
