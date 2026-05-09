using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class TmdbQueryBuilderTests
{
    [Fact]
    public void BuildDiscoverTvPath_UsesFirstAirDateAndOriginCountries()
    {
        var path = TmdbQueryBuilder.BuildDiscoverTvPath(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters
            {
                OriginalLanguage = "en",
                OriginCountries = ["US", "GB", "AU"],
                SortBy = "first_air_date.asc"
            },
            2);

        var query = ParseQuery(path);

        Assert.Equal("2026-05-04", query["first_air_date.gte"]);
        Assert.Equal("2026-05-10", query["first_air_date.lte"]);
        Assert.Equal("en", query["with_original_language"]);
        Assert.Equal("US|GB|AU", query["with_origin_country"]);
        Assert.Equal("first_air_date.asc", query["sort_by"]);
        Assert.Equal("false", query["include_adult"]);
        Assert.Equal("2", query["page"]);
        Assert.False(query.ContainsKey("air_date.gte"));
        Assert.False(query.ContainsKey("air_date.lte"));
    }

    [Fact]
    public void BuildDiscoverMoviePath_UsesPrimaryReleaseDateWithoutHiddenRuntimeOrReleaseType()
    {
        var path = TmdbQueryBuilder.BuildDiscoverMoviePath(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters
            {
                OriginalLanguage = "nl",
                OriginCountries = ["NL", "BE"],
                SortBy = "primary_release_date.asc"
            },
            1);

        var query = ParseQuery(path);

        Assert.Equal("2026-05-04", query["primary_release_date.gte"]);
        Assert.Equal("2026-05-10", query["primary_release_date.lte"]);
        Assert.Equal("nl", query["with_original_language"]);
        Assert.Equal("NL|BE", query["with_origin_country"]);
        Assert.Equal("primary_release_date.asc", query["sort_by"]);
        Assert.False(query.ContainsKey("with_runtime.gte"));
        Assert.False(query.ContainsKey("with_release_type"));
    }

    [Fact]
    public void BuildDiscoverPaths_OmitOriginalLanguageWhenLanguageIsMissing()
    {
        var tvQuery = ParseQuery(TmdbQueryBuilder.BuildDiscoverTvPath(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters { OriginCountries = ["BE"] },
            1));
        var movieQuery = ParseQuery(TmdbQueryBuilder.BuildDiscoverMoviePath(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters { OriginCountries = ["BE"] },
            1));

        Assert.False(tvQuery.ContainsKey("with_original_language"));
        Assert.Equal("BE", tvQuery["with_origin_country"]);
        Assert.False(movieQuery.ContainsKey("with_original_language"));
        Assert.Equal("BE", movieQuery["with_origin_country"]);
        Assert.False(movieQuery.ContainsKey("with_runtime.gte"));
    }

    [Fact]
    public void BuildDiscoverPaths_CanUseUnfilteredDateWindows()
    {
        var tvQuery = ParseQuery(TmdbQueryBuilder.BuildDiscoverTvPath(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters(),
            1));
        var movieQuery = ParseQuery(TmdbQueryBuilder.BuildDiscoverMoviePath(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters(),
            1));

        Assert.False(tvQuery.ContainsKey("with_original_language"));
        Assert.False(tvQuery.ContainsKey("with_origin_country"));
        Assert.False(movieQuery.ContainsKey("with_original_language"));
        Assert.False(movieQuery.ContainsKey("with_origin_country"));
        Assert.False(movieQuery.ContainsKey("with_runtime.gte"));
    }

    [Fact]
    public void BuildDiscoverTvPath_AllowsBelgianOriginWithoutLanguageRestriction()
    {
        var path = TmdbQueryBuilder.BuildDiscoverTvPath(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters { OriginCountries = ["BE"] },
            1);

        var query = ParseQuery(path);

        Assert.Equal("BE", query["with_origin_country"]);
        Assert.False(query.ContainsKey("with_original_language"));
        Assert.Equal("2026-05-04", query["first_air_date.gte"]);
    }

    [Fact]
    public void BuildDiscoverMoviePath_AddsTmdbSupportedFilterParameters()
    {
        var path = TmdbQueryBuilder.BuildDiscoverMoviePath(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            new TmdbDiscoverFilters
            {
                SortBy = "vote_average.desc",
                GenreIds = [28, 53],
                WatchRegion = "BE",
                WatchProviderIds = [337],
                WatchMonetizationTypes = ["flatrate", "rent"],
                MinVoteAverage = 6.5,
                MaxVoteAverage = 9.0,
                MinVoteCount = 50,
                RuntimeMinMinutes = 80,
                RuntimeMaxMinutes = 150,
                KeywordIds = [123],
                MovieReleaseTypes = [4],
                MovieCertificationCountry = "US",
                MovieCertifications = ["PG-13"]
            },
            1);

        var query = ParseQuery(path);

        Assert.Equal("vote_average.desc", query["sort_by"]);
        Assert.Equal("2026-05-04", query["release_date.gte"]);
        Assert.Equal("2026-05-10", query["release_date.lte"]);
        Assert.Equal("BE", query["region"]);
        Assert.False(query.ContainsKey("primary_release_date.gte"));
        Assert.Equal("28|53", query["with_genres"]);
        Assert.Equal("BE", query["watch_region"]);
        Assert.Equal("337", query["with_watch_providers"]);
        Assert.Equal("flatrate|rent", query["with_watch_monetization_types"]);
        Assert.Equal("6.5", query["vote_average.gte"]);
        Assert.Equal("9", query["vote_average.lte"]);
        Assert.Equal("50", query["vote_count.gte"]);
        Assert.Equal("80", query["with_runtime.gte"]);
        Assert.Equal("150", query["with_runtime.lte"]);
        Assert.Equal("123", query["with_keywords"]);
        Assert.Equal("4", query["with_release_type"]);
        Assert.Equal("US", query["certification_country"]);
        Assert.Equal("PG-13", query["certification"]);
    }

    [Fact]
    public void BuildDiscoverTvByNetworksPath_UsesNetworkIdsWithoutMovieOnlyFilters()
    {
        var path = TmdbQueryBuilder.BuildDiscoverTvByNetworksPath(
            new DateOnly(2026, 4, 1),
            new DateOnly(2026, 4, 30),
            [556, 5257, 4496, 556],
            3);

        var query = ParseQuery(path);

        Assert.Equal("2026-04-01", query["first_air_date.gte"]);
        Assert.Equal("2026-04-30", query["first_air_date.lte"]);
        Assert.Equal("556|5257|4496", query["with_networks"]);
        Assert.Equal("first_air_date.asc", query["sort_by"]);
        Assert.Equal("false", query["include_adult"]);
        Assert.Equal("3", query["page"]);
        Assert.False(query.ContainsKey("with_runtime.gte"));
        Assert.False(query.ContainsKey("with_origin_country"));
        Assert.False(query.ContainsKey("with_original_language"));
    }

    [Fact]
    public void BuildDetailsPaths_AppendVideosAndExternalIds()
    {
        var tvQuery = ParseQuery(TmdbQueryBuilder.BuildTvDetailsPath(12));
        var movieQuery = ParseQuery(TmdbQueryBuilder.BuildMovieDetailsPath(34));

        Assert.Equal("videos,external_ids,images,keywords,watch/providers,release_dates,content_ratings", tvQuery["append_to_response"]);
        Assert.Equal("en,nl,null", tvQuery["include_image_language"]);
        Assert.Equal("videos,external_ids,images,keywords,watch/providers,release_dates,content_ratings", movieQuery["append_to_response"]);
        Assert.Equal("en,nl,null", movieQuery["include_image_language"]);
    }

    [Fact]
    public void BuildWatchProvidersPath_OmitsRegionWhenNoUiRegionFilterIsConfigured()
    {
        var query = ParseQuery(TmdbQueryBuilder.BuildWatchProvidersPath(PremiereCalendar.Models.PremiereMediaType.Movie, ""));

        Assert.Equal("en-US", query["language"]);
        Assert.False(query.ContainsKey("watch_region"));
    }

    private static Dictionary<string, string> ParseQuery(string path)
    {
        var queryStart = path.IndexOf('?');
        Assert.True(queryStart >= 0);

        return path[(queryStart + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "");
    }
}
