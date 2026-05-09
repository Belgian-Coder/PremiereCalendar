namespace PremiereCalendar.Services;

public static class CachedImageUrl
{
    public static string Build(string sourceUrl, string? version = null, bool refresh = false, int? width = null)
    {
        var path = $"/cached-image?url={Uri.EscapeDataString(sourceUrl)}";

        if (width is > 0)
        {
            path += $"&w={width.Value}";
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            path += $"&v={Uri.EscapeDataString(version)}";
        }

        if (refresh)
        {
            path += "&refresh=true";
        }

        return path;
    }
}
