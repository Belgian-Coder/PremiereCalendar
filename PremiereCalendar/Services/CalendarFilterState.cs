using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public static class CalendarFilterState
{
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
        filters.MinVoteCount = Math.Max(0, filters.MinVoteCount);
        filters.RuntimeMinMinutes = Math.Clamp(filters.RuntimeMinMinutes, 0, 360);
        filters.RuntimeMaxMinutes = Math.Clamp(filters.RuntimeMaxMinutes, 0, 360);
        filters.MinScore = Math.Clamp(filters.MinScore, 0, 10);
        filters.MaxScore = Math.Clamp(filters.MaxScore, 0, 10);
        Normalize(filters.SeriesFilters);
        Normalize(filters.MovieFilters);
    }

    public static void NormalizeVisibleFilterState(CalendarFilters filters)
    {
        filters.ScoreSource = ScoreSource.Tmdb;
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
        filters.RuntimeMinMinutes = Math.Clamp(filters.RuntimeMinMinutes, 0, 360);
        filters.RuntimeMaxMinutes = Math.Clamp(filters.RuntimeMaxMinutes, 0, 360);
        filters.WatchRegion = filters.WatchRegion.Trim().ToUpperInvariant();
    }
}
