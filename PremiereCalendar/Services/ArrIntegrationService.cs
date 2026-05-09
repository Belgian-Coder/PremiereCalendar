using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class ArrIntegrationService : IArrIntegrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly IIntegrationSettingsStore _settingsStore;
    private readonly ILogger<ArrIntegrationService> _logger;

    public ArrIntegrationService(
        HttpClient httpClient,
        IIntegrationSettingsStore settingsStore,
        ILogger<ArrIntegrationService> logger)
    {
        _httpClient = httpClient;
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public async Task<ArrAddResult> AddAsync(PremiereItem item, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsStore.GetAsync(cancellationToken);
        return item.MediaType == PremiereMediaType.Movie
            ? await AddMovieAsync(item, settings.Radarr, cancellationToken)
            : await AddSeriesAsync(item, settings.Sonarr, cancellationToken);
    }

    public async Task<ArrConnectionOptions> GetSonarrOptionsAsync(
        SonarrIntegrationSettings settings,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured(settings.BaseUrl, settings.ApiKey, "Sonarr");

        var rootsTask = GetRootFoldersAsync(settings.BaseUrl, settings.ApiKey, cancellationToken);
        var profilesTask = GetQualityProfilesAsync(settings.BaseUrl, settings.ApiKey, cancellationToken);

        await Task.WhenAll(rootsTask, profilesTask);
        return new ArrConnectionOptions(await rootsTask, await profilesTask);
    }

    public async Task<ArrConnectionOptions> GetRadarrOptionsAsync(
        RadarrIntegrationSettings settings,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured(settings.BaseUrl, settings.ApiKey, "Radarr");

        var rootsTask = GetRootFoldersAsync(settings.BaseUrl, settings.ApiKey, cancellationToken);
        var profilesTask = GetQualityProfilesAsync(settings.BaseUrl, settings.ApiKey, cancellationToken);

        await Task.WhenAll(rootsTask, profilesTask);
        return new ArrConnectionOptions(await rootsTask, await profilesTask);
    }

    private async Task<ArrAddResult> AddMovieAsync(
        PremiereItem item,
        RadarrIntegrationSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            return new ArrAddResult(false, false, ArrIntegrationTarget.Radarr, item.Title, "Radarr is disabled.");
        }

        EnsureConfigured(settings.BaseUrl, settings.ApiKey, "Radarr");

        try
        {
            var existing = await GetJsonAsync<List<RadarrMovieLookupDto>>(
                settings.BaseUrl,
                settings.ApiKey,
                $"api/v3/movie?tmdbId={item.TmdbId}",
                cancellationToken) ?? [];

            if (existing.Count > 0)
            {
                return new ArrAddResult(true, true, ArrIntegrationTarget.Radarr, item.Title, $"{item.Title} is already in Radarr.");
            }

            var movie = await GetJsonNodeAsync(
                settings.BaseUrl,
                settings.ApiKey,
                $"api/v3/movie/lookup/tmdb?tmdbId={item.TmdbId}",
                cancellationToken);

            if (movie is not JsonObject movieObject)
            {
                return new ArrAddResult(false, false, ArrIntegrationTarget.Radarr, item.Title, "Radarr could not find this TMDb movie.");
            }

            var rootFolderPath = await ResolveRootFolderPathAsync(
                settings.BaseUrl,
                settings.ApiKey,
                settings.RootFolderPath,
                "Radarr",
                cancellationToken);
            var qualityProfileId = await ResolveQualityProfileIdAsync(
                settings.BaseUrl,
                settings.ApiKey,
                settings.QualityProfileId,
                "Radarr",
                cancellationToken);

            movieObject["rootFolderPath"] = rootFolderPath;
            movieObject["qualityProfileId"] = qualityProfileId;
            movieObject["monitored"] = settings.Monitored;
            movieObject["minimumAvailability"] = string.IsNullOrWhiteSpace(settings.MinimumAvailability)
                ? "released"
                : settings.MinimumAvailability;
            movieObject["addOptions"] = new JsonObject
            {
                ["searchForMovie"] = settings.SearchOnAdd
            };
            await ApplyTagOnAddAsync(
                movieObject,
                settings.TagOnAdd,
                settings.BaseUrl,
                settings.ApiKey,
                "Radarr",
                cancellationToken);

            var created = await PostJsonNodeAsync(
                settings.BaseUrl,
                settings.ApiKey,
                "api/v3/movie",
                movieObject,
                cancellationToken);

            var title = created?["title"]?.GetValue<string>() ?? item.Title;
            return new ArrAddResult(true, false, ArrIntegrationTarget.Radarr, title, $"{title} was added to Radarr.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to add TMDb movie {TmdbId} to Radarr.", item.TmdbId);
            return new ArrAddResult(false, false, ArrIntegrationTarget.Radarr, item.Title, $"Radarr add failed: {ex.Message}");
        }
    }

    private async Task<ArrAddResult> AddSeriesAsync(
        PremiereItem item,
        SonarrIntegrationSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            return new ArrAddResult(false, false, ArrIntegrationTarget.Sonarr, item.Title, "Sonarr is disabled.");
        }

        EnsureConfigured(settings.BaseUrl, settings.ApiKey, "Sonarr");

        if (item.TvdbId is not > 0)
        {
            return new ArrAddResult(false, false, ArrIntegrationTarget.Sonarr, item.Title, "Sonarr needs a TVDB ID for series adds. This item has no TVDB ID yet.");
        }

        try
        {
            var existing = await GetJsonAsync<List<SonarrSeriesLookupDto>>(
                settings.BaseUrl,
                settings.ApiKey,
                $"api/v3/series?tvdbId={item.TvdbId.Value}",
                cancellationToken) ?? [];

            if (existing.Count > 0)
            {
                return new ArrAddResult(true, true, ArrIntegrationTarget.Sonarr, item.Title, $"{item.Title} is already in Sonarr.");
            }

            var lookup = await GetJsonAsync<List<JsonObject>>(
                settings.BaseUrl,
                settings.ApiKey,
                $"api/v3/series/lookup?term=tvdb:{item.TvdbId.Value}",
                cancellationToken) ?? [];

            var seriesObject = lookup.FirstOrDefault();
            if (seriesObject is null)
            {
                return new ArrAddResult(false, false, ArrIntegrationTarget.Sonarr, item.Title, "Sonarr could not find this TVDB series.");
            }

            var rootFolderPath = await ResolveRootFolderPathAsync(
                settings.BaseUrl,
                settings.ApiKey,
                settings.RootFolderPath,
                "Sonarr",
                cancellationToken);
            var qualityProfileId = await ResolveQualityProfileIdAsync(
                settings.BaseUrl,
                settings.ApiKey,
                settings.QualityProfileId,
                "Sonarr",
                cancellationToken);

            seriesObject["rootFolderPath"] = rootFolderPath;
            seriesObject["qualityProfileId"] = qualityProfileId;
            seriesObject["monitored"] = true;
            seriesObject["seasonFolder"] = settings.SeasonFolder;
            seriesObject["seriesType"] = string.IsNullOrWhiteSpace(settings.SeriesType)
                ? "standard"
                : settings.SeriesType;
            seriesObject["addOptions"] = new JsonObject
            {
                ["monitor"] = string.IsNullOrWhiteSpace(settings.Monitor) ? "all" : settings.Monitor,
                ["searchForMissingEpisodes"] = settings.SearchOnAdd,
                ["searchForCutoffUnmetEpisodes"] = false
            };
            await ApplyTagOnAddAsync(
                seriesObject,
                settings.TagOnAdd,
                settings.BaseUrl,
                settings.ApiKey,
                "Sonarr",
                cancellationToken);

            var created = await PostJsonNodeAsync(
                settings.BaseUrl,
                settings.ApiKey,
                "api/v3/series",
                seriesObject,
                cancellationToken);

            var title = created?["title"]?.GetValue<string>() ?? item.Title;
            return new ArrAddResult(true, false, ArrIntegrationTarget.Sonarr, title, $"{title} was added to Sonarr.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to add TVDB series {TvdbId} to Sonarr.", item.TvdbId);
            return new ArrAddResult(false, false, ArrIntegrationTarget.Sonarr, item.Title, $"Sonarr add failed: {ex.Message}");
        }
    }

    private async Task<string> ResolveRootFolderPathAsync(
        string baseUrl,
        string apiKey,
        string configuredPath,
        string serviceName,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath.Trim();
        }

        var rootFolders = await GetRootFoldersAsync(baseUrl, apiKey, cancellationToken);
        return rootFolders.FirstOrDefault()?.Path
            ?? throw new InvalidOperationException($"{serviceName} has no root folder configured.");
    }

    private async Task<int> ResolveQualityProfileIdAsync(
        string baseUrl,
        string apiKey,
        int? configuredId,
        string serviceName,
        CancellationToken cancellationToken)
    {
        if (configuredId is > 0)
        {
            return configuredId.Value;
        }

        var profiles = await GetQualityProfilesAsync(baseUrl, apiKey, cancellationToken);
        return profiles.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException($"{serviceName} has no quality profile configured.");
    }

    private async Task<IReadOnlyList<ArrRootFolder>> GetRootFoldersAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var folders = await GetJsonAsync<List<ArrRootFolderDto>>(
            baseUrl,
            apiKey,
            "api/v3/rootfolder",
            cancellationToken) ?? [];

        return folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.Path))
            .Select(folder => new ArrRootFolder(folder.Id, folder.Path, folder.FreeSpace))
            .ToArray();
    }

    private async Task<IReadOnlyList<ArrOption>> GetQualityProfilesAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var profiles = await GetJsonAsync<List<ArrQualityProfileDto>>(
            baseUrl,
            apiKey,
            "api/v3/qualityprofile",
            cancellationToken) ?? [];

        return profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Name))
            .Select(profile => new ArrOption(profile.Id, profile.Name))
            .ToArray();
    }

    private async Task ApplyTagOnAddAsync(
        JsonObject payload,
        string tagOnAdd,
        string baseUrl,
        string apiKey,
        string serviceName,
        CancellationToken cancellationToken)
    {
        var tagLabel = tagOnAdd.Trim();
        if (string.IsNullOrWhiteSpace(tagLabel))
        {
            return;
        }

        var tagId = await EnsureTagAsync(baseUrl, apiKey, serviceName, tagLabel, cancellationToken);
        AddTagId(payload, tagId);
    }

    private async Task<int> EnsureTagAsync(
        string baseUrl,
        string apiKey,
        string serviceName,
        string tagLabel,
        CancellationToken cancellationToken)
    {
        var tags = await GetJsonAsync<List<ArrTagDto>>(
            baseUrl,
            apiKey,
            "api/v3/tag",
            cancellationToken) ?? [];

        var existing = tags.FirstOrDefault(tag =>
            tag.Id > 0
            && string.Equals(tag.Label, tagLabel, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing.Id;
        }

        var created = await PostJsonNodeAsync(
            baseUrl,
            apiKey,
            "api/v3/tag",
            new JsonObject { ["label"] = tagLabel },
            cancellationToken);

        var createdId = created?["id"]?.GetValue<int>() ?? 0;
        if (createdId <= 0)
        {
            throw new InvalidOperationException($"{serviceName} did not return an ID for tag '{tagLabel}'.");
        }

        return createdId;
    }

    private static void AddTagId(JsonObject payload, int tagId)
    {
        if (tagId <= 0)
        {
            return;
        }

        if (payload["tags"] is not JsonArray tags)
        {
            tags = [];
            payload["tags"] = tags;
        }

        if (!tags.Any(tag => TryGetInt(tag, out var existingTagId) && existingTagId == tagId))
        {
            tags.Add(tagId);
        }
    }

    private static bool TryGetInt(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private async Task<T?> GetJsonAsync<T>(
        string baseUrl,
        string apiKey,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, apiKey, path);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private async Task<JsonNode?> GetJsonNodeAsync(
        string baseUrl,
        string apiKey,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, baseUrl, apiKey, path);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonNode>(JsonOptions, cancellationToken);
    }

    private async Task<JsonNode?> PostJsonNodeAsync(
        string baseUrl,
        string apiKey,
        string path,
        JsonNode payload,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, baseUrl, apiKey, path);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonNode>(JsonOptions, cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string baseUrl,
        string apiKey,
        string path)
    {
        EnsureConfigured(baseUrl, apiKey, "Integration");

        var requestUri = new Uri(new Uri(NormalizeBaseUrl(baseUrl)), path);
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("X-Api-Key", apiKey.Trim());
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = string.IsNullOrWhiteSpace(body)
            ? response.ReasonPhrase
            : body.Length > 400
                ? body[..400]
                : body;

        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : $"{trimmed}/";
    }

    private static void EnsureConfigured(string baseUrl, string apiKey, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException($"{serviceName} URL is missing.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"{serviceName} API key is missing.");
        }

        if (!Uri.TryCreate(NormalizeBaseUrl(baseUrl), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{serviceName} URL must be an absolute http or https URL.");
        }
    }

    private sealed record ArrRootFolderDto(int Id, string Path, long? FreeSpace);

    private sealed record ArrQualityProfileDto(int Id, string Name);

    private sealed record ArrTagDto(int Id, string? Label);

    private sealed record RadarrMovieLookupDto(int Id, int TmdbId, string Title);

    private sealed record SonarrSeriesLookupDto(int Id, int TvdbId, string Title);
}
