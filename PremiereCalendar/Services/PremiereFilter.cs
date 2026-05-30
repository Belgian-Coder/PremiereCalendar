using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public static class PremiereFilter
{
    public static IReadOnlyList<PremiereItem> Apply(IEnumerable<PremiereItem> items, CalendarFilters filters)
    {
        return items
            .Where(item => Matches(item, filters))
            .ApplySort(filters.SortMode, filters.SortDirection, filters.ScoreSource)
            .ToList();
    }

    public static int CountMatches(IEnumerable<PremiereItem> items, CalendarFilters filters)
    {
        return items.Count(item => Matches(item, filters));
    }

    public static IOrderedEnumerable<PremiereItem> SortItems(
        IEnumerable<PremiereItem> items,
        PremiereSortMode sortMode,
        SortDirection sortDirection,
        ScoreSource scoreSource)
    {
        return items.ApplySort(sortMode, sortDirection, scoreSource);
    }

    private static bool Matches(PremiereItem item, CalendarFilters filters)
    {
        return PassesMediaTypeFilter(item, filters)
            && PassesLanguageFilter(item, filters.Language)
            && PassesOriginGroupFilter(item, filters.OriginGroup)
            && PassesGenreFilter(item, filters.GenreIds)
            && PassesSourceSelectionFilter(item, filters.SelectedSources)
            && PassesNetworkFilter(item, filters.NetworkText)
            && PassesSearchFilter(item, filters.SearchText)
            && PassesKeywordFilter(item, filters.KeywordText)
            && PassesMediaFilterSet(item, EffectiveMediaFilterSet(item, filters))
            && PassesScoreFilter(item, filters)
            && PassesVoteCountFilter(item, filters.MinVoteCount, filters.ScoreSource)
            && PassesRuntimeFilter(item, filters.RuntimeMinMinutes, filters.RuntimeMaxMinutes);
    }

    public static bool PassesScoreFilter(PremiereItem item, CalendarFilters filters)
    {
        var score = ScoreFor(item, filters.ScoreSource);

        if (score is null)
        {
            return filters.IncludeUnknownScores;
        }

        var min = Math.Min(filters.MinScore, filters.MaxScore);
        var max = Math.Max(filters.MinScore, filters.MaxScore);

        if (filters.ScoreSource == ScoreSource.RottenTomatoes)
        {
            min *= 10;
            max *= 10;
        }
        else if (filters.ScoreSource == ScoreSource.Metacritic)
        {
            min *= 10;
            max *= 10;
        }

        return score >= min && score <= max;
    }

    public static double? ScoreFor(PremiereItem item, ScoreSource source)
    {
        return source switch
        {
            ScoreSource.Tmdb => item.TmdbScore,
            ScoreSource.Imdb => item.ImdbScore,
            ScoreSource.RottenTomatoes => item.RottenTomatoesScore,
            ScoreSource.Metacritic => item.MetacriticScore,
            _ => item.TmdbScore
        };
    }

    public static int VoteCountFor(PremiereItem item, ScoreSource source)
    {
        return source switch
        {
            ScoreSource.Imdb => item.ImdbVoteCount ?? 0,
            _ => item.TmdbVoteCount ?? 0
        };
    }

    private static bool PassesMediaTypeFilter(PremiereItem item, CalendarFilters filters)
    {
        return item.MediaType switch
        {
            PremiereMediaType.Series => filters.ShowSeries,
            PremiereMediaType.Movie => filters.ShowMovies,
            _ => true
        };
    }

    private static bool PassesSearchFilter(PremiereItem item, string searchText)
    {
        return string.IsNullOrWhiteSpace(searchText)
            || item.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PassesKeywordFilter(PremiereItem item, string keywordText)
    {
        return string.IsNullOrWhiteSpace(keywordText)
            || item.Keywords.Any(keyword => keyword.Contains(keywordText, StringComparison.OrdinalIgnoreCase))
            || (item.Overview?.Contains(keywordText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool PassesGenreFilter(PremiereItem item, IReadOnlyCollection<int> genreIds)
    {
        return genreIds.Count == 0 || item.GenreIds.Any(genreIds.Contains);
    }

    private static bool PassesNetworkFilter(PremiereItem item, string networkText)
    {
        return string.IsNullOrWhiteSpace(networkText)
            || SourceCandidates(item).Any(source => source.Contains(networkText, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PassesSourceSelectionFilter(PremiereItem item, IReadOnlyCollection<string> selectedSources)
    {
        return selectedSources.Count == 0
            || selectedSources.Any(selected => SourceFilterValue.Matches(item, selected));
    }

    private static IEnumerable<string> SourceCandidates(PremiereItem item)
    {
        return item.SourceNames
            .Concat([item.NetworkName, item.WebChannelName])
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => source!);
    }

    private static bool PassesVoteCountFilter(PremiereItem item, int minVoteCount, ScoreSource scoreSource)
    {
        return minVoteCount <= 0 || VoteCountFor(item, scoreSource) >= minVoteCount;
    }

    private static bool PassesRuntimeFilter(PremiereItem item, int runtimeMinMinutes, int runtimeMaxMinutes)
    {
        var min = Math.Max(0, Math.Min(runtimeMinMinutes, runtimeMaxMinutes));
        var max = Math.Max(runtimeMinMinutes, runtimeMaxMinutes);
        if (min <= 0 && max >= 360)
        {
            return true;
        }

        return item.RuntimeMinutes is { } runtime
            && runtime >= min
            && runtime <= max;
    }

    private static bool PassesMediaFilterSet(PremiereItem item, MediaFilterSet filters)
    {
        if (!filters.HasCriteria)
        {
            return true;
        }

        return PassesOriginalLanguageFilter(item, filters.OriginalLanguages)
            && PassesSeriesDateModeFilter(item, filters.SeriesDateMode)
            && PassesOriginCountryFilter(item, filters.OriginCountries)
            && PassesGenreFilter(item, filters.GenreIds)
            && PassesSourceSelectionFilter(item, filters.SelectedSources)
            && PassesNetworkFilter(item, filters.SourceText)
            && PassesMonetizationFilter(item, filters.MonetizationTypes)
            && PassesMovieReleaseTypeFilter(item, filters.MovieReleaseTypes)
            && PassesCertificationFilter(item, filters.Certifications, filters.CertificationCountry)
            && PassesTvStatusFilter(item, filters.TvStatuses)
            && PassesTvTypeFilter(item, filters.TvTypes)
            && PassesRuntimeFilter(item, filters.RuntimeMinMinutes, filters.RuntimeMaxMinutes)
            && PassesSearchFilter(item, filters.SearchText)
            && PassesKeywordFilter(item, filters.KeywordText);
    }

    private static bool PassesSeriesDateModeFilter(PremiereItem item, SeriesDateMode seriesDateMode)
    {
        return item.MediaType != PremiereMediaType.Series
            || seriesDateMode == SeriesDateMode.AllEpisodes
            || item.Type == PremiereItemType.SeriesPremiere;
    }

    private static MediaFilterSet EffectiveMediaFilterSet(PremiereItem item, CalendarFilters filters)
    {
        return item.MediaType == PremiereMediaType.Series
            ? filters.SeriesFilters
            : filters.MovieFilters;
    }

    private static bool PassesOriginalLanguageFilter(PremiereItem item, IReadOnlyCollection<string> originalLanguages)
    {
        return originalLanguages.Count == 0
            || originalLanguages.Any(language => string.Equals(
                item.OriginalLanguage,
                language.Trim(),
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool PassesOriginCountryFilter(PremiereItem item, IReadOnlyCollection<string> originCountries)
    {
        return originCountries.Count == 0
            || originCountries.Any(country => HasOriginCountry(item, country));
    }

    private static bool PassesMonetizationFilter(PremiereItem item, IReadOnlyCollection<string> monetizationTypes)
    {
        return monetizationTypes.Count == 0
            || item.Sources.Any(source => monetizationTypes.Contains(source.Kind, StringComparer.OrdinalIgnoreCase));
    }

    private static bool PassesMovieReleaseTypeFilter(PremiereItem item, IReadOnlyCollection<int> releaseTypes)
    {
        return item.MediaType != PremiereMediaType.Movie
            || releaseTypes.Count == 0
            || item.MovieReleaseTypes.Any(releaseTypes.Contains);
    }

    private static bool PassesCertificationFilter(
        PremiereItem item,
        IReadOnlyCollection<string> certifications,
        string certificationCountry)
    {
        var country = certificationCountry.Trim();
        var countryMatches = string.IsNullOrWhiteSpace(country)
            || item.Certifications.Any(certification => certification.StartsWith($"{country}:", StringComparison.OrdinalIgnoreCase));

        return countryMatches
            && (certifications.Count == 0
                || certifications.Any(selected => item.Certifications.Contains(selected, StringComparer.OrdinalIgnoreCase)));
    }

    private static bool PassesTvStatusFilter(PremiereItem item, IReadOnlyCollection<string> statuses)
    {
        return item.MediaType != PremiereMediaType.Series
            || statuses.Count == 0
            || statuses.Contains(item.TvStatus ?? "", StringComparer.OrdinalIgnoreCase);
    }

    private static bool PassesTvTypeFilter(PremiereItem item, IReadOnlyCollection<string> types)
    {
        return item.MediaType != PremiereMediaType.Series
            || types.Count == 0
            || types.Contains(item.TvType ?? "", StringComparer.OrdinalIgnoreCase);
    }

    private static bool PassesLanguageFilter(PremiereItem item, LanguageFilter language)
    {
        return language switch
        {
            LanguageFilter.English => string.Equals(item.OriginalLanguage, "en", StringComparison.OrdinalIgnoreCase),
            LanguageFilter.Dutch => string.Equals(item.OriginalLanguage, "nl", StringComparison.OrdinalIgnoreCase),
            LanguageFilter.French => string.Equals(item.OriginalLanguage, "fr", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private static bool PassesOriginGroupFilter(PremiereItem item, OriginGroupFilter originGroup)
    {
        return originGroup switch
        {
            OriginGroupFilter.Belgium => HasOriginCountry(item, "BE"),
            OriginGroupFilter.UnitedStates => HasOriginCountry(item, "US"),
            OriginGroupFilter.UnitedKingdom => HasOriginCountry(item, "GB"),
            OriginGroupFilter.Australia => HasOriginCountry(item, "AU"),
            OriginGroupFilter.DutchLanguage => string.Equals(item.OriginalLanguage, "nl", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private static bool HasOriginCountry(PremiereItem item, string country)
    {
        return item.OriginCountries.Any(value => string.Equals(value, country, StringComparison.OrdinalIgnoreCase));
    }

    private static IOrderedEnumerable<PremiereItem> ApplySort(
        this IEnumerable<PremiereItem> items,
        PremiereSortMode sortMode,
        SortDirection sortDirection,
        ScoreSource scoreSource)
    {
        return sortMode switch
        {
            PremiereSortMode.Title => SortBy(
                items,
                sortDirection,
                item => item.Title,
                StringComparer.OrdinalIgnoreCase),
            PremiereSortMode.Score => SortBy(
                    items,
                    sortDirection,
                    item => ScoreFor(item, scoreSource) ?? double.MinValue)
                .ThenBy(item => item.PremiereDate)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase),
            PremiereSortMode.VoteCount => SortBy(
                    items,
                    sortDirection,
                    item => VoteCountFor(item, scoreSource))
                .ThenBy(item => item.PremiereDate)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase),
            PremiereSortMode.Runtime => SortBy(
                    items,
                    sortDirection,
                    item => item.RuntimeMinutes ?? 0)
                .ThenBy(item => item.PremiereDate)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase),
            _ => SortBy(items, sortDirection, item => item.PremiereDate)
                .ThenBy(VerificationSortRank)
                .ThenBy(item => item.MediaType)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static int VerificationSortRank(PremiereItem item)
    {
        return item.VerificationState == PremiereVerificationState.Unverified ? 1 : 0;
    }

    private static IOrderedEnumerable<PremiereItem> SortBy<TKey>(
        IEnumerable<PremiereItem> items,
        SortDirection sortDirection,
        Func<PremiereItem, TKey> keySelector)
    {
        return sortDirection == SortDirection.Descending
            ? items.OrderByDescending(keySelector)
            : items.OrderBy(keySelector);
    }

    private static IOrderedEnumerable<PremiereItem> SortBy<TKey>(
        IEnumerable<PremiereItem> items,
        SortDirection sortDirection,
        Func<PremiereItem, TKey> keySelector,
        IComparer<TKey> comparer)
    {
        return sortDirection == SortDirection.Descending
            ? items.OrderByDescending(keySelector, comparer)
            : items.OrderBy(keySelector, comparer);
    }
}
