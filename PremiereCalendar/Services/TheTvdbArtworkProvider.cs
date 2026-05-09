using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class TheTvdbArtworkProvider : IArtworkProvider
{
    private readonly ITheTvdbClient _client;
    private readonly TheTvdbOptions _options;

    public TheTvdbArtworkProvider(ITheTvdbClient client, IOptions<TheTvdbOptions> options)
    {
        _client = client;
        _options = options.Value;
    }

    public async Task<ArtworkCandidate?> GetArtworkAsync(
        ArtworkRequest request,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (request.MediaType != PremiereMediaType.Series || request.TvdbId is not > 0)
        {
            return null;
        }

        var artworks = await _client.GetSeriesArtworkAsync(request.TvdbId.Value, cancellationToken, forceRefresh);
        var url = artworks
            .Where(artwork => IsPosterLike(artwork.Type))
            .Where(artwork => !string.IsNullOrWhiteSpace(ArtworkUrl(artwork)))
            .OrderBy(artwork => LanguageRank(artwork.Language))
            .ThenByDescending(artwork => artwork.Score ?? 0)
            .Select(ArtworkUrl)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(url)
            ? null
            : new ArtworkCandidate(url, ArtworkSources.TheTvdb);
    }

    private string? ArtworkUrl(TheTvdbArtwork artwork)
    {
        var value = artwork.Image ?? artwork.Thumbnail;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            ? absolute.AbsoluteUri
            : $"{_options.ImageBaseUrl.TrimEnd('/')}/{value.TrimStart('/')}";
    }

    private static bool IsPosterLike(string? type)
    {
        return string.IsNullOrWhiteSpace(type)
            || string.Equals(type, "2", StringComparison.OrdinalIgnoreCase)
            || type.Contains("poster", StringComparison.OrdinalIgnoreCase);
    }

    private static int LanguageRank(string? language)
    {
        if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(language, "nl", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return string.IsNullOrWhiteSpace(language) ? 2 : 3;
    }
}
