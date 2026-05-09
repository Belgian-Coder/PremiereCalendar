using System.Text.Json.Serialization;

namespace PremiereCalendar.Models;

public sealed record WatchmodeSearchResponse
{
    [JsonPropertyName("title_results")]
    public List<WatchmodeTitleSearchResult> TitleResults { get; init; } = [];
}

public sealed record WatchmodeTitleSearchResult
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; init; }

    [JsonPropertyName("tmdb_id")]
    public int? TmdbId { get; init; }

    [JsonPropertyName("tmdb_type")]
    public string? TmdbType { get; init; }
}

public sealed record WatchmodeTitleSource
{
    [JsonPropertyName("source_id")]
    public int SourceId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("region")]
    public string? Region { get; init; }
}

public sealed record WatchmodeReleasesResponse
{
    [JsonPropertyName("releases")]
    public List<WatchmodeRelease> Releases { get; init; } = [];
}

public sealed record WatchmodeRelease
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("tmdb_id")]
    public int? TmdbId { get; init; }

    [JsonPropertyName("tmdb_type")]
    public string? TmdbType { get; init; }

    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; init; }

    [JsonPropertyName("season_number")]
    public int? SeasonNumber { get; init; }

    [JsonPropertyName("source_release_date")]
    public string? SourceReleaseDate { get; init; }

    [JsonPropertyName("source_id")]
    public int? SourceId { get; init; }

    [JsonPropertyName("source_name")]
    public string? SourceName { get; init; }
}
