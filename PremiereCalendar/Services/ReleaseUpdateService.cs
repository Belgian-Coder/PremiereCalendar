using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace PremiereCalendar.Services;

public sealed class ReleaseUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly string _currentVersion;

    [ActivatorUtilitiesConstructor]
    public ReleaseUpdateService(HttpClient httpClient)
        : this(httpClient, CurrentAssemblyVersion())
    {
    }

    public ReleaseUpdateService(HttpClient httpClient, string currentVersion)
    {
        _httpClient = httpClient;
        _currentVersion = NormalizeVersion(currentVersion);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PremiereCalendar", "1.0"));
        }
    }

    public async Task<ReleaseUpdateResult> CheckLatestAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync("repos/Belgian-Coder/PremiereCalendar/releases/latest", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var latest = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(stream, JsonOptions, cancellationToken);
        var latestVersion = NormalizeVersion(latest?.TagName ?? "");
        return new ReleaseUpdateResult(
            _currentVersion,
            latestVersion,
            IsNewer(latestVersion, _currentVersion),
            latest?.HtmlUrl ?? "",
            latest?.Name ?? latestVersion,
            latest?.PublishedAt);
    }

    private static bool IsNewer(string latest, string current)
    {
        return Version.TryParse(latest, out var latestVersion)
            && Version.TryParse(current, out var currentVersion)
            && latestVersion > currentVersion;
    }

    private static string NormalizeVersion(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        var dashIndex = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex > 0)
        {
            trimmed = trimmed[..dashIndex];
        }

        var buildMetadataIndex = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (buildMetadataIndex > 0)
        {
            trimmed = trimmed[..buildMetadataIndex];
        }

        return string.IsNullOrWhiteSpace(trimmed) ? "0.0.0" : trimmed;
    }

    private static string CurrentAssemblyVersion()
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.0.0";
    }

    private sealed record GitHubReleaseResponse(
        [property: JsonPropertyName("tag_name")]
        string TagName,
        [property: JsonPropertyName("html_url")]
        string HtmlUrl,
        [property: JsonPropertyName("name")]
        string Name,
        [property: JsonPropertyName("published_at")]
        DateTimeOffset? PublishedAt);
}

public sealed record ReleaseUpdateResult(
    string CurrentVersion,
    string LatestVersion,
    bool IsUpdateAvailable,
    string ReleaseUrl,
    string ReleaseName,
    DateTimeOffset? PublishedUtc);
