using System.Text.Json;
using System.Text.Json.Serialization;

namespace PremiereCalendar.Models;

public sealed record FanartMovieArtwork
{
    [JsonPropertyName("movieposter")]
    public List<FanartImage> MoviePosters { get; init; } = [];

    [JsonPropertyName("moviebackground")]
    public List<FanartImage> MovieBackgrounds { get; init; } = [];
}

public sealed record FanartTvArtwork
{
    [JsonPropertyName("tvposter")]
    public List<FanartImage> TvPosters { get; init; } = [];

    [JsonPropertyName("showbackground")]
    public List<FanartImage> ShowBackgrounds { get; init; } = [];

    [JsonPropertyName("tvthumb")]
    public List<FanartImage> TvThumbs { get; init; } = [];
}

public sealed record FanartImage
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("lang")]
    public string? Language { get; init; }

    [JsonPropertyName("likes")]
    public JsonElement? LikesValue { get; init; }

    [JsonIgnore]
    public string? Likes => LikesValue?.ValueKind switch
    {
        JsonValueKind.String => LikesValue.Value.GetString(),
        JsonValueKind.Number => LikesValue.Value.GetRawText(),
        _ => null
    };
}
