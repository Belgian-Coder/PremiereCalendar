using Microsoft.AspNetCore.WebUtilities;

namespace PremiereCalendar.Services;

public static class FilterStorageQueryComposer
{
    private static readonly StringComparer QueryKeyComparer = StringComparer.OrdinalIgnoreCase;

    public static bool HasMeaningfulFilterQuery(string? query)
    {
        foreach (var (key, value) in ToDictionary(query))
        {
            if (IsNavigationOnlyKey(key)
                || IsUnsupportedMediaSpecificKey(key)
                || IsDefaultFilterValue(key, value))
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
        parameters.Remove("day");

        var currentParameters = ToDictionary(currentQuery);
        if (currentParameters.TryGetValue("week", out var week)
            && !string.IsNullOrWhiteSpace(week))
        {
            parameters["week"] = week;
        }

        if (currentParameters.TryGetValue("day", out var day)
            && !string.IsNullOrWhiteSpace(day))
        {
            parameters["day"] = day;
        }

        RemoveUnsupportedMediaSpecificKeys(parameters);
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

        RemoveUnsupportedMediaSpecificKeys(parameters);
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
            .Where(pair => IsMediaSpecificKey(pair.Key, prefix) && !IsUnsupportedMediaSpecificKey(pair.Key)))
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
        return key.Equals("week", StringComparison.OrdinalIgnoreCase)
            || key.Equals("day", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnsupportedMediaSpecificKey(string key)
    {
        return key.Equals("movieScope", StringComparison.OrdinalIgnoreCase)
            || key.Equals("seriesReleaseTypes", StringComparison.OrdinalIgnoreCase)
            || key.Equals("seriesCertifications", StringComparison.OrdinalIgnoreCase)
            || key.Equals("seriesCertificationCountry", StringComparison.OrdinalIgnoreCase)
            || key.Equals("movieStatuses", StringComparison.OrdinalIgnoreCase)
            || key.Equals("movieTypes", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveUnsupportedMediaSpecificKeys(IDictionary<string, string?> parameters)
    {
        foreach (var key in parameters.Keys.Where(IsUnsupportedMediaSpecificKey).ToArray())
        {
            parameters.Remove(key);
        }
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
        var parameters = new Dictionary<string, string?>(QueryKeyComparer);
        foreach (var pair in QueryHelpers.ParseQuery($"?{trimmed}"))
        {
            parameters[pair.Key] = LastNonBlankOrLast(pair.Value);
        }

        return parameters;
    }

    private static string? LastNonBlankOrLast(IReadOnlyList<string?> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        for (var index = values.Count - 1; index >= 0; index--)
        {
            if (!string.IsNullOrWhiteSpace(values[index]))
            {
                return values[index];
            }
        }

        return values[values.Count - 1];
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
