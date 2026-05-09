using System.Text.Json.Serialization;

namespace PremiereCalendar.Models;

public sealed record TraktMovieCalendarItem
{
    [JsonPropertyName("released")]
    public string? Released { get; init; }

    [JsonPropertyName("movie")]
    public TraktMovie? Movie { get; init; }
}

public sealed record TraktShowCalendarItem
{
    [JsonPropertyName("first_aired")]
    public string? FirstAired { get; init; }

    [JsonPropertyName("episode")]
    public TraktEpisode? Episode { get; init; }

    [JsonPropertyName("show")]
    public TraktShow? Show { get; init; }
}

public sealed record TraktMovie
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("ids")]
    public TraktIds? Ids { get; init; }
}

public sealed record TraktShow
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("ids")]
    public TraktIds? Ids { get; init; }
}

public sealed record TraktEpisode
{
    [JsonPropertyName("season")]
    public int? Season { get; init; }

    [JsonPropertyName("number")]
    public int? Number { get; init; }
}

public sealed record TraktIds
{
    [JsonPropertyName("tmdb")]
    public int? Tmdb { get; init; }

    [JsonPropertyName("imdb")]
    public string? Imdb { get; init; }

    [JsonPropertyName("tvdb")]
    public int? Tvdb { get; init; }
}
