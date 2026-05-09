using Microsoft.AspNetCore.WebUtilities;

namespace PremiereCalendar.Services;

public static class FilterStorageQueryComposer
{
    private static readonly StringComparer QueryKeyComparer = StringComparer.OrdinalIgnoreCase;

    public static bool HasMeaningfulFilterQuery(string? query)
    {
        foreach (var (key, value) in ToDictionary(query))
        {
            if (IsNavigationOnlyKey(key) || IsDefaultFilterValue(key, value))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }

        return false;
    }

    public static string ComposeRestoredQuery(string? currentQuery, string savedQuery)
    {
        var parameters = ToDictionary(savedQuery);
        parameters.Remove("week");

        var currentParameters = ToDictionary(currentQuery);
        if (currentParameters.TryGetValue("week", out var week)
            && !string.IsNullOrWhiteSpace(week))
        {
            parameters["week"] = week;
        }

        return ToQueryString(parameters);
    }

    public static string? ComposeAllQuery(string? allQuery, string? seriesQuery, string? movieQuery)
    {
        if (string.IsNullOrWhiteSpace(allQuery)
            && string.IsNullOrWhiteSpace(seriesQuery)
            && string.IsNullOrWhiteSpace(movieQuery))
        {
            return null;
        }

        var parameters = ToDictionary(FirstNonBlank(allQuery, seriesQuery, movieQuery));

        OverlayMediaParameters(parameters, "series", seriesQuery);
        OverlayMediaParameters(parameters, "movie", movieQuery);

        return ToQueryString(parameters);
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static void OverlayMediaParameters(
        IDictionary<string, string?> parameters,
        string prefix,
        string? sourceQuery)
    {
        if (string.IsNullOrWhiteSpace(sourceQuery))
        {
            return;
        }

        foreach (var key in parameters.Keys.Where(key => IsMediaSpecificKey(key, prefix)).ToArray())
        {
            parameters.Remove(key);
        }

        foreach (var (key, value) in ToDictionary(sourceQuery)
            .Where(pair => IsMediaSpecificKey(pair.Key, prefix)))
        {
            parameters[key] = value;
        }
    }

    private static bool IsMediaSpecificKey(string key, string prefix)
    {
        return key.StartsWith(prefix, StringComparison.Ordinal)
            && key.Length > prefix.Length
            && char.IsUpper(key[prefix.Length]);
    }

    private static bool IsNavigationOnlyKey(string key)
    {
        return key.Equals("week", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefaultFilterValue(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim();
        if (key.Equals("sort", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Equals("date", StringComparison.OrdinalIgnoreCase);
        }

        if (key.Equals("dir", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Equals("asc", StringComparison.OrdinalIgnoreCase);
        }

        if (key.Equals("media", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Equals("all", StringComparison.OrdinalIgnoreCase);
        }

        if (key.Equals("score", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Equals("tmdb", StringComparison.OrdinalIgnoreCase);
        }

        if (key.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return normalized is "1" or "true";
        }

        if (key.Equals("lang", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Equals("both", StringComparison.OrdinalIgnoreCase);
        }

        if (key.Equals("origin", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Equals("all", StringComparison.OrdinalIgnoreCase);
        }

        if (key.EndsWith("Scope", StringComparison.Ordinal)
            && normalized.Equals("episodes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (key is "minVotes" or "runtimeMin"
            || key.EndsWith("RuntimeMin", StringComparison.Ordinal))
        {
            return normalized is "0" or "0.0";
        }

        if (key is "min")
        {
            return normalized is "0" or "0.0";
        }

        if (key is "max")
        {
            return normalized is "10" or "10.0";
        }

        if (key is "runtimeMax"
            || key.EndsWith("RuntimeMax", StringComparison.Ordinal))
        {
            return normalized is "360" or "360.0";
        }

        return false;
    }

    private static Dictionary<string, string?> ToDictionary(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new Dictionary<string, string?>(QueryKeyComparer);
        }

        var trimmed = query.Trim().TrimStart('?');
        return QueryHelpers.ParseQuery($"?{trimmed}")
            .ToDictionary(
                pair => pair.Key,
                pair => (string?)pair.Value.ToString(),
                QueryKeyComparer);
    }

    private static string ToQueryString(IDictionary<string, string?> parameters)
    {
        return string.Join(
            '&',
            parameters
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));
    }
}
