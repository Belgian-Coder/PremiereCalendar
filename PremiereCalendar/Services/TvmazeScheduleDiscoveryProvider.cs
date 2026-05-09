using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class TvmazeScheduleDiscoveryProvider :
    IStreamingPremiereDiscoveryProvider,
    INamedPremiereDiscoveryProvider,
    IMediaScopedPremiereDiscoveryProvider
{
    private readonly ITvmazeClient _client;
    private readonly TvmazeOptions _options;
    private readonly IIntegrationSettingsStore? _settingsStore;

    public TvmazeScheduleDiscoveryProvider(
        ITvmazeClient client,
        IOptions<TvmazeOptions> options,
        IIntegrationSettingsStore? settingsStore = null)
    {
        _client = client;
        _options = options.Value;
        _settingsStore = settingsStore;
    }

    public string DisplayName => "TVmaze schedules";

    public bool SupportsMediaType(PremiereMediaType mediaType)
    {
        return mediaType == PremiereMediaType.Series;
    }

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
        var settings = await GetEffectiveSettingsAsync(cancellationToken);
        if (!settings.Enabled || !settings.EnableScheduleDiscovery)
        {
            yield return [];
            yield break;
        }

        var countries = settings.ScheduleCountries
            .Where(country => !string.IsNullOrWhiteSpace(country))
            .Select(country => country.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var requests = new List<TvmazeScheduleRequest>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            foreach (var country in countries)
            {
                requests.Add(new TvmazeScheduleRequest(date, country, WebSchedule: false));
                requests.Add(new TvmazeScheduleRequest(date, country, WebSchedule: true));
            }

            requests.Add(new TvmazeScheduleRequest(date, "", WebSchedule: true));
        }

        var concurrency = Math.Clamp(_options.ScheduleFetchConcurrency, 1, Math.Max(1, requests.Count));
        var nextRequestIndex = 0;
        var active = new List<Task<IReadOnlyList<ExternalPremiereCandidate>>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = false;

        void StartPending()
        {
            while (active.Count < concurrency && nextRequestIndex < requests.Count)
            {
                var request = requests[nextRequestIndex++];
                active.Add(GetScheduleCandidatesAsync(request, cancellationToken, forceRefresh));
            }
        }

        StartPending();

        while (active.Count > 0)
        {
            var completed = await Task.WhenAny(active);
            active.Remove(completed);
            StartPending();

            var candidates = (await completed)
                .Where(candidate => seen.Add(CandidateKey(candidate)))
                .ToArray();

            if (candidates.Length == 0)
            {
                continue;
            }

            emitted = true;
            yield return candidates;
        }

        if (!emitted)
        {
            yield return [];
        }
    }

    private async Task<IReadOnlyList<ExternalPremiereCandidate>> GetScheduleCandidatesAsync(
        TvmazeScheduleRequest request,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        IReadOnlyList<TvmazeScheduleEpisode> schedule;
        try
        {
            schedule = await _client.GetScheduleAsync(
                request.Date,
                request.Country,
                request.WebSchedule,
                cancellationToken,
                forceRefresh);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }

        return schedule
            .Select(ToCandidate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray();
    }

    private static ExternalPremiereCandidate? ToCandidate(TvmazeScheduleEpisode episode)
    {
        var show = episode.Show ?? episode.Embedded?.Show;
        if (show?.Externals?.TheTvdb is not > 0 && string.IsNullOrWhiteSpace(show?.Externals?.Imdb))
        {
            return null;
        }

        if (!DateOnly.TryParse(
            episode.Airdate,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var airdate))
        {
            return null;
        }

        return new ExternalPremiereCandidate(
            PremiereMediaType.Series,
            airdate,
            show?.Name,
            null,
            show?.Externals?.Imdb,
            show?.Externals?.TheTvdb,
            "TVmaze schedule",
            IsSeriesEpisode: true,
            EpisodeTitle: episode.Name,
            SeasonNumber: episode.Season,
            EpisodeNumber: episode.Number,
            OriginalLanguage: NormalizeLanguage(show?.Language),
            SeriesPremiereDate: TryParseDateOnly(show?.Premiered, out var premiered) ? premiered : null);
    }

    private static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        return language.Trim().ToUpperInvariant() switch
        {
            "ARABIC" => "ar",
            "BULGARIAN" => "bg",
            "CHINESE" => "zh",
            "CZECH" => "cs",
            "DANISH" => "da",
            "DUTCH" => "nl",
            "ENGLISH" => "en",
            "FINNISH" => "fi",
            "FRENCH" => "fr",
            "GERMAN" => "de",
            "GREEK" => "el",
            "HEBREW" => "he",
            "HINDI" => "hi",
            "HUNGARIAN" => "hu",
            "ITALIAN" => "it",
            "JAPANESE" => "ja",
            "KOREAN" => "ko",
            "NORWEGIAN" => "no",
            "POLISH" => "pl",
            "PORTUGUESE" => "pt",
            "ROMANIAN" => "ro",
            "RUSSIAN" => "ru",
            "SPANISH" => "es",
            "SWEDISH" => "sv",
            "TURKISH" => "tr",
            _ => null
        };
    }

    private static bool TryParseDateOnly(string? value, out DateOnly date)
    {
        date = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateOnly.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out date);
    }

    private static string CandidateKey(ExternalPremiereCandidate candidate)
    {
        var episodeKey = $"{candidate.PremiereDate:yyyyMMdd}:{candidate.SeasonNumber}:{candidate.EpisodeNumber}";
        return candidate.TvdbId is > 0
            ? $"tvdb:{candidate.TvdbId}:{episodeKey}"
            : $"imdb:{candidate.ImdbId}:{episodeKey}";
    }

    private async ValueTask<TvmazeSourceSettings> GetEffectiveSettingsAsync(CancellationToken cancellationToken)
    {
        return _settingsStore is null
            ? new TvmazeSourceSettings
            {
                Enabled = _options.Enabled,
                EnableScheduleDiscovery = _options.EnableScheduleDiscovery,
                ScheduleCountries = _options.ScheduleCountries
            }
            : (await _settingsStore.GetAsync(cancellationToken)).Sources.Tvmaze;
    }

    private sealed record TvmazeScheduleRequest(DateOnly Date, string Country, bool WebSchedule);
}
