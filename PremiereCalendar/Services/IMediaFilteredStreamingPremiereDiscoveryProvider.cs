namespace PremiereCalendar.Services;

/// <summary>Lets discovery omit feed types that the requested calendar does not use.</summary>
public interface IMediaFilteredStreamingPremiereDiscoveryProvider : IStreamingPremiereDiscoveryProvider
{
    IAsyncEnumerable<IReadOnlyList<ExternalPremiereCandidate>> StreamCandidatesAsync(
        DateOnly start,
        DateOnly end,
        bool includeSeries,
        bool includeMovies,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}
