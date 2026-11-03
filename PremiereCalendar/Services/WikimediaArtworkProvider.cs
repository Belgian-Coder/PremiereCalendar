namespace PremiereCalendar.Services;

public sealed class WikimediaArtworkProvider : IArtworkProvider
{
    public string OwnedSource => ArtworkSources.Wikimedia;
    private readonly IWikimediaClient _client;

    public WikimediaArtworkProvider(IWikimediaClient client)
    {
        _client = client;
    }

    public async Task<ArtworkCandidate?> GetArtworkAsync(
        ArtworkRequest request,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
        => (await GetArtworkWithStatusAsync(request, cancellationToken, forceRefresh)).Value;

    public async Task<ProviderRefreshResult<ArtworkCandidate?>> GetArtworkWithStatusAsync(ArtworkRequest request, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(request.WikidataId))
        {
            return new(null, true, new("not_applicable"));
        }

        var result = await _client.GetReusableImageWithStatusAsync(request.WikidataId, cancellationToken, forceRefresh);
        return new(string.IsNullOrWhiteSpace(result.Value) ? null : new ArtworkCandidate(result.Value, ArtworkSources.Wikimedia), result.Successful, result.Failure);
    }
}
