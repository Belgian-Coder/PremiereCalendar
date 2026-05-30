using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public static class CalendarFilterState
{
    private static readonly int[] ValidMovieReleaseTypes = [1, 2, 3, 4, 5, 6];

    public static CalendarFilters Clone(CalendarFilters source)
    {
        return new CalendarFilters
        {
            WeekStart = source.WeekStart,
            PriorityDate = source.PriorityDate,
            ShowSeries = source.ShowSeries,
            ShowMovies = source.ShowMovies,
            SortMode = source.SortMode,
            SortDirection = source.SortDirection,
            ScoreSource = source.ScoreSource,
            MinScore = source.MinScore,
            MaxScore = source.MaxScore,
            IncludeUnknownScores = source.IncludeUnknownScores,
            MinVoteCount = source.MinVoteCount,
            Language = source.Language,
            OriginGroup = source.OriginGroup,
            GenreIds = [.. source.GenreIds],
            SelectedSources = [.. source.SelectedSources],
            NetworkText = source.NetworkText,
            RuntimeMinMinutes = source.RuntimeMinMinutes,
            RuntimeMaxMinutes = source.RuntimeMaxMinutes,
            KeywordText = source.KeywordText,
            SearchText = source.SearchText,
            SeriesFilters = Clone(source.SeriesFilters),
            MovieFilters = Clone(source.MovieFilters)
        };
    }

    public static MediaFilterSet Clone(MediaFilterSet source)
    {
        return new MediaFilterSet
        {
            SeriesDateMode = source.SeriesDateMode,
            OriginalLanguages = [.. source.OriginalLanguages],
            OriginCountries = [.. source.OriginCountries],
            GenreIds = [.. source.GenreIds],
            SelectedSources = [.. source.SelectedSources],
            WatchRegion = source.WatchRegion,
            SourceText = source.SourceText,
            MonetizationTypes = [.. source.MonetizationTypes],
            MovieReleaseTypes = [.. source.MovieReleaseTypes],
            Certifications = [.. source.Certifications],
            CertificationCountry = source.CertificationCountry,
            TvStatuses = [.. source.TvStatuses],
            TvTypes = [.. source.TvTypes],
            RuntimeMinMinutes = source.RuntimeMinMinutes,
            RuntimeMaxMinutes = source.RuntimeMaxMinutes,
            KeywordText = source.KeywordText,
            SearchText = source.SearchText
        };
    }

    public static void ApplyPageMode(CalendarFilters filters, CalendarPageMode pageMode)
    {
        if (pageMode == CalendarPageMode.Series)
        {
            filters.ShowSeries = true;
            filters.ShowMovies = false;
        }
        else if (pageMode == CalendarPageMode.Movies)
        {
            filters.ShowSeries = false;
            filters.ShowMovies = true;
        }
    }

    public static void Normalize(CalendarFilters filters)
    {
        NormalizeVisibleFilterState(filters);
        if (!filters.ShowSeries && !filters.ShowMovies)
        {
            filters.ShowSeries = true;
            filters.ShowMovies = true;
        }

        filters.MinVoteCount = Math.Max(0, filters.MinVoteCount);
        (filters.RuntimeMinMinutes, filters.RuntimeMaxMinutes) = NormalizeRange(
            filters.RuntimeMinMinutes,
            filters.RuntimeMaxMinutes,
            0,
            360);
        (filters.MinScore, filters.MaxScore) = NormalizeRange(
            filters.MinScore,
            filters.MaxScore,
            0,
            10);
        Normalize(filters.SeriesFilters);
        Normalize(filters.MovieFilters);
    }

    public static void NormalizeVisibleFilterState(CalendarFilters filters)
    {
        filters.IncludeUnknownScores = true;

        if (filters.SortMode == PremiereSortMode.Runtime)
        {
            filters.SortMode = PremiereSortMode.PremiereDate;
        }
    }

    public static void Normalize(MediaFilterSet filters)
    {
        filters.OriginalLanguages = filters.OriginalLanguages
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Select(language => language.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
            .ToList();
        (filters.RuntimeMinMinutes, filters.RuntimeMaxMinutes) = NormalizeRange(
            filters.RuntimeMinMinutes,
            filters.RuntimeMaxMinutes,
            0,
            360);
        filters.MovieReleaseTypes = filters.MovieReleaseTypes
            .Where(ValidMovieReleaseTypes.Contains)
            .Distinct()
            .Order()
            .ToList();
        filters.WatchRegion = filters.WatchRegion.Trim().ToUpperInvariant();
    }

    private static (int Min, int Max) NormalizeRange(int min, int max, int floor, int ceiling)
    {
        var normalizedMin = Math.Clamp(min, floor, ceiling);
        var normalizedMax = Math.Clamp(max, floor, ceiling);
        return normalizedMin <= normalizedMax
            ? (normalizedMin, normalizedMax)
            : (normalizedMax, normalizedMin);
    }

    private static (double Min, double Max) NormalizeRange(double min, double max, double floor, double ceiling)
    {
        var normalizedMin = Math.Clamp(min, floor, ceiling);
        var normalizedMax = Math.Clamp(max, floor, ceiling);
        return normalizedMin <= normalizedMax
            ? (normalizedMin, normalizedMax)
            : (normalizedMax, normalizedMin);
    }
}
