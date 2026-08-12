using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class SimklClient : ISimklClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CalendarCacheDuration = TimeSpan.FromHours(5);
    private const int MaxCalendarCacheEntries = 8;

    private readonly HttpClient _httpClient;
    private readonly SimklOptions _options;
    private readonly ISimklSyncStateStore _stateStore;
    private readonly IIntegrationSettingsStore? _settingsStore;
    private readonly object _calendarCacheLock = new();
    private readonly Dictionary<string, LinkedListNode<CalendarCacheEntry>> _calendarCache = new(StringComparer.Ordinal);
    private readonly LinkedList<CalendarCacheEntry> _calendarCacheLru = new();

    public SimklClient(
        HttpClient httpClient,
        IOptions<SimklOptions> options,
        ISimklSyncStateStore stateStore,
        IIntegrationSettingsStore? settingsStore = null)
    {
        _httpClient = httpClient;
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

    public async Task<IReadOnlyList<SimklCalendarItem>> GetCalendarAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!IsCalendarConfigured(settings))
        {
            return [];
        }

        var items = new List<SimklCalendarItem>();
        foreach (var (path, type) in CalendarPaths(start, end))
        {
            var fileItems = await GetCalendarFileAsync(path, type, settings, start, end, cancellationToken, forceRefresh);
            items.AddRange(fileItems);
        }

        return items
            .Where(item => CalendarItemDate(item) >= start && CalendarItemDate(item) <= end)
            .GroupBy(CalendarItemKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
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

    private async Task<IReadOnlyList<SimklCalendarItem>> GetCalendarFileAsync(
        string path,
        SimklCalendarItemType type,
        SimklSourceSettings settings,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var cacheKey = $"{path}:{start:yyyyMMdd}:{end:yyyyMMdd}";
        if (!forceRefresh && TryGetCachedCalendar(cacheKey, out var cached))
        {
            return cached;
        }

        var uri = BuildCalendarUri(path, settings);
        var items = await StreamCalendarItemsAsync(uri, type, start, end, cancellationToken);
        if (items is null)
        {
            return [];
        }

        SetCachedCalendar(cacheKey, items);
        return items;
    }

    private async Task<IReadOnlyList<SimklCalendarItem>?> StreamCalendarItemsAsync(
        Uri uri,
        SimklCalendarItemType type,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
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

                var items = new List<SimklCalendarItem>();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await foreach (var rawItem in JsonSerializer.DeserializeAsyncEnumerable<SimklCalendarPayloadItem>(
                    stream,
                    JsonOptions,
                    cancellationToken))
                {
                    if (rawItem is null || ToCalendarItem(type, rawItem) is not { } item)
                    {
                        continue;
                    }

                    var date = CalendarItemDate(item);
                    if (date >= start && date <= end)
                    {
                        items.Add(item);
                    }
                }

                return items;
            }
            catch (JsonException)
            {
                return null;
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

    private bool TryGetCachedCalendar(string cacheKey, out IReadOnlyList<SimklCalendarItem> items)
    {
        lock (_calendarCacheLock)
        {
            if (!_calendarCache.TryGetValue(cacheKey, out var node))
            {
                items = [];
                return false;
            }

            if (node.Value.ExpiresUtc <= DateTimeOffset.UtcNow)
            {
                _calendarCache.Remove(cacheKey);
                _calendarCacheLru.Remove(node);
                items = [];
                return false;
            }

            _calendarCacheLru.Remove(node);
            _calendarCacheLru.AddFirst(node);
            items = node.Value.Items;
            return true;
        }
    }

    private void SetCachedCalendar(string cacheKey, IReadOnlyList<SimklCalendarItem> items)
    {
        lock (_calendarCacheLock)
        {
            if (_calendarCache.Remove(cacheKey, out var existing))
            {
                _calendarCacheLru.Remove(existing);
            }

            var entry = new CalendarCacheEntry(cacheKey, items, DateTimeOffset.UtcNow + CalendarCacheDuration);
            var node = _calendarCacheLru.AddFirst(entry);
            _calendarCache[cacheKey] = node;

            while (_calendarCache.Count > MaxCalendarCacheEntries && _calendarCacheLru.Last is { } oldest)
            {
                _calendarCache.Remove(oldest.Value.Key);
                _calendarCacheLru.RemoveLast();
            }
        }
    }

    internal int CalendarCacheEntryCount
    {
        get
        {
            lock (_calendarCacheLock)
            {
                return _calendarCache.Count;
            }
        }
    }

    private IEnumerable<(string Path, SimklCalendarItemType Type)> CalendarPaths(DateOnly start, DateOnly end)
    {
        yield return ("calendar/tv.json", SimklCalendarItemType.Tv);
        yield return ("calendar/movie_release.json", SimklCalendarItemType.MovieRelease);

        var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
        if (start >= todayUtc.AddDays(-1) && end <= todayUtc.AddDays(33))
        {
            yield break;
        }

        var cursor = new DateOnly(start.Year, start.Month, 1);
        var lastMonth = new DateOnly(end.Year, end.Month, 1);
        while (cursor <= lastMonth)
        {
            yield return ($"calendar/{cursor.Year.ToString(CultureInfo.InvariantCulture)}/{cursor.Month.ToString(CultureInfo.InvariantCulture)}/tv.json", SimklCalendarItemType.Tv);
            yield return ($"calendar/{cursor.Year.ToString(CultureInfo.InvariantCulture)}/{cursor.Month.ToString(CultureInfo.InvariantCulture)}/movie_release.json", SimklCalendarItemType.MovieRelease);
            cursor = cursor.AddMonths(1);
        }
    }

    private Uri BuildCalendarUri(string path, SimklSourceSettings settings)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_options.CalendarBaseUrl)
            ? "https://data.simkl.in/"
            : _options.CalendarBaseUrl.Trim();
        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
        {
            baseUrl += "/";
        }

        var uri = new Uri(new Uri(baseUrl), path);
        var query = string.Join(
            "&",
            $"client_id={Uri.EscapeDataString(settings.ClientId.Trim())}",
            $"app-name={Uri.EscapeDataString(AppParameter(_options.AppName, "premiere-calendar"))}",
            $"app-version={Uri.EscapeDataString(AppParameter(_options.AppVersion, "1.0"))}");
        return new Uri($"{uri}?{query}");
    }

    private static SimklCalendarItem? ToCalendarItem(SimklCalendarItemType type, SimklCalendarPayloadItem item)
    {
        if (!TryParseCalendarDate(item.Date, item.ReleaseDate, out var date))
        {
            return null;
        }

        return new SimklCalendarItem(
            type,
            item.Title,
            date,
            TryParseDateOnly(item.ReleaseDate),
            NormalizeSimklUrl(item.Url),
            new SimklCalendarIds(
                JsonElementToInt(item.Ids?.SimklId),
                JsonElementToString(item.Ids?.Tmdb),
                JsonElementToString(item.Ids?.Imdb),
                JsonElementToString(item.Ids?.Tvdb)),
            new SimklCalendarRatings(ToRating(item.Ratings?.Imdb)),
            item.Episode is null
                ? null
                : new SimklCalendarEpisode(
                    JsonElementToInt(item.Episode.Season),
                    JsonElementToInt(item.Episode.Episode),
                    NormalizeSimklUrl(item.Episode.Url)));
    }

    private static SimklRating? ToRating(SimklPayloadRating? rating)
    {
        if (rating is null)
        {
            return null;
        }

        var value = JsonElementToDouble(rating.Rating);
        var votes = JsonElementToInt(rating.Votes);
        return value is null && votes is null ? null : new SimklRating(value, votes);
    }

    private static bool TryParseCalendarDate(string? value, string? fallbackDate, out DateTimeOffset date)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date))
        {
            return true;
        }

        var releaseDate = TryParseDateOnly(fallbackDate);
        if (releaseDate is { } parsed)
        {
            date = new DateTimeOffset(parsed.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return true;
        }

        date = default;
        return false;
    }

    private static DateOnly? TryParseDateOnly(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;
    }

    private static DateOnly CalendarItemDate(SimklCalendarItem item)
    {
        return DateOnly.FromDateTime(item.Date.DateTime);
    }

    private static string CalendarItemKey(SimklCalendarItem item)
    {
        var date = CalendarItemDate(item);
        return $"{item.Type}:{date:yyyyMMdd}:{item.Ids.SimklId}:{item.Ids.Tmdb}:{item.Ids.Imdb}:{item.Episode?.Season}:{item.Episode?.Episode}:{item.Title}";
    }

    private static string? NormalizeSimklUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.ToString();
        }

        return trimmed.StartsWith("/", StringComparison.Ordinal)
            ? $"https://simkl.com{trimmed}"
            : null;
    }

    private static string AppParameter(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? JsonElementToString(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return element.Value.ValueKind == JsonValueKind.String
            ? element.Value.GetString()
            : element.Value.GetRawText();
    }

    private static int? JsonElementToInt(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return element.Value.ValueKind switch
        {
            JsonValueKind.Number when element.Value.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(element.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static double? JsonElementToDouble(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return element.Value.ValueKind switch
        {
            JsonValueKind.Number when element.Value.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(element.Value.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
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

    private static bool IsCalendarConfigured(SimklSourceSettings settings)
    {
        return settings.Enabled && !string.IsNullOrWhiteSpace(settings.ClientId);
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

    private sealed record SimklCalendarPayloadItem
    {
        public string? Title { get; init; }
        public string? Date { get; init; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; init; }

        public string? Url { get; init; }
        public SimklPayloadIds? Ids { get; init; }
        public SimklPayloadRatings? Ratings { get; init; }
        public SimklPayloadEpisode? Episode { get; init; }
    }

    private sealed record SimklPayloadIds
    {
        [JsonPropertyName("simkl_id")]
        public JsonElement? SimklId { get; init; }

        public JsonElement? Tmdb { get; init; }
        public JsonElement? Imdb { get; init; }
        public JsonElement? Tvdb { get; init; }
    }

    private sealed record SimklPayloadRatings
    {
        public SimklPayloadRating? Imdb { get; init; }
    }

    private sealed record SimklPayloadRating
    {
        public JsonElement? Rating { get; init; }
        public JsonElement? Votes { get; init; }
    }

    private sealed record SimklPayloadEpisode
    {
        public JsonElement? Season { get; init; }
        public JsonElement? Episode { get; init; }
        public string? Url { get; init; }
    }

    private sealed record CalendarCacheEntry(
        string Key,
        IReadOnlyList<SimklCalendarItem> Items,
        DateTimeOffset ExpiresUtc);
}
