using System.Runtime.CompilerServices;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class WatchmodeReleaseDiscoveryProvider : IStreamingPremiereDiscoveryProvider, INamedPremiereDiscoveryProvider
{
    private readonly IWatchmodeClient _client;

    public WatchmodeReleaseDiscoveryProvider(IWatchmodeClient client)
    {
        _client = client;
    }

    public string DisplayName => "Watchmode releases";

    public async Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        return await _client.GetReleaseCandidatesAsync(start, end, cancellationToken, forceRefresh);
    }

    public async IAsyncEnumerable<IReadOnlyList<ExternalPremiereCandidate>> StreamCandidatesAsync(
        DateOnly start,
        DateOnly end,
        bool forceRefresh = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return await GetCandidatesAsync(start, end, cancellationToken, forceRefresh);
    }
}
