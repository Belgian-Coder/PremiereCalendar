using System.Globalization;

namespace PremiereCalendar.Services;

public static class TmdbQueryBuilder
{
    private const string DetailAppendResponses = "videos,external_ids,images,keywords,watch/providers,release_dates,content_ratings";
    private const string DetailImageLanguages = "en,nl,null";

    public static string BuildDiscoverTvPath(
        DateOnly start,
        DateOnly end,
        TmdbDiscoverFilters filters,
        int page)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["with_original_language"] = filters.OriginalLanguage,
            ["sort_by"] = string.IsNullOrWhiteSpace(filters.SortBy) ? "first_air_date.asc" : filters.SortBy,
            ["include_adult"] = "false",
            ["page"] = page.ToString(CultureInfo.InvariantCulture)
        };

        if (filters.UseEpisodeAirDate)
        {
            parameters["air_date.gte"] = FormatDate(start);
            parameters["air_date.lte"] = FormatDate(end);
        }
        else
        {
            parameters["first_air_date.gte"] = FormatDate(start);
            parameters["first_air_date.lte"] = FormatDate(end);
        }

        AddDiscoverFilters(parameters, filters, PremiereCalendar.Models.PremiereMediaType.Series);
        return BuildPath("discover/tv", parameters);
    }

    public static string BuildDiscoverMoviePath(
        DateOnly start,
        DateOnly end,
        TmdbDiscoverFilters filters,
        int page)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["with_original_language"] = filters.OriginalLanguage,
            ["sort_by"] = string.IsNullOrWhiteSpace(filters.SortBy) ? "primary_release_date.asc" : filters.SortBy,
            ["include_adult"] = "false",
            ["page"] = page.ToString(CultureInfo.InvariantCulture)
        };

        if (string.IsNullOrWhiteSpace(filters.WatchRegion))
        {
            parameters["primary_release_date.gte"] = FormatDate(start);
            parameters["primary_release_date.lte"] = FormatDate(end);
        }
        else
        {
            parameters["release_date.gte"] = FormatDate(start);
            parameters["release_date.lte"] = FormatDate(end);
            parameters["region"] = filters.WatchRegion.Trim().ToUpperInvariant();
        }

        AddDiscoverFilters(parameters, filters, PremiereCalendar.Models.PremiereMediaType.Movie);
        return BuildPath("discover/movie", parameters);
    }

    public static string BuildDiscoverTvByNetworksPath(
        DateOnly start,
        DateOnly end,
        IReadOnlyList<int> networkIds,
        int page)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["first_air_date.gte"] = FormatDate(start),
            ["first_air_date.lte"] = FormatDate(end),
            ["with_networks"] = NetworkIds(networkIds),
            ["sort_by"] = "first_air_date.asc",
            ["include_adult"] = "false",
            ["page"] = page.ToString(CultureInfo.InvariantCulture)
        };

        return BuildPath("discover/tv", parameters);
    }

    public static string BuildTvDetailsPath(int id)
    {
        return BuildPath($"tv/{id}", new Dictionary<string, string?>
        {
            ["append_to_response"] = DetailAppendResponses,
            ["include_image_language"] = DetailImageLanguages
        });
    }

    public static string BuildTvSeasonDetailsPath(int id, int seasonNumber)
    {
        return BuildPath($"tv/{id}/season/{seasonNumber}", new Dictionary<string, string?>());
    }

    public static string BuildMovieDetailsPath(int id)
    {
        return BuildPath($"movie/{id}", new Dictionary<string, string?>
        {
            ["append_to_response"] = DetailAppendResponses,
            ["include_image_language"] = DetailImageLanguages
        });
    }

    public static string BuildFindByExternalIdPath(string externalId, string externalSource)
    {
        return BuildPath($"find/{externalId}", new Dictionary<string, string?>
        {
            ["external_source"] = externalSource
        });
    }

    public static string BuildMovieGenresPath()
    {
        return BuildPath("genre/movie/list", new Dictionary<string, string?>
        {
            ["language"] = "en-US"
        });
    }

    public static string BuildTvGenresPath()
    {
        return BuildPath("genre/tv/list", new Dictionary<string, string?>
        {
            ["language"] = "en-US"
        });
    }

    public static string BuildLanguagesPath()
    {
        return "configuration/languages";
    }

    public static string BuildSearchKeywordPath(string query)
    {
        return BuildPath("search/keyword", new Dictionary<string, string?>
        {
            ["query"] = query,
            ["page"] = "1"
        });
    }

    public static string BuildSearchTitlePath(
        PremiereCalendar.Models.PremiereMediaType mediaType,
        string query,
        int? year)
    {
        var isMovie = mediaType == PremiereCalendar.Models.PremiereMediaType.Movie;
        return BuildPath(isMovie ? "search/movie" : "search/tv", new Dictionary<string, string?>
        {
            ["query"] = query,
            [isMovie ? "year" : "first_air_date_year"] = year is > 0
                ? year.Value.ToString(CultureInfo.InvariantCulture)
                : null,
            ["include_adult"] = "false",
            ["page"] = "1"
        });
    }

    public static string BuildChangesPath(
        PremiereCalendar.Models.PremiereMediaType mediaType,
        DateOnly start,
        DateOnly end,
        int page)
    {
        return BuildPath(
            mediaType == PremiereCalendar.Models.PremiereMediaType.Movie ? "movie/changes" : "tv/changes",
            new Dictionary<string, string?>
            {
                ["start_date"] = FormatDate(start),
                ["end_date"] = FormatDate(end),
                ["page"] = page.ToString(CultureInfo.InvariantCulture)
            });
    }

    public static string BuildCountriesPath()
    {
        return BuildPath("configuration/countries", new Dictionary<string, string?>
        {
            ["language"] = "en-US"
        });
    }

    public static string BuildWatchProvidersPath(PremiereCalendar.Models.PremiereMediaType mediaType, string? region)
    {
        var path = mediaType == PremiereCalendar.Models.PremiereMediaType.Movie
            ? "watch/providers/movie"
            : "watch/providers/tv";

        return BuildPath(path, new Dictionary<string, string?>
        {
            ["language"] = "en-US",
            ["watch_region"] = string.IsNullOrWhiteSpace(region) ? null : region.Trim().ToUpperInvariant()
        });
    }

    public static string BuildMovieCertificationsPath()
    {
        return "certification/movie/list";
    }

    public static string BuildTvCertificationsPath()
    {
        return "certification/tv/list";
    }

    private static void AddDiscoverFilters(
        IDictionary<string, string?> parameters,
        TmdbDiscoverFilters filters,
        PremiereCalendar.Models.PremiereMediaType mediaType)
    {
        AddPipe(parameters, "with_origin_country", filters.OriginCountries);
        AddPipe(parameters, "with_genres", filters.GenreIds);
        AddPipe(parameters, "with_keywords", filters.KeywordIds);
        AddInvariant(parameters, "vote_average.gte", filters.MinVoteAverage);
        AddInvariant(parameters, "vote_average.lte", filters.MaxVoteAverage);
        AddInvariant(parameters, "vote_count.gte", filters.MinVoteCount);
        AddInvariant(parameters, "with_runtime.gte", filters.RuntimeMinMinutes);
        AddInvariant(parameters, "with_runtime.lte", filters.RuntimeMaxMinutes);

        if (!string.IsNullOrWhiteSpace(filters.WatchRegion)
            && (filters.WatchProviderIds.Count > 0 || filters.WatchMonetizationTypes.Count > 0))
        {
            parameters["watch_region"] = filters.WatchRegion;
            AddPipe(parameters, "with_watch_providers", filters.WatchProviderIds);
            AddPipe(parameters, "with_watch_monetization_types", filters.WatchMonetizationTypes);
        }

        if (mediaType == PremiereCalendar.Models.PremiereMediaType.Series)
        {
            AddPipe(parameters, "with_networks", filters.NetworkIds);
            AddPipe(parameters, "with_status", filters.TvStatusIds);
            AddPipe(parameters, "with_type", filters.TvTypeIds);
        }
        else
        {
            AddPipe(parameters, "with_release_type", filters.MovieReleaseTypes);
            if (!string.IsNullOrWhiteSpace(filters.MovieCertificationCountry)
                && filters.MovieCertifications.Count > 0)
            {
                parameters["certification_country"] = filters.MovieCertificationCountry;
                parameters["certification"] = string.Join('|', filters.MovieCertifications);
            }
        }
    }

    private static void AddPipe<T>(IDictionary<string, string?> parameters, string key, IReadOnlyCollection<T> values)
    {
        if (values.Count > 0)
        {
            parameters[key] = string.Join('|', values);
        }
    }

    private static void AddInvariant(IDictionary<string, string?> parameters, string key, IFormattable? value)
    {
        if (value is not null)
        {
            parameters[key] = value.ToString(null, CultureInfo.InvariantCulture);
        }
    }

    private static string NetworkIds(IReadOnlyList<int> networkIds)
    {
        return string.Join('|', networkIds.Where(id => id > 0).Distinct());
    }

    private static string BuildPath(string path, IReadOnlyDictionary<string, string?> parameters)
    {
        var query = string.Join(
            '&',
            parameters
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));

        return string.IsNullOrWhiteSpace(query) ? path : $"{path}?{query}";
    }

    private static string FormatDate(DateOnly date)
    {
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
