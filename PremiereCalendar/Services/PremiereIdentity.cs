using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public static class PremiereIdentity
{
    public static string CanonicalId(PremiereMediaType mediaType, int tmdbId)
    {
        var prefix = mediaType == PremiereMediaType.Series ? "tv" : "movie";
        return $"{prefix}:{tmdbId}";
    }

    public static string SeriesEpisodeCanonicalId(int tmdbId, DateOnly airDate, int? seasonNumber, int? episodeNumber)
    {
        return seasonNumber is > 0 && episodeNumber is > 0
            ? $"tv:{tmdbId}:s{seasonNumber.Value:00}e{episodeNumber.Value:00}"
            : $"tv:{tmdbId}:air:{airDate:yyyyMMdd}";
    }

    public static PremiereItemType ItemType(PremiereMediaType mediaType)
    {
        return mediaType == PremiereMediaType.Series
            ? PremiereItemType.SeriesPremiere
            : PremiereItemType.MovieFirstRelease;
    }
}
