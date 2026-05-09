using System.Text.Json.Serialization;

namespace PremiereCalendar.Models;

public enum TvmazeUpdateWindow
{
    Day,
    Week,
    Month
}

public sealed record TvmazeShowUpdate(
    int ShowId,
    DateTimeOffset UpdatedAtUtc);

public sealed record TvmazeShow
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("premiered")]
    public string? Premiered { get; init; }

    [JsonPropertyName("externals")]
    public TvmazeExternals? Externals { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; init; }

    [JsonPropertyName("averageRuntime")]
    public int? AverageRuntime { get; init; }

    [JsonPropertyName("officialSite")]
    public string? OfficialSite { get; init; }

    [JsonPropertyName("image")]
    public TvmazeImage? Image { get; init; }

    [JsonPropertyName("rating")]
    public TvmazeRating? Rating { get; init; }

    [JsonPropertyName("network")]
    public TvmazeChannel? Network { get; init; }

    [JsonPropertyName("webChannel")]
    public TvmazeChannel? WebChannel { get; init; }
}

public sealed record TvmazeSearchResult
{
    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("show")]
    public TvmazeShow? Show { get; init; }
}

public sealed record TvmazeRating
{
    [JsonPropertyName("average")]
    public double? Average { get; init; }
}

public sealed record TvmazeChannel
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record TvmazeImage
{
    [JsonPropertyName("medium")]
    public string? Medium { get; init; }

    [JsonPropertyName("original")]
    public string? Original { get; init; }
}

public sealed record TvmazeExternals
{
    [JsonPropertyName("imdb")]
    public string? Imdb { get; init; }

    [JsonPropertyName("thetvdb")]
    public int? TheTvdb { get; init; }
}

public sealed record TvmazeShowImage
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("resolutions")]
    public TvmazeImageResolutions? Resolutions { get; init; }
}

public sealed record TvmazeImageResolutions
{
    [JsonPropertyName("original")]
    public TvmazeImageResolution? Original { get; init; }

    [JsonPropertyName("medium")]
    public TvmazeImageResolution? Medium { get; init; }
}

public sealed record TvmazeImageResolution
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed record TvmazeScheduleEpisode
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("season")]
    public int? Season { get; init; }

    [JsonPropertyName("number")]
    public int? Number { get; init; }

    [JsonPropertyName("airdate")]
    public string? Airdate { get; init; }

    [JsonPropertyName("show")]
    public TvmazeShow? Show { get; init; }

    [JsonPropertyName("_embedded")]
    public TvmazeEmbedded? Embedded { get; init; }
}

public sealed record TvmazeEmbedded
{
    [JsonPropertyName("show")]
    public TvmazeShow? Show { get; init; }
}

public sealed record TvSeriesEnrichment(
    string? NetworkName,
    string? WebChannelName,
    int? AverageRuntimeMinutes,
    double? TvmazeRating,
    string? OfficialSiteUrl,
    string? TvmazeUrl,
    string? Summary,
    string? ImageUrl);
