using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed record PremiereDiscoveryCriteria
{
    private const string DefaultGlobalFilterKey = "global:default";

    public bool IncludeSeries { get; init; } = true;
    public bool IncludeMovies { get; init; } = true;
    public PremiereSortMode SortMode { get; init; } = PremiereSortMode.PremiereDate;
    public SortDirection SortDirection { get; init; } = SortDirection.Ascending;
    public ScoreSource ScoreSource { get; init; } = ScoreSource.Tmdb;
    public double MinScore { get; init; }
    public double MaxScore { get; init; } = 10;
    public bool IncludeUnknownScores { get; init; } = true;
    public int MinVoteCount { get; init; }
    public MediaDiscoveryCriteria Series { get; init; } = new();
    public MediaDiscoveryCriteria Movies { get; init; } = new();
    public string GlobalFilterKey { get; init; } = DefaultGlobalFilterKey;

    public static PremiereDiscoveryCriteria None { get; } = new();

    public string CacheKey()
    {
        var minScore = MinScore.ToString("0.0", CultureInfo.InvariantCulture);
        var maxScore = MaxScore.ToString("0.0", CultureInfo.InvariantCulture);
        var normalized = string.Join(
            ';',
            IncludeSeries ? "series" : "no-series",
            IncludeMovies ? "movies" : "no-movies",
            $"score:{ScoreSource}:{minScore}:{maxScore}:{IncludeUnknownScores}",
            MinVoteCount > 0 ? $"votes:{MinVoteCount}" : "votes:any",
            GlobalFilterKey,
            IncludeSeries ? Series.ServerKey(PremiereMediaType.Series) : "series:off",
            IncludeMovies ? Movies.ServerKey(PremiereMediaType.Movie) : "movies:off");

        if (normalized == None.CacheKeyUnhashed())
        {
            return "default";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    public static PremiereDiscoveryCriteria FromFilters(CalendarFilters? filters)
    {
        if (filters is null)
        {
            return None;
        }

        return new PremiereDiscoveryCriteria
        {
            IncludeSeries = filters.ShowSeries,
            IncludeMovies = filters.ShowMovies,
            SortMode = filters.SortMode,
            SortDirection = filters.SortDirection,
            ScoreSource = filters.ScoreSource,
            MinScore = filters.MinScore,
            MaxScore = filters.MaxScore,
            IncludeUnknownScores = filters.IncludeUnknownScores,
            MinVoteCount = filters.MinVoteCount,
            GlobalFilterKey = CreateGlobalFilterKey(filters),
            Series = MediaDiscoveryCriteria.FromFilters(filters.SeriesFilters),
            Movies = MediaDiscoveryCriteria.FromFilters(filters.MovieFilters)
        };
    }

    public TmdbDiscoverFilters ToTmdbFilters(
        PremiereMediaType mediaType,
        IReadOnlyList<int> keywordIds,
        string? originalLanguageOverride = null)
    {
        var mediaFilters = mediaType == PremiereMediaType.Series ? Series : Movies;
        var minScore = Math.Min(MinScore, MaxScore);
        var maxScore = Math.Max(MinScore, MaxScore);
        var originalLanguage = originalLanguageOverride
            ?? (mediaFilters.OriginalLanguages.Length == 1 ? mediaFilters.OriginalLanguages[0] : "");

        return new TmdbDiscoverFilters
        {
            SortBy = TmdbDateSortBy(mediaType),
            OriginalLanguage = originalLanguage ?? "",
            OriginCountries = mediaFilters.OriginCountries,
            GenreIds = mediaFilters.GenreIds,
            WatchRegion = mediaFilters.WatchRegion,
            WatchProviderIds = mediaFilters.WatchProviderIds,
            WatchMonetizationTypes = mediaFilters.MonetizationTypes,
            MinVoteAverage = ScoreSource == ScoreSource.Tmdb && minScore > 0 ? minScore : null,
            MaxVoteAverage = ScoreSource == ScoreSource.Tmdb && maxScore < 10 ? maxScore : null,
            MinVoteCount = MinVoteCount > 0 ? MinVoteCount : null,
            RuntimeMinMinutes = mediaFilters.RuntimeMinMinutes > 0 ? mediaFilters.RuntimeMinMinutes : null,
            RuntimeMaxMinutes = mediaFilters.RuntimeMaxMinutes < 360 ? mediaFilters.RuntimeMaxMinutes : null,
            KeywordIds = keywordIds,
            UseEpisodeAirDate = mediaType == PremiereMediaType.Series
                && mediaFilters.SeriesDateMode == SeriesDateMode.AllEpisodes,
            NetworkIds = mediaType == PremiereMediaType.Series ? mediaFilters.NetworkIds : [],
            TvStatusIds = mediaType == PremiereMediaType.Series ? mediaFilters.TvStatusIds : [],
            TvTypeIds = mediaType == PremiereMediaType.Series ? mediaFilters.TvTypeIds : [],
            MovieReleaseTypes = mediaType == PremiereMediaType.Movie ? mediaFilters.MovieReleaseTypes : [],
            MovieCertificationCountry = mediaType == PremiereMediaType.Movie ? mediaFilters.MovieCertificationCountry : "",
            MovieCertifications = mediaType == PremiereMediaType.Movie ? mediaFilters.MovieCertifications : []
        };
    }

    private string CacheKeyUnhashed()
    {
        return string.Join(
            ';',
            "series",
            "movies",
            "score:Tmdb:0.0:10.0:True",
            "votes:any",
            DefaultGlobalFilterKey,
            Series.ServerKey(PremiereMediaType.Series),
            Movies.ServerKey(PremiereMediaType.Movie));
    }

    private static string CreateGlobalFilterKey(CalendarFilters filters)
    {
        var genreIds = filters.GenreIds.Where(id => id > 0).Distinct().Order().ToArray();
        var selectedSources = filters.SelectedSources
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var runtimeMin = Math.Clamp(Math.Min(filters.RuntimeMinMinutes, filters.RuntimeMaxMinutes), 0, 360);
        var runtimeMax = Math.Clamp(Math.Max(filters.RuntimeMinMinutes, filters.RuntimeMaxMinutes), 0, 360);
        var networkText = filters.NetworkText.Trim().ToLowerInvariant();
        var keywordText = filters.KeywordText.Trim().ToLowerInvariant();
        var searchText = filters.SearchText.Trim().ToLowerInvariant();

        if (filters.Language == LanguageFilter.Both
            && filters.OriginGroup == OriginGroupFilter.AllConfigured
            && genreIds.Length == 0
            && selectedSources.Length == 0
            && string.IsNullOrWhiteSpace(networkText)
            && runtimeMin == 0
            && runtimeMax == 360
            && string.IsNullOrWhiteSpace(keywordText)
            && string.IsNullOrWhiteSpace(searchText))
        {
            return DefaultGlobalFilterKey;
        }

        return string.Join(
            ',',
            $"global-lang:{filters.Language}",
            $"global-origin:{filters.OriginGroup}",
            $"global-genres:{Join(genreIds)}",
            $"global-sources:{Join(selectedSources)}",
            $"global-network:{networkText}",
            $"global-runtime:{runtimeMin}-{runtimeMax}",
            $"global-keywords:{keywordText}",
            $"global-search:{searchText}");
    }

    private static string Join<T>(IEnumerable<T> values)
    {
        return string.Join('|', values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)));
    }

    private string TmdbDateSortBy(PremiereMediaType mediaType)
    {
        return mediaType == PremiereMediaType.Series
            ? "first_air_date.asc"
            : "primary_release_date.asc";
    }
}

