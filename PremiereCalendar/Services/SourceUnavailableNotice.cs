using System.Text.Json.Serialization;

namespace PremiereCalendar.Services;

/// <summary>Persisted incomplete discovery coverage, without provider text or credentials.</summary>
public sealed record SourceUnavailableNotice(string Provider, string Reason, int StatusCode, DateTimeOffset RetryAtUtc)
{
    // Authentication failures in optional discovery must not turn into an endless
    // durable retry. Other failures still use the normal partial/retry contract.
    [JsonIgnore]
    public bool IsOptionalAccessDenial => Provider is "trakt" or "simkl" or "watchmode"
        && Reason == "authentication_denied" && StatusCode is 401 or 403;

    [JsonIgnore]
    public string DisplayName => Provider switch { "trakt" => "Trakt", "simkl" => "Simkl", "watchmode" => "Watchmode", _ => "Source" };
}
