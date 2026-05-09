using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class FanartArtworkProvider : IArtworkProvider
{
    private readonly IFanartClient _client;

    public FanartArtworkProvider(IFanartClient client)
    {
        _client = client;
    }

    public async Task<ArtworkCandidate?> GetArtworkAsync(
        ArtworkRequest request,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (request.MediaType == PremiereMediaType.Movie)
        {
            var movie = await _client.GetMovieArtworkAsync(request.TmdbId, cancellationToken, forceRefresh);
            return SelectBest(movie?.MoviePosters)
                ?? SelectBest(movie?.MovieBackgrounds);
        }

        if (request.TvdbId is not > 0)
        {
            return null;
        }

        var tv = await _client.GetTvArtworkAsync(request.TvdbId.Value, cancellationToken, forceRefresh);
        return SelectBest(tv?.TvPosters)
            ?? SelectBest(tv?.ShowBackgrounds)
            ?? SelectBest(tv?.TvThumbs);
    }

    public static ArtworkCandidate? SelectBest(IEnumerable<FanartImage>? images)
    {
        var url = images?
            .Where(image => !string.IsNullOrWhiteSpace(image.Url))
            .OrderBy(image => LanguageRank(image.Language))
            .ThenByDescending(image => ParseLikes(image.Likes))
            .Select(image => image.Url)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(url)
            ? null
            : new ArtworkCandidate(url, ArtworkSources.Fanart);
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

        return string.IsNullOrWhiteSpace(language)
            || string.Equals(language, "00", StringComparison.OrdinalIgnoreCase)
                ? 2
                : 3;
    }

    private static int ParseLikes(string? likes)
    {
        return int.TryParse(likes, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}
