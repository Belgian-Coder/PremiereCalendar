using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class RottenTomatoesClient : IRottenTomatoesClient
{
    private static readonly Regex SearchRowRegex = new(
        "<search-page-media-row\\b(?<attrs>[^>]*)>(?<body>.*?)</search-page-media-row>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex InfoNameLinkRegex = new(
        "<a\\b(?<attrs>[^>]*)data-qa=[\"']info-name[\"'](?<attrs2>[^>]*)>(?<title>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ScorecardJsonRegex = new(
        "<script\\b[^>]*id=[\"']media-scorecard-json[\"'][^>]*>(?<json>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new("<.*?>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumericRegex = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly RottenTomatoesOptions _options;
    private readonly ILogger<RottenTomatoesClient> _logger;
    private readonly IWikimediaClient? _wikimediaClient;
    private readonly ISingleFlightCoordinator _singleFlight;

    public RottenTomatoesClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<RottenTomatoesOptions> options,
        ILogger<RottenTomatoesClient> logger,
        IWikimediaClient? wikimediaClient = null,
        ISingleFlightCoordinator? singleFlight = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
        _wikimediaClient = wikimediaClient;
        _singleFlight = singleFlight ?? new SingleFlightCoordinator();
    }

    public async Task<int?> GetTomatometerScoreAsync(
        PremiereMediaType mediaType,
        string title,
        int? year,
        string? wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var normalizedTitle = NormalizeTitle(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return null;
        }

        var cacheKey = $"rt:score:{mediaType}:{normalizedTitle}:{year?.ToString(CultureInfo.InvariantCulture) ?? ""}:{wikidataId?.Trim() ?? ""}";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out int cachedScore))
        {
            return cachedScore >= 0 ? cachedScore : null;
        }

        return await _singleFlight.RunAsync(
            forceRefresh ? $"refresh:{cacheKey}" : $"cache:{cacheKey}",
            async token =>
            {
                if (!forceRefresh && _cache.TryGetValue(cacheKey, out int flightCachedScore))
                {
                    return flightCachedScore >= 0 ? flightCachedScore : null;
                }

                var hasIdentifierContext = !string.IsNullOrWhiteSpace(wikidataId);
                var score = await TryGetScoreByWikidataIdAsync(mediaType, wikidataId, token, forceRefresh);
                if (score is null && ShouldSearchByTitle(mediaType, year, hasIdentifierContext))
                {
                    score = await SearchAndFetchScoreAsync(
                        mediaType,
                        title.Trim(),
                        normalizedTitle,
                        year,
                        hasIdentifierContext,
                        token);
                }

                _cache.Set(cacheKey, score ?? -1, TimeSpan.FromHours(Math.Clamp(_options.CacheHours, 1, 168)));
                return score;
            },
            cancellationToken);
    }

    private static bool ShouldSearchByTitle(PremiereMediaType mediaType, int? year, bool hasIdentifierContext)
    {
        return mediaType == PremiereMediaType.Series || hasIdentifierContext || year.HasValue;
    }

    private async Task<int?> TryGetScoreByWikidataIdAsync(
        PremiereMediaType mediaType,
        string? wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (_wikimediaClient is null || string.IsNullOrWhiteSpace(wikidataId))
        {
            return null;
        }

        try
        {
            var rottenTomatoesId = await _wikimediaClient.GetRottenTomatoesIdAsync(wikidataId, cancellationToken, forceRefresh);
            if (string.IsNullOrWhiteSpace(rottenTomatoesId) || !IsExpectedMediaPath(mediaType, rottenTomatoesId))
            {
                return null;
            }

            return await FetchDirectScoreAsync(rottenTomatoesId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Skipping Rotten Tomatoes Wikidata lookup for {WikidataId}.", wikidataId);
            return null;
        }
    }

    private async Task<int?> SearchAndFetchScoreAsync(
        PremiereMediaType mediaType,
        string title,
        string normalizedTitle,
        int? year,
        bool hasIdentifierContext,
        CancellationToken cancellationToken)
    {
        var html = await GetStringAsync($"search?search={Uri.EscapeDataString(title)}", cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = SelectBestSearchResult(ParseSearchResults(html), mediaType, normalizedTitle, year);
        if (match is null)
        {
            return null;
        }

        return match.Score
            ?? (ShouldFetchDirectSearchMatch(match, year, hasIdentifierContext)
                ? await FetchDirectScoreAsync(match.Url, cancellationToken)
                : null);
    }

    private static bool ShouldFetchDirectSearchMatch(
        RottenTomatoesSearchResult match,
        int? targetYear,
        bool hasIdentifierContext)
    {
        return hasIdentifierContext
            || (targetYear.HasValue && match.Year.HasValue && YearMatches(match.Year, targetYear));
    }

    private async Task<int?> FetchDirectScoreAsync(string urlOrPath, CancellationToken cancellationToken)
    {
        var html = await GetStringAsync(urlOrPath, cancellationToken);
        return string.IsNullOrWhiteSpace(html) ? null : ParseScorecardScore(html);
    }

    private async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Skipping Rotten Tomatoes request to {Url}.", url);
            return null;
        }
    }

    private static RottenTomatoesSearchResult? SelectBestSearchResult(
        IEnumerable<RottenTomatoesSearchResult> results,
        PremiereMediaType mediaType,
        string normalizedTitle,
        int? year)
    {
        var exactMatches = results
            .Where(result => result.MediaType == mediaType
                && string.Equals(NormalizeTitle(result.Title), normalizedTitle, StringComparison.Ordinal))
            .ToArray();
        if (exactMatches.Length == 0)
        {
            return null;
        }

        var yearMatches = exactMatches
            .Where(result => YearMatches(result.Year, year))
            .OrderBy(result => result.Year.HasValue && year.HasValue
                ? Math.Abs(result.Year.Value - year.Value)
                : int.MaxValue)
            .ThenByDescending(result => result.Score.HasValue)
            .ToArray();
        if (yearMatches.Length > 0)
        {
            return yearMatches[0];
        }

        return exactMatches.Length == 1 && !exactMatches[0].Year.HasValue ? exactMatches[0] : null;
    }

    private static bool YearMatches(int? resultYear, int? targetYear)
    {
        return resultYear is null
            || targetYear is null
            || Math.Abs(resultYear.Value - targetYear.Value) <= 1;
    }

    private static IReadOnlyList<RottenTomatoesSearchResult> ParseSearchResults(string html)
    {
        var results = new List<RottenTomatoesSearchResult>();
        foreach (Match row in SearchRowRegex.Matches(html))
        {
            var rowAttributes = row.Groups["attrs"].Value;
            var body = row.Groups["body"].Value;
            var link = InfoNameLinkRegex.Match(body);
            if (!link.Success)
            {
                continue;
            }

            var linkAttributes = link.Groups["attrs"].Value + link.Groups["attrs2"].Value;
            var url = AttributeValue(linkAttributes, "href");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var mediaType = MediaTypeFromUrl(url);
            if (mediaType is null)
            {
                continue;
            }

            var title = CleanHtmlText(link.Groups["title"].Value);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            results.Add(new RottenTomatoesSearchResult(
                mediaType.Value,
                title,
                url,
                ParseScore(AttributeValue(rowAttributes, "tomatometer-score")
                    ?? AttributeValue(rowAttributes, "tomatometerscore")),
                ParseYear(rowAttributes)));
        }

        return results;
    }

    private static int? ParseScorecardScore(string html)
    {
        var match = ScorecardJsonRegex.Match(html);
        if (!match.Success)
        {
            return null;
        }

        var json = WebUtility.HtmlDecode(match.Groups["json"].Value).Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (TryGetProperty(root, "criticsScore", out var criticsScore))
        {
            var score = ScoreFromJsonObject(criticsScore);
            if (score is not null)
            {
                return score;
            }
        }

        if (TryGetProperty(root, "overlay", out var overlay)
            && TryGetProperty(overlay, "criticsAll", out var criticsAll))
        {
            return ScoreFromJsonObject(criticsAll);
        }

        return null;
    }

    private static int? ScoreFromJsonObject(JsonElement scoreObject)
    {
        return ReadScoreProperty(scoreObject, "score")
            ?? ReadScoreProperty(scoreObject, "scorePercent")
            ?? ScoreFromLikedCounts(scoreObject);
    }

    private static int? ReadScoreProperty(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => ClampPercent(value),
            JsonValueKind.String => ParseScore(property.GetString()),
            _ => null
        };
    }

    private static int? ScoreFromLikedCounts(JsonElement element)
    {
        var liked = ReadIntProperty(element, "likedCount");
        var notLiked = ReadIntProperty(element, "notLikedCount");
        if (liked is null || notLiked is null || liked + notLiked <= 0)
        {
            return null;
        }

        return ClampPercent((int)Math.Round(liked.Value * 100.0 / (liked.Value + notLiked.Value), MidpointRounding.AwayFromZero));
    }

    private static int? ReadIntProperty(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static int? ParseScore(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().TrimEnd('%');
        return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var score)
            ? ClampPercent(score)
            : null;
    }

    private static int? ParseYear(string attributes)
    {
        var value = AttributeValue(attributes, "release-year")
            ?? AttributeValue(attributes, "releaseyear")
            ?? AttributeValue(attributes, "start-year")
            ?? AttributeValue(attributes, "startyear");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    private static int ClampPercent(int value)
    {
        return Math.Clamp(value, 0, 100);
    }

    private static PremiereMediaType? MediaTypeFromUrl(string url)
    {
        var path = PathFromUrlOrId(url);
        if (path.StartsWith("m/", StringComparison.OrdinalIgnoreCase))
        {
            return PremiereMediaType.Movie;
        }

        if (path.StartsWith("tv/", StringComparison.OrdinalIgnoreCase))
        {
            return PremiereMediaType.Series;
        }

        return null;
    }

    private static bool IsExpectedMediaPath(PremiereMediaType mediaType, string urlOrId)
    {
        var path = PathFromUrlOrId(urlOrId);
        return mediaType switch
        {
            PremiereMediaType.Movie => path.StartsWith("m/", StringComparison.OrdinalIgnoreCase),
            PremiereMediaType.Series => path.StartsWith("tv/", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string PathFromUrlOrId(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath.TrimStart('/');
        }

        return value.Trim().TrimStart('/');
    }

    private static string? AttributeValue(string attributes, string name)
    {
        var match = Regex.Match(
            attributes,
            $"(?:^|\\s){Regex.Escape(name)}\\s*=\\s*[\"'](?<value>.*?)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : null;
    }

    private static string CleanHtmlText(string value)
    {
        var withoutTags = TagRegex.Replace(value, " ");
        return WhitespaceRegex.Replace(WebUtility.HtmlDecode(withoutTags), " ").Trim();
    }

    private static string NormalizeTitle(string value)
    {
        var normalized = WebUtility.HtmlDecode(value).ToLowerInvariant();
        normalized = NonAlphaNumericRegex.Replace(normalized, " ");
        return WhitespaceRegex.Replace(normalized, " ").Trim();
    }

    private sealed record RottenTomatoesSearchResult(
        PremiereMediaType MediaType,
        string Title,
        string Url,
        int? Score,
        int? Year);
}
