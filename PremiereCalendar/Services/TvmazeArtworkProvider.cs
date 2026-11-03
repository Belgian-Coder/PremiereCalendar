using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class TvmazeArtworkProvider : IArtworkProvider
{
    public string OwnedSource => ArtworkSources.TvmazeImage;
    private readonly ITvmazeClient _client;

    public TvmazeArtworkProvider(ITvmazeClient client)
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
        if (request.MediaType != PremiereMediaType.Series)
        {
            return new(null, true, new("not_applicable"));
        }

        var lookup = await _client.LookupShowWithStatusAsync(request.TvdbId, request.ImdbId, cancellationToken, forceRefresh);
        var show = lookup.Value;
        if (show is null) return new(null, lookup.Successful, lookup.Failure);
        var images = await _client.GetShowImagesWithStatusAsync(show.Id, cancellationToken, forceRefresh);
        var url = images.Value
            .Where(image => !string.IsNullOrWhiteSpace(ImageUrl(image)))
            .OrderBy(image => ImageTypeRank(image.Type))
            .Select(ImageUrl)
            .FirstOrDefault();

        return new(string.IsNullOrWhiteSpace(url) ? null : new ArtworkCandidate(url, ArtworkSources.TvmazeImage), images.Successful, images.Failure);
    }

    private static int ImageTypeRank(string? type)
    {
        if (string.Equals(type, "poster", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            return 1;
        }

        return string.Equals(type, "background", StringComparison.OrdinalIgnoreCase)
            ? 2
            : 3;
    }

    private static string? ImageUrl(TvmazeShowImage image)
    {
        return image.Resolutions?.Original?.Url
            ?? image.Resolutions?.Medium?.Url;
    }
}
