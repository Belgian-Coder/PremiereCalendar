using System.Text.Json.Serialization;

namespace PremiereCalendar.Models;

public sealed record TmdbChangeHistory
{
    [JsonPropertyName("changes")]
    public IReadOnlyList<TmdbChangeField>? Changes { get; init; }
}

public sealed record TmdbChangeField
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = "";
    [JsonPropertyName("items")]
    public IReadOnlyList<TmdbChangeEvent> Items { get; init; } = [];
}

public sealed record TmdbChangeEvent
{
    [JsonPropertyName("time")]
    public string Time { get; init; } = "";
}
