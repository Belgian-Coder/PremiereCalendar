using System.Globalization;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class SimklCalendarDiscoveryProvider : IStreamingPremiereDiscoveryProvider, INamedPremiereDiscoveryProvider
{
    private readonly ISimklClient _client;

    public SimklCalendarDiscoveryProvider(ISimklClient client)
    {
        _client = client;
    }

    public string DisplayName => "Simkl";

    public async Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var items = await _client.GetCalendarAsync(start, end, cancellationToken, forceRefresh);
        return items
            .Select(ToCandidate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray();
    }

    public async IAsyncEnumerable<IReadOnlyList<ExternalPremiereCandidate>> StreamCandidatesAsync(
        DateOnly start,
        DateOnly end,
        bool forceRefresh = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var candidates = await GetCandidatesAsync(start, end, cancellationToken, forceRefresh);
        if (candidates.Count > 0)
        {
            yield return candidates;
        }
    }

    private static ExternalPremiereCandidate? ToCandidate(SimklCalendarItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Title))
        {
            return null;
        }

        var premiereDate = DateOnly.FromDateTime(item.Date.DateTime);
        var tmdbId = TryParseInt(item.Ids.Tmdb);
        var tvdbId = TryParseInt(item.Ids.Tvdb);
        var providerId = item.Ids.SimklId?.ToString(CultureInfo.InvariantCulture);
        var imdbScore = item.Ratings?.Imdb?.Rating;
        var imdbVoteCount = item.Ratings?.Imdb?.Votes;

        return item.Type == SimklCalendarItemType.MovieRelease
            ? new ExternalPremiereCandidate(
                PremiereMediaType.Movie,
                premiereDate,
                item.Title,
                tmdbId,
                item.Ids.Imdb,
                null,
                "Simkl",
                ExternalProviderId: providerId,
                ExternalUrl: item.Url,
                ReleaseYear: item.ReleaseDate?.Year ?? premiereDate.Year,
                ImdbScore: imdbScore,
                ImdbVoteCount: imdbVoteCount)
            : new ExternalPremiereCandidate(
                PremiereMediaType.Series,
                premiereDate,
                item.Title,
                tmdbId,
                item.Ids.Imdb,
                tvdbId,
                "Simkl",
                IsSeriesEpisode: item.Episode is not null,
                SeasonNumber: item.Episode?.Season,
                EpisodeNumber: item.Episode?.Episode,
                SeriesPremiereDate: item.ReleaseDate,
                ExternalProviderId: providerId,
                ExternalUrl: item.Episode?.Url ?? item.Url,
                ReleaseYear: item.ReleaseDate?.Year ?? premiereDate.Year,
                ImdbScore: imdbScore,
                ImdbVoteCount: imdbVoteCount);
    }

    private static int? TryParseInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }
}
