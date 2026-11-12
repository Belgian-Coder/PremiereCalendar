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
        var active = new List<Task<ScheduleCandidateResult>>();
        var failures = new List<Exception>();
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

            var scheduleResult = await completed;
            if (scheduleResult.Error is { } scheduleError)
            {
                // Keep the days that succeeded, but never let a failed day look like a day without premieres.
                failures.Add(scheduleError);
                continue;
            }

            var candidates = scheduleResult.Candidates
                .Where(candidate => seen.Add(CandidateKey(candidate)))
                .ToArray();

            if (candidates.Length == 0)
            {
                continue;
            }

            emitted = true;
            yield return candidates;
        }

        if (failures.Count > 0) throw CombineFailures(failures);

        if (!emitted)
        {
            yield return [];
        }
    }

    // One failure carrying the latest retry deadline: the source is deferred until every throttled request may be retried.
    private static Exception CombineFailures(IReadOnlyList<Exception> failures)
    {
        var deadlines = failures.Select(RetryDeadlineOf).Where(deadline => deadline is not null).Select(deadline => deadline!.Value).ToArray();
        if (deadlines.Length > 0)
        {
            return new ExternalApiException(
                $"{failures.Count} TVmaze schedule request(s) failed.",
                new ProviderRetryAfterException("tvmaze", deadlines.Max()));
        }

        return failures.Count == 1 ? failures[0] : new AggregateException("One or more TVmaze schedule requests failed.", failures);
    }

    private static DateTimeOffset? RetryDeadlineOf(Exception? error)
    {
        if (error is ProviderRetryAfterException throttled) return throttled.RetryAtUtc;
        if (error is ProviderCircuitOpenException circuit) return circuit.RetryAtUtc;
        if (error is ProviderRequestException request && request.Failure.RetryAtUtc is { } retryAt) return retryAt;
        if (error is AggregateException aggregate)
            return aggregate.InnerExceptions.Select(RetryDeadlineOf).Where(value => value is not null).Max();
        return error?.InnerException is { } inner ? RetryDeadlineOf(inner) : null;
    }

    private sealed record ScheduleCandidateResult(IReadOnlyList<ExternalPremiereCandidate> Candidates, Exception? Error);

    private async Task<ScheduleCandidateResult> GetScheduleCandidatesAsync(
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
            return new([], new ExternalApiException("TVmaze schedule request timed out.",
                new ProviderRetryAfterException("tvmaze", DateTimeOffset.UtcNow.AddSeconds(60))));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new([], ex);
        }

        return new(schedule
            .Select(ToCandidate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToArray(), null);
    }

    private static ExternalPremiereCandidate? ToCandidate(TvmazeScheduleEpisode episode)
    {
        var show = episode.Show ?? episode.Embedded?.Show;
        if (show is null || string.IsNullOrWhiteSpace(show.Name))
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
            show.Name,
            null,
            show.Externals?.Imdb,
            show.Externals?.TheTvdb,
            "TVmaze schedule",
            IsSeriesEpisode: true,
            EpisodeTitle: episode.Name,
            SeasonNumber: episode.Season,
            EpisodeNumber: episode.Number,
            OriginalLanguage: NormalizeLanguage(show.Language),
            SeriesPremiereDate: TryParseDateOnly(show.Premiered, out var premiered) ? premiered : null,
            ExternalProviderId: show.Id > 0 ? show.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            ExternalUrl: show.Url,
            PosterUrl: CoalesceText(show.Image?.Original, show.Image?.Medium),
            ReleaseYear: premiered == default ? null : premiered.Year);
    }

    private static string? CoalesceText(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
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
            "FLEMISH" => "nl",
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
        if (candidate.TvdbId is > 0)
        {
            return $"tvdb:{candidate.TvdbId}:{episodeKey}";
        }

        if (!string.IsNullOrWhiteSpace(candidate.ImdbId))
        {
            return $"imdb:{candidate.ImdbId}:{episodeKey}";
        }

        if (!string.IsNullOrWhiteSpace(candidate.ExternalProviderId))
        {
            return $"tvmaze:{candidate.ExternalProviderId}:{episodeKey}";
        }

        return $"title:{candidate.Title}:{episodeKey}";
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
