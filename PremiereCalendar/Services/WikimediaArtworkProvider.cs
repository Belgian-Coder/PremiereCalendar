namespace PremiereCalendar.Services;

public sealed class WikimediaArtworkProvider : IArtworkProvider
{
    private readonly IWikimediaClient _client;

    public WikimediaArtworkProvider(IWikimediaClient client)
    {
        _client = client;
    }

    public async Task<ArtworkCandidate?> GetArtworkAsync(
        ArtworkRequest request,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(request.WikidataId))
        {
            return null;
        }

        var imageUrl = await _client.GetReusableImageUrlAsync(request.WikidataId, cancellationToken, forceRefresh);
        return string.IsNullOrWhiteSpace(imageUrl)
            ? null
            : new ArtworkCandidate(imageUrl, ArtworkSources.Wikimedia);
    }
}
