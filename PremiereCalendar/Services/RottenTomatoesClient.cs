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
    private readonly ProviderResponseCache? _responseCache;

    public RottenTomatoesClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<RottenTomatoesOptions> options,
        ILogger<RottenTomatoesClient> logger,
        IWikimediaClient? wikimediaClient = null,
        ISingleFlightCoordinator? singleFlight = null,
        ProviderResponseCache? responseCache = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
        _wikimediaClient = wikimediaClient;
        _singleFlight = singleFlight ?? new SingleFlightCoordinator();
        _responseCache = responseCache;
    }

    public bool TryGetCachedScores(
        PremiereMediaType mediaType,
        string title,
        int? year,
        string? wikidataId,
        out RottenTomatoesScores scores)
    {
        scores = RottenTomatoesScores.Empty;
        if (!_options.Enabled || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var normalizedTitle = NormalizeTitle(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return false;
        }

        var cacheKey = ScoreCacheKey(mediaType, normalizedTitle, year, wikidataId);
        return TryGetVerifiedMemory(cacheKey, out scores!);
    }

    public async Task<RottenTomatoesScores> GetScoresAsync(
        PremiereMediaType mediaType,
        string title,
        int? year,
        string? wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(title)) return RottenTomatoesScores.Empty;
        var key = $"rottentomatoes:{_options.BaseUrl}:{ScoreCacheKey(mediaType, NormalizeTitle(title), year, wikidataId)}";
        if (ProviderRefreshScope.Current is { } refresh)
        {
            var cacheKey = ScoreCacheKey(mediaType, NormalizeTitle(title), year, wikidataId);
            return await refresh.RunAsync(key, forceRefresh,
                token => GetScoresCoreAsync(mediaType, title, year, wikidataId, token, forceRefresh),
                cancellationToken,
                value => TryGetVerifiedMemory(cacheKey, out var latest)
                    && ReferenceEquals(latest, value));
        }
        return (await GetScoresCoreAsync(mediaType, title, year, wikidataId, cancellationToken, forceRefresh)).Value;
    }

    public async Task<RottenTomatoesScores?> TryGetCachedScoresAsync(
        PremiereMediaType mediaType,
        string title,
        int? year,
        string? wikidataId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetCachedScores(mediaType, title, year, wikidataId, out var cached)) return cached;
        if (!_options.Enabled || _responseCache is null || string.IsNullOrWhiteSpace(title)) return null;
        var normalizedTitle = NormalizeTitle(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle)) return null;
        var cacheKey = ScoreCacheKey(mediaType, normalizedTitle, year, wikidataId);
        var requestKey = $"scores-v1:{_options.BaseUrl}:{cacheKey}";
        using var publication = _responseCache.BeginOperation("rottentomatoes", requestKey, forceRefresh: false);
        var persisted = await _responseCache.GetAsync<RottenTomatoesScores>(
            "rottentomatoes", requestKey, TimeSpan.FromHours(Math.Clamp(_options.CacheHours, 1, 168)), cancellationToken);
        if (persisted is null) return null;
        return await publication.PublishAsync(current =>
        {
            if (TryGetVerifiedMemory(cacheKey, out var latest))
                return Task.FromResult(latest);
            if (current)
            {
                _cache.Set(cacheKey, new CachedScores(persisted.Value, true), persisted.ExpiresAtUtc);
            }
            return Task.FromResult(persisted.Value);
        }, cancellationToken);
    }

    private async Task<ProviderRefreshResult<RottenTomatoesScores>> GetScoresCoreAsync(
        PremiereMediaType mediaType,
        string title,
        int? year,
        string? wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled || string.IsNullOrWhiteSpace(title))
        {
            return new(RottenTomatoesScores.Empty, false);
        }

        var normalizedTitle = NormalizeTitle(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return new(RottenTomatoesScores.Empty, false);
        }

        var cacheKey = ScoreCacheKey(mediaType, normalizedTitle, year, wikidataId);
        var lifetime = TimeSpan.FromHours(Math.Clamp(_options.CacheHours, 1, 168));
        var persistentRequestKey = $"scores-v1:{_options.BaseUrl}:{cacheKey}";
        if (!forceRefresh && TryGetMemory(cacheKey, out var cachedScores, out var cacheVerified)
            && (ProviderRefreshScope.Current is null || cacheVerified))
        {
            return new(cachedScores, cacheVerified);
        }

        return await _singleFlight.RunAsync(
            forceRefresh ? $"refresh:{cacheKey}" : $"cache:{cacheKey}",
            async token =>
            {
                if (!forceRefresh && TryGetMemory(cacheKey, out var flightCachedScores, out var flightVerified)
                    && (ProviderRefreshScope.Current is null || flightVerified))
                {
                    return new ProviderRefreshResult<RottenTomatoesScores>(flightCachedScores, flightVerified);
                }

                using var publication = _responseCache?.BeginOperation("rottentomatoes", persistentRequestKey, forceRefresh);
                if (!forceRefresh && _responseCache is not null)
                {
                    var persisted = await _responseCache.GetAsync<RottenTomatoesScores>("rottentomatoes", persistentRequestKey, lifetime, token);
                    if (persisted is not null)
                    {
                        return await publication!.PublishAsync(current =>
                        {
                            if (TryGetVerifiedMemory(cacheKey, out var latest))
                                return Task.FromResult(new ProviderRefreshResult<RottenTomatoesScores>(latest, true));
                            if (current)
                            {
                                _cache.Set(cacheKey, new CachedScores(persisted.Value, true), persisted.ExpiresAtUtc);
                            }
                            return Task.FromResult(new ProviderRefreshResult<RottenTomatoesScores>(persisted.Value, current));
                        }, token);
                    }
                }

                var fetchOutcome = new FetchOutcome();
                var hasIdentifierContext = !string.IsNullOrWhiteSpace(wikidataId);
                var scores = await TryGetScoresByWikidataIdAsync(mediaType, wikidataId, token, forceRefresh, fetchOutcome);
                if (!scores.HasAnyScore && ShouldSearchByTitle(mediaType, year, hasIdentifierContext))
                {
                    scores = await SearchAndFetchScoresAsync(
                        mediaType,
                        title.Trim(),
                        normalizedTitle,
                        year,
                        hasIdentifierContext,
                        token,
                        fetchOutcome);
                }

                if (forceRefresh && !fetchOutcome.Successful) return new ProviderRefreshResult<RottenTomatoesScores>(scores, false);

                if (publication is not null)
                {
                    return await publication.PublishAsync(async current =>
                    {
                        if (current)
                        {
                            _cache.Set(cacheKey, new CachedScores(scores, fetchOutcome.Successful), lifetime);
                            if (fetchOutcome.Successful) await _responseCache!.SaveAsync("rottentomatoes", persistentRequestKey, scores, token, invalidateOversized: forceRefresh);
                        }
                        else if (!forceRefresh && TryGetMemory(cacheKey, out var latest, out var latestVerified))
                            return new ProviderRefreshResult<RottenTomatoesScores>(latest, latestVerified);
                        return new ProviderRefreshResult<RottenTomatoesScores>(scores, current && fetchOutcome.Successful);
                    }, token, successfulForceCommit: fetchOutcome.Successful);
                }

                _cache.Set(cacheKey, new CachedScores(scores, fetchOutcome.Successful), lifetime);
                return new ProviderRefreshResult<RottenTomatoesScores>(scores, fetchOutcome.Successful);
            },
            cancellationToken);
    }

    private bool TryGetMemory(string cacheKey, out RottenTomatoesScores scores, out bool verified)
    {
        if (_cache.TryGetValue(cacheKey, out object? value))
        {
            if (value is CachedScores entry)
            {
                scores = entry.Scores;
                verified = entry.Verified;
                return true;
            }
            // Compatibility with scores supplied by older in-process consumers.
            if (value is RottenTomatoesScores legacy)
            {
                scores = legacy;
                verified = false;
                return true;
            }
        }
        scores = RottenTomatoesScores.Empty;
        verified = false;
        return false;
    }

    private bool TryGetVerifiedMemory(string cacheKey, out RottenTomatoesScores scores)
        => TryGetMemory(cacheKey, out scores, out var verified) && verified;

    private sealed record CachedScores(RottenTomatoesScores Scores, bool Verified);

    private static string ScoreCacheKey(
        PremiereMediaType mediaType,
        string normalizedTitle,
        int? year,
        string? wikidataId)
    {
        return $"rt:score:{mediaType}:{normalizedTitle}:{year?.ToString(CultureInfo.InvariantCulture) ?? ""}:{wikidataId?.Trim() ?? ""}";
    }

    public async Task<int?> GetTomatometerScoreAsync(
        PremiereMediaType mediaType,
        string title,
        int? year,
        string? wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var scores = await GetScoresAsync(mediaType, title, year, wikidataId, cancellationToken, forceRefresh);
        return scores.CriticScore;
    }

    private static bool ShouldSearchByTitle(PremiereMediaType mediaType, int? year, bool hasIdentifierContext)
    {
        return mediaType == PremiereMediaType.Series || hasIdentifierContext || year.HasValue;
    }

    private async Task<RottenTomatoesScores> TryGetScoresByWikidataIdAsync(
        PremiereMediaType mediaType,
        string? wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh,
        FetchOutcome fetchOutcome)
    {
        if (_wikimediaClient is null || string.IsNullOrWhiteSpace(wikidataId))
        {
            return RottenTomatoesScores.Empty;
        }

        try
        {
            var identifier = await _wikimediaClient.GetRottenTomatoesIdResultAsync(wikidataId, cancellationToken, forceRefresh);
            var rottenTomatoesId = identifier.Value;
            if (!identifier.IsSuccess) fetchOutcome.Successful = false;
            if (string.IsNullOrWhiteSpace(rottenTomatoesId) || !IsExpectedMediaPath(mediaType, rottenTomatoesId))
            {
                return RottenTomatoesScores.Empty;
            }

            return await FetchDirectScoresAsync(rottenTomatoesId, fallbackCriticScore: null, cancellationToken, fetchOutcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            fetchOutcome.Successful = false;
            _logger.LogWarning(ex, "Skipping Rotten Tomatoes Wikidata lookup for {WikidataId}.", wikidataId);
            return RottenTomatoesScores.Empty;
        }
    }

    private async Task<RottenTomatoesScores> SearchAndFetchScoresAsync(
        PremiereMediaType mediaType,
        string title,
        string normalizedTitle,
        int? year,
        bool hasIdentifierContext,
        CancellationToken cancellationToken,
        FetchOutcome fetchOutcome)
    {
        var html = await GetStringAsync($"search?search={Uri.EscapeDataString(title)}", cancellationToken, fetchOutcome);
        if (string.IsNullOrWhiteSpace(html))
        {
            return RottenTomatoesScores.Empty;
        }

        var match = SelectBestSearchResult(ParseSearchResults(html), mediaType, normalizedTitle, year);
        if (match is null)
        {
            return RottenTomatoesScores.Empty;
        }

        return ShouldFetchDirectSearchMatch(match, year, hasIdentifierContext)
            ? await FetchDirectScoresAsync(match.Url, match.Score, cancellationToken, fetchOutcome)
            : new RottenTomatoesScores(match.Score, AudienceScore: null);
    }

    private static bool ShouldFetchDirectSearchMatch(
        RottenTomatoesSearchResult match,
        int? targetYear,
        bool hasIdentifierContext)
    {
        return match.Score.HasValue
            || hasIdentifierContext
            || (targetYear.HasValue && match.Year.HasValue && YearMatches(match.Year, targetYear));
    }

    private async Task<RottenTomatoesScores> FetchDirectScoresAsync(
        string urlOrPath,
        int? fallbackCriticScore,
        CancellationToken cancellationToken,
        FetchOutcome fetchOutcome)
    {
        var html = await GetStringAsync(urlOrPath, cancellationToken, fetchOutcome);
        if (string.IsNullOrWhiteSpace(html))
        {
            return new RottenTomatoesScores(fallbackCriticScore, AudienceScore: null);
        }

        var scores = ParseScorecardScores(html);
        return scores with
        {
            CriticScore = scores.CriticScore ?? fallbackCriticScore
        };
    }

    private async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken, FetchOutcome fetchOutcome)
    {
        try
        {
            using var response = await ProviderHttpRetry.SendAsync(
                _httpClient,
                () => new HttpRequestMessage(HttpMethod.Get, url),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                fetchOutcome.Successful = false;
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            fetchOutcome.Successful = false;
            _logger.LogWarning(ex, "Skipping Rotten Tomatoes request to {Url}.", url);
            return null;
        }
    }

    private sealed class FetchOutcome
    {
        public bool Successful { get; set; } = true;
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

    private static RottenTomatoesScores ParseScorecardScores(string html)
    {
        var match = ScorecardJsonRegex.Match(html);
        if (!match.Success)
        {
            return RottenTomatoesScores.Empty;
        }

        var json = WebUtility.HtmlDecode(match.Groups["json"].Value).Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            return RottenTomatoesScores.Empty;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        int? criticScore = null;
        int? audienceScore = null;
        if (TryGetProperty(root, "criticsScore", out var criticsScore))
        {
            criticScore = ScoreFromJsonObject(criticsScore);
        }

        if (TryGetProperty(root, "audienceScore", out var rootAudienceScore))
        {
            audienceScore = ScoreFromJsonObject(rootAudienceScore);
        }

        if (TryGetProperty(root, "overlay", out var overlay)
            && TryGetProperty(overlay, "criticsAll", out var criticsAll))
        {
            criticScore ??= ScoreFromJsonObject(criticsAll);
        }

        if (overlay.ValueKind == JsonValueKind.Object
            && TryGetProperty(overlay, "audienceAll", out var audienceAll))
        {
            audienceScore ??= ScoreFromJsonObject(audienceAll);
        }

        return new RottenTomatoesScores(criticScore, audienceScore);
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
