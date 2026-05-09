namespace PremiereCalendar.Services;

public static class ImageSourceUrlPolicy
{
    public static bool TryCreateAllowedUri(
        string sourceUrl,
        IEnumerable<string> allowedHosts,
        out Uri uri)
    {
        uri = default!;

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var parsedUri)
            || parsedUri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(parsedUri.Host)
            || !IsAllowedHost(parsedUri.Host, allowedHosts))
        {
            return false;
        }

        uri = parsedUri;
        return true;
    }

    public static bool IsAllowedHost(string host, IEnumerable<string> allowedHosts)
    {
        return allowedHosts.Any(allowedHost =>
        {
            if (string.IsNullOrWhiteSpace(allowedHost))
            {
                return false;
            }

            var normalizedAllowedHost = allowedHost.Trim().ToLowerInvariant();
            var normalizedHost = host.ToLowerInvariant();

            return normalizedAllowedHost.StartsWith(".", StringComparison.Ordinal)
                ? normalizedHost.EndsWith(normalizedAllowedHost, StringComparison.Ordinal)
                : string.Equals(normalizedHost, normalizedAllowedHost, StringComparison.Ordinal);
        });
    }
}
