using Microsoft.AspNetCore.WebUtilities;

namespace PremiereCalendar.Services;

public static class ViewSyncUrlPolicy
{
    private const int MaximumUrlLength = 2048;

    private static readonly HashSet<string> AllowedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/series",
        "/movies"
    };

    public static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaximumUrlLength
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Relative, out var uri))
        {
            return false;
        }

        var questionIndex = trimmed.IndexOf('?', StringComparison.Ordinal);
        var fragmentIndex = trimmed.IndexOf('#', StringComparison.Ordinal);
        if (fragmentIndex >= 0)
        {
            return false;
        }

        var path = questionIndex >= 0 ? trimmed[..questionIndex] : trimmed;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = "/";
        }

        path = NormalizePath(path);
        if (!AllowedPaths.Contains(path))
        {
            return false;
        }

        if (questionIndex < 0)
        {
            normalized = path;
            return true;
        }

        var query = trimmed[(questionIndex + 1)..];
        if (string.IsNullOrWhiteSpace(query))
        {
            normalized = path;
            return true;
        }

        try
        {
            QueryHelpers.ParseQuery($"?{query}");
        }
        catch (ArgumentException)
        {
            return false;
        }

        normalized = $"{path}?{query}";
        return true;
    }

    public static string? RouteKeyFor(string? value)
    {
        if (!TryNormalize(value, out var normalized) || string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var path = normalized.Split('?', 2)[0];
        return path.ToLowerInvariant() switch
        {
            "/series" => "series",
            "/movies" => "movies",
            "/" => "all",
            _ => null
        };
    }

    private static string NormalizePath(string value)
    {
        var path = value.Trim();
        if (!path.StartsWith('/'))
        {
            path = $"/{path}";
        }

        return path.Length > 1 ? path.TrimEnd('/') : path;
    }
}
