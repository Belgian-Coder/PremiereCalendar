using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class TvmazeArtworkProvider : IArtworkProvider
{
    private readonly ITvmazeClient _client;

    public TvmazeArtworkProvider(ITvmazeClient client)
    {
        _client = client;
    }

    public async Task<ArtworkCandidate?> GetArtworkAsync(
        ArtworkRequest request,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (request.MediaType != PremiereMediaType.Series)
        {
            return null;
        }

        var show = await _client.LookupShowAsync(request.TvdbId, request.ImdbId, cancellationToken, forceRefresh);
        if (show is null)
        {
            return null;
        }

        var images = await _client.GetShowImagesAsync(show.Id, cancellationToken, forceRefresh);
        var url = images
            .Where(image => !string.IsNullOrWhiteSpace(ImageUrl(image)))
            .OrderBy(image => ImageTypeRank(image.Type))
            .Select(ImageUrl)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(url)
            ? null
            : new ArtworkCandidate(url, ArtworkSources.TvmazeImage);
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
