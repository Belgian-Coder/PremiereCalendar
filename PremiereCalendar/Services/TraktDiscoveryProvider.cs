using System.Runtime.CompilerServices;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class TraktDiscoveryProvider : IStreamingPremiereDiscoveryProvider, INamedPremiereDiscoveryProvider
{
    private readonly ITraktClient _client;

    public TraktDiscoveryProvider(ITraktClient client)
    {
        _client = client;
    }

    public string DisplayName => "Trakt";

    public async Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var candidates = new List<ExternalPremiereCandidate>();
        await foreach (var batch in StreamCandidatesAsync(start, end, forceRefresh, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            candidates.AddRange(batch);
        }

        return candidates;
    }

    public async IAsyncEnumerable<IReadOnlyList<ExternalPremiereCandidate>> StreamCandidatesAsync(
        DateOnly start,
        DateOnly end,
        bool forceRefresh = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var movieTask = TryStartMovieCalendarAsync(start, end, cancellationToken, forceRefresh);
        var showTask = TryStartShowCalendarAsync(start, end, cancellationToken, forceRefresh);
        var active = new List<Task<IReadOnlyList<ExternalPremiereCandidate>>>
        {
            MapMovieCalendarAsync(movieTask),
            MapShowCalendarAsync(showTask)
        };

        while (active.Count > 0)
        {
            var completed = await Task.WhenAny(active);
            active.Remove(completed);

            IReadOnlyList<ExternalPremiereCandidate> candidates;
            try
            {
                candidates = await completed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                candidates = [];
            }
            catch (Exception)
            {
                candidates = [];
            }

            if (candidates.Count > 0)
            {
                yield return candidates;
            }
        }
    }

    private Task<IReadOnlyList<TraktMovieCalendarItem>> TryStartMovieCalendarAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        try
        {
            return _client.GetMovieCalendarAsync(start, end, cancellationToken, forceRefresh);
        }
        catch (Exception ex)
        {
            return Task.FromException<IReadOnlyList<TraktMovieCalendarItem>>(ex);
        }
    }

    private Task<IReadOnlyList<TraktShowCalendarItem>> TryStartShowCalendarAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        try
        {
            return _client.GetNewShowCalendarAsync(start, end, cancellationToken, forceRefresh);
        }
        catch (Exception ex)
        {
            return Task.FromException<IReadOnlyList<TraktShowCalendarItem>>(ex);
        }
    }

    private static async Task<IReadOnlyList<ExternalPremiereCandidate>> MapMovieCalendarAsync(
        Task<IReadOnlyList<TraktMovieCalendarItem>> movieTask)
    {
        return (await movieTask)
            .Select(ToMovieCandidate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray();
    }

    private static async Task<IReadOnlyList<ExternalPremiereCandidate>> MapShowCalendarAsync(
        Task<IReadOnlyList<TraktShowCalendarItem>> showTask)
    {
        return (await showTask)
            .Select(ToShowCandidate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray();
    }

    private static ExternalPremiereCandidate? ToMovieCandidate(TraktMovieCalendarItem item)
    {
        if (!TryParseDate(item.Released, out var date))
        {
            return null;
        }

        return new ExternalPremiereCandidate(
            PremiereMediaType.Movie,
            date,
            item.Movie?.Title,
            item.Movie?.Ids?.Tmdb,
            item.Movie?.Ids?.Imdb,
            item.Movie?.Ids?.Tvdb,
            "Trakt",
            ReleaseYear: date.Year);
    }

    private static ExternalPremiereCandidate? ToShowCandidate(TraktShowCalendarItem item)
    {
        if (!TryParseDate(item.FirstAired, out var date))
        {
            return null;
        }

        return new ExternalPremiereCandidate(
            PremiereMediaType.Series,
            date,
            item.Show?.Title,
            item.Show?.Ids?.Tmdb,
            item.Show?.Ids?.Imdb,
            item.Show?.Ids?.Tvdb,
            "Trakt",
            SeriesPremiereDate: date,
            ReleaseYear: date.Year);
    }

    private static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return DateOnly.TryParse(
            value.Length >= 10 ? value[..10] : value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out date);
    }
}
