namespace PremiereCalendar.Services;

/// <summary>
/// Decoded response-size limits for provider calls. Automatic decompression removes Content-Length and the
/// Content-Encoding header, so a few kilobytes of gzip or Brotli can expand into gigabytes; the limit is therefore
/// applied to the decoded bytes, both for buffered reads (HttpClient.MaxResponseContentBufferSize) and for streamed
/// reads (the provider content wrapper).
/// </summary>
internal static class ProviderResponseLimits
{
    /// <summary>
    /// Default decoded cap. The largest known provider bodies are Wikidata claim batches (about 1.2 MB decoded),
    /// TVmaze and Trakt multi-day schedules and Rotten Tomatoes pages (hundreds of KB to a few MB), so 16 MiB leaves
    /// ample headroom while stopping decompression bombs.
    /// </summary>
    public const long DefaultMaxBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Higher decoded cap for the few requests that legitimately return a whole user library. SIMKL's full library
    /// pulls (<c>sync/shows</c>, <c>sync/movies</c>, <c>sync/anime</c>) carry every watched season and episode of every
    /// title, which can pass 16 MiB for a large library, so those requests raise their own limit through
    /// <see cref="ProviderRequestOptions.MaxResponseBytes"/>.
    /// </summary>
    public const long LibraryPullMaxBytes = 64L * 1024 * 1024;

    public const long Unlimited = long.MaxValue;
}

internal sealed class ProviderResponseTooLargeException(string provider, long limit)
    : HttpRequestException($"The {provider} response exceeded the decoded size limit of {limit} bytes.");

/// <summary>
/// Tells the adaptive provider handler which decoded size limit applies to a request: the client default, unless the
/// request already carries its own limit (see <see cref="ProviderResponseLimits.LibraryPullMaxBytes"/>).
/// </summary>
internal sealed class ProviderResponseLimitHandler(long maxBytes) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(ProviderRequestOptions.MaxResponseBytes, out _))
        {
            request.Options.Set(ProviderRequestOptions.MaxResponseBytes, maxBytes);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
