using System.Text.Json.Serialization;

namespace PremiereCalendar.Models;

public sealed record OmdbItem
{
    [JsonPropertyName("Response")]
    public string? Response { get; init; }

    [JsonPropertyName("Error")]
    public string? Error { get; init; }

    [JsonPropertyName("imdbRating")]
    public string? ImdbRating { get; init; }

    [JsonPropertyName("imdbVotes")]
    public string? ImdbVotes { get; init; }

    [JsonPropertyName("Metascore")]
    public string? Metascore { get; init; }

    [JsonPropertyName("Plot")]
    public string? Plot { get; init; }

    [JsonPropertyName("Ratings")]
    public List<OmdbRating> Ratings { get; init; } = [];

    [JsonPropertyName("Poster")]
    public string? Poster { get; init; }
}

public sealed record OmdbRating
{
    [JsonPropertyName("Source")]
    public string? Source { get; init; }

    [JsonPropertyName("Value")]
    public string? Value { get; init; }
}

public sealed record ExternalRatings(
    double? ImdbScore,
    int? RottenTomatoesScore,
    string? PosterUrl = null,
    int? ImdbVoteCount = null,
    int? MetacriticScore = null,
    string? Plot = null,
    int? RottenTomatoesAudienceScore = null);
