using System.Text.Json;
using System.Text.Json.Serialization;

namespace PremiereCalendar.Models;

public sealed record TheTvdbLoginResponse
{
    [JsonPropertyName("data")]
    public TheTvdbLoginData? Data { get; init; }
}

public sealed record TheTvdbLoginData
{
    [JsonPropertyName("token")]
    public string? Token { get; init; }
}

public sealed record TheTvdbArtworkResponse
{
    [JsonPropertyName("data")]
    public List<TheTvdbArtwork> Data { get; init; } = [];
}

public sealed record TheTvdbArtwork
{
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; init; }

    [JsonPropertyName("type")]
    public JsonElement? TypeValue { get; init; }

    [JsonIgnore]
    public string? Type => TypeValue?.ValueKind switch
    {
        JsonValueKind.String => TypeValue.Value.GetString(),
        JsonValueKind.Number => TypeValue.Value.GetRawText(),
        _ => null
    };

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }
}
