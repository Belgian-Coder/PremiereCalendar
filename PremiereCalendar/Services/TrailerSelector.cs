using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class TrailerSelector
{
    public string? SelectBestYouTubeTrailer(IEnumerable<TmdbVideo>? videos)
    {
        var match = videos?
            .Where(IsUsableYouTubeVideo)
            .OrderBy(VideoTypeRank)
            .ThenBy(video => video.Official ? 0 : 1)
            .ThenByDescending(video => video.PublishedAt)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(match?.Key)
            ? null
            : $"https://www.youtube.com/watch?v={Uri.EscapeDataString(match.Key)}";
    }

    private static bool IsUsableYouTubeVideo(TmdbVideo video)
    {
        return string.Equals(video.Site, "YouTube", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(video.Key)
            && (string.Equals(video.Type, "Trailer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(video.Type, "Teaser", StringComparison.OrdinalIgnoreCase));
    }

    private static int VideoTypeRank(TmdbVideo video)
    {
        if (string.Equals(video.Type, "Trailer", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(video.Type, "Teaser", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }
}
