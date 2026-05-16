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
        return SemanticVersion.TryParse(latest, out var latestVersion)
            && SemanticVersion.TryParse(current, out var currentVersion)
            && latestVersion.CompareTo(currentVersion) > 0;
    }

    private static string NormalizeVersion(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        var buildMetadataIndex = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (buildMetadataIndex > 0)
        {
            trimmed = trimmed[..buildMetadataIndex];
        }

        return SemanticVersion.TryParse(trimmed, out var version)
            ? version.ToString()
            : "0.0.0";
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

    private sealed record SemanticVersion(
        int Major,
        int Minor,
        int Patch,
        string? Prerelease) : IComparable<SemanticVersion>
    {
        public static bool TryParse(string value, out SemanticVersion version)
        {
            version = new SemanticVersion(0, 0, 0, null);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            var dashIndex = trimmed.IndexOf('-', StringComparison.Ordinal);
            var core = dashIndex >= 0 ? trimmed[..dashIndex] : trimmed;
            var prerelease = dashIndex >= 0 ? trimmed[(dashIndex + 1)..] : null;
            var parts = core.Split('.', StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 3
                || !int.TryParse(parts[0], out var major)
                || major < 0)
            {
                return false;
            }

            var minor = 0;
            if (parts.Length > 1 && (!int.TryParse(parts[1], out minor) || minor < 0))
            {
                return false;
            }

            var patch = 0;
            if (parts.Length > 2 && (!int.TryParse(parts[2], out patch) || patch < 0))
            {
                return false;
            }

            version = new SemanticVersion(
                major,
                minor,
                patch,
                string.IsNullOrWhiteSpace(prerelease) ? null : prerelease.Trim());
            return true;
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            var coreComparison = Major.CompareTo(other.Major);
            if (coreComparison != 0)
            {
                return coreComparison;
            }

            coreComparison = Minor.CompareTo(other.Minor);
            if (coreComparison != 0)
            {
                return coreComparison;
            }

            coreComparison = Patch.CompareTo(other.Patch);
            if (coreComparison != 0)
            {
                return coreComparison;
            }

            return ComparePrerelease(Prerelease, other.Prerelease);
        }

        public override string ToString()
        {
            var core = $"{Major}.{Minor}.{Patch}";
            return Prerelease is null ? core : $"{core}-{Prerelease}";
        }

        private static int ComparePrerelease(string? left, string? right)
        {
            if (string.Equals(left, right, StringComparison.Ordinal))
            {
                return 0;
            }

            if (left is null)
            {
                return 1;
            }

            if (right is null)
            {
                return -1;
            }

            var leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var count = Math.Max(leftParts.Length, rightParts.Length);
            for (var index = 0; index < count; index++)
            {
                if (index >= leftParts.Length)
                {
                    return -1;
                }

                if (index >= rightParts.Length)
                {
                    return 1;
                }

                var leftIsNumber = int.TryParse(leftParts[index], out var leftNumber);
                var rightIsNumber = int.TryParse(rightParts[index], out var rightNumber);
                var comparison = (leftIsNumber, rightIsNumber) switch
                {
                    (true, true) => leftNumber.CompareTo(rightNumber),
                    (true, false) => -1,
                    (false, true) => 1,
                    _ => string.Compare(leftParts[index], rightParts[index], StringComparison.OrdinalIgnoreCase)
                };
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return 0;
        }
    }
}

public sealed record ReleaseUpdateResult(
    string CurrentVersion,
    string LatestVersion,
    bool IsUpdateAvailable,
    string ReleaseUrl,
    string ReleaseName,
    DateTimeOffset? PublishedUtc);