public sealed record MediaDiscoveryCriteria
{
    public SeriesDateMode SeriesDateMode { get; init; } = SeriesDateMode.AllEpisodes;
    public string[] OriginalLanguages { get; init; } = [];
    public string[] OriginCountries { get; init; } = [];
    public int[] GenreIds { get; init; } = [];
    public string[] SourceValues { get; init; } = [];
    public string WatchRegion { get; init; } = "";
    public int[] WatchProviderIds { get; init; } = [];
    public string[] MonetizationTypes { get; init; } = [];
    public int[] MovieReleaseTypes { get; init; } = [];
    public string[] MovieCertifications { get; init; } = [];
    public string MovieCertificationCountry { get; init; } = "";
    public int[] NetworkIds { get; init; } = [];
    public int[] TvStatusIds { get; init; } = [];
    public int[] TvTypeIds { get; init; } = [];
    public int RuntimeMinMinutes { get; init; }
    public int RuntimeMaxMinutes { get; init; } = 360;
    public string KeywordText { get; init; } = "";
    public string SourceText { get; init; } = "";
    public string SearchText { get; init; } = "";

    public static MediaDiscoveryCriteria FromFilters(MediaFilterSet filters)
    {
        return new MediaDiscoveryCriteria
        {
            SeriesDateMode = filters.SeriesDateMode,
            OriginalLanguages = NormalizeOriginalLanguages(filters.OriginalLanguages),
            OriginCountries = filters.OriginCountries
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            GenreIds = filters.GenreIds.Where(id => id > 0).Distinct().Order().ToArray(),
            SourceValues = filters.SelectedSources
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            WatchRegion = filters.WatchRegion.Trim().ToUpperInvariant(),
            WatchProviderIds = filters.SelectedSources
                .SelectMany(value => SourceFilterValue.TryGetProviderIds(value, out var providerIds) ? providerIds : [])
                .Distinct()
                .Order()
                .ToArray(),
            MonetizationTypes = filters.MonetizationTypes
                .Where(IsMonetizationType)
                .Select(value => value.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            MovieReleaseTypes = filters.MovieReleaseTypes.Where(id => id is >= 1 and <= 6).Distinct().Order().ToArray(),
            MovieCertifications = ResolveMovieCertificationValues(filters.Certifications, filters.CertificationCountry),
            MovieCertificationCountry = ResolveMovieCertificationCountry(filters.Certifications, filters.CertificationCountry),
            NetworkIds = filters.SelectedSources
                .Select(value => SourceFilterValue.TryGetNetworkId(value, out var networkId) ? networkId : 0)
                .Where(id => id > 0)
                .Distinct()
                .Order()
                .ToArray(),
            TvStatusIds = filters.TvStatuses.Select(TvStatusId).Where(id => id >= 0).Distinct().Order().ToArray(),
            TvTypeIds = filters.TvTypes.Select(TvTypeId).Where(id => id >= 0).Distinct().Order().ToArray(),
            RuntimeMinMinutes = Math.Clamp(Math.Min(filters.RuntimeMinMinutes, filters.RuntimeMaxMinutes), 0, 360),
            RuntimeMaxMinutes = Math.Clamp(Math.Max(filters.RuntimeMinMinutes, filters.RuntimeMaxMinutes), 0, 360),
            KeywordText = filters.KeywordText.Trim(),
            SourceText = filters.SourceText.Trim(),
            SearchText = filters.SearchText.Trim()
        };
    }

    public string ServerKey(PremiereMediaType mediaType)
    {
        return string.Join(
            ',',
            $"lang:{Join(OriginalLanguages)}",
            $"origin:{Join(OriginCountries)}",
            $"genres:{Join(GenreIds)}",
            $"sources:{Join(SourceValues.Select(value => value.ToLowerInvariant()))}",
            $"watch-region:{WatchRegion}",
            $"providers:{Join(WatchProviderIds)}",
            $"availability:{Join(MonetizationTypes)}",
            $"runtime:{RuntimeMinMinutes}-{RuntimeMaxMinutes}",
            $"source-text:{SourceText.ToLowerInvariant()}",
            $"keywords:{KeywordText.ToLowerInvariant()}",
            $"search:{SearchText.ToLowerInvariant()}",
            mediaType == PremiereMediaType.Series ? $"series-date:{SeriesDateMode}" : "",
            mediaType == PremiereMediaType.Movie ? $"release:{Join(MovieReleaseTypes)}" : "",
            mediaType == PremiereMediaType.Movie ? $"cert-country:{MovieCertificationCountry}" : "",
            mediaType == PremiereMediaType.Movie ? $"cert:{Join(MovieCertifications)}" : "",
            mediaType == PremiereMediaType.Series ? $"networks:{Join(NetworkIds)}" : "",
            mediaType == PremiereMediaType.Series ? $"status:{Join(TvStatusIds)}" : "",
            mediaType == PremiereMediaType.Series ? $"type:{Join(TvTypeIds)}" : "");
    }

    private static string[] ResolveMovieCertificationValues(IEnumerable<string> certifications, string certificationCountry)
    {
        var country = ResolveMovieCertificationCountry(certifications, certificationCountry);
        return string.IsNullOrWhiteSpace(country)
            ? []
            : certifications
                .Select(value => value.Split(':', 2))
                .Where(parts => parts.Length == 2 && string.Equals(parts[0], country, StringComparison.OrdinalIgnoreCase))
                .Select(parts => parts[1].Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private static string[] NormalizeOriginalLanguages(IEnumerable<string> languages)
    {
        return languages
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveMovieCertificationCountry(IEnumerable<string> certifications, string certificationCountry)
    {
        if (!string.IsNullOrWhiteSpace(certificationCountry))
        {
            return certificationCountry.Trim().ToUpperInvariant();
        }

        var countries = certifications
            .Select(value => value.Split(':', 2))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .Select(parts => parts[0].Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return countries.Length == 1 ? countries[0] : "";
    }

    private static bool IsMonetizationType(string value)
    {
        return value.Equals("flatrate", StringComparison.OrdinalIgnoreCase)
            || value.Equals("free", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ads", StringComparison.OrdinalIgnoreCase)
            || value.Equals("rent", StringComparison.OrdinalIgnoreCase)
            || value.Equals("buy", StringComparison.OrdinalIgnoreCase);
    }

    private static int TvStatusId(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "returning series" => 0,
            "planned" => 1,
            "in production" => 2,
            "ended" => 3,
            "canceled" => 4,
            "cancelled" => 4,
            "pilot" => 5,
            _ => -1
        };
    }

    private static int TvTypeId(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "documentary" => 0,
            "news" => 1,
            "miniseries" => 2,
            "reality" => 3,
            "scripted" => 4,
            "talk show" => 5,
            "video" => 6,
            _ => -1
        };
    }

    private static string Join<T>(IEnumerable<T> values)
    {
        return string.Join('|', values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)));
    }
}

public sealed record TmdbDiscoverFilters
{
    public string SortBy { get; init; } = "";
    public string OriginalLanguage { get; init; } = "";
    public IReadOnlyList<string> OriginCountries { get; init; } = [];
    public IReadOnlyList<int> GenreIds { get; init; } = [];
    public string WatchRegion { get; init; } = "";
    public IReadOnlyList<int> WatchProviderIds { get; init; } = [];
    public IReadOnlyList<string> WatchMonetizationTypes { get; init; } = [];
    public double? MinVoteAverage { get; init; }
    public double? MaxVoteAverage { get; init; }
    public int? MinVoteCount { get; init; }
    public int? RuntimeMinMinutes { get; init; }
    public int? RuntimeMaxMinutes { get; init; }
    public IReadOnlyList<int> KeywordIds { get; init; } = [];
    public bool UseEpisodeAirDate { get; init; }
    public IReadOnlyList<int> NetworkIds { get; init; } = [];
    public IReadOnlyList<int> TvStatusIds { get; init; } = [];
    public IReadOnlyList<int> TvTypeIds { get; init; } = [];
    public IReadOnlyList<int> MovieReleaseTypes { get; init; } = [];
    public string MovieCertificationCountry { get; init; } = "";
    public IReadOnlyList<string> MovieCertifications { get; init; } = [];
}
