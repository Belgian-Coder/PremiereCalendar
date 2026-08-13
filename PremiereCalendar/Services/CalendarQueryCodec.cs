using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

/// <summary>Canonical route parsing shared by calendar navigation, persistence, and view sync.</summary>
public static class CalendarQueryCodec
{
    public static CalendarPageMode ResolvePageMode(Uri uri) => uri.AbsolutePath.TrimEnd('/').ToLowerInvariant() switch
    {
        "/series" => CalendarPageMode.Series,
        "/movies" => CalendarPageMode.Movies,
        _ => CalendarPageMode.All
    };

    public static bool HasQuery(Uri uri) => !string.IsNullOrWhiteSpace(uri.Query);

    public static string PathAndQuery(Uri uri) => string.IsNullOrWhiteSpace(uri.Query)
        ? uri.AbsolutePath
        : $"{uri.AbsolutePath}{uri.Query}";
}

/// <summary>Keeps view-sync normalization and route ownership out of the page component.</summary>
public static class CalendarViewSyncNavigationCoordinator
{
    public static string? Normalize(Uri uri)
    {
        var candidate = CalendarQueryCodec.PathAndQuery(uri);
        return ViewSyncUrlPolicy.TryNormalize(candidate, out var normalized) ? normalized : null;
    }

    public static string? RouteKey(Uri uri) => Normalize(uri) is { } relativeUrl
        ? ViewSyncUrlPolicy.RouteKeyFor(relativeUrl)
        : null;
}
