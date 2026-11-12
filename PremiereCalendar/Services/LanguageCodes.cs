namespace PremiereCalendar.Services;

/// <summary>
/// Normalises provider language values to the lower-case primary language code used by the language filters.
/// TMDb has no separate Flemish code, so Dutch and Flemish variants all map to <c>nl</c>.
/// </summary>
public static class LanguageCodes
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var lowered = value.Trim().ToLowerInvariant();
        var separator = lowered.IndexOfAny(['-', '_']);
        var primary = separator >= 0 ? lowered[..separator] : lowered;
        if (primary.Length == 0)
        {
            return null;
        }

        return primary switch
        {
            "vls" or "nld" or "dut" or "flemish" or "vlaams" or "dutch" or "nederlands" => "nl",
            _ => primary
        };
    }
}
