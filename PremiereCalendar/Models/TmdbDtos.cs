using System.Text.Json.Serialization;

namespace PremiereCalendar.Models;

public sealed record TmdbPagedResponse<T>
{
    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; init; }

    [JsonPropertyName("total_results")]
    public int TotalResults { get; init; }

    [JsonPropertyName("results")]
    public List<T> Results { get; init; } = [];
}

public sealed record TmdbDiscoverBatch<T>(
    int PageStart,
    int PageEnd,
    int TotalPages,
    int TotalResults,
    IReadOnlyList<T> Results);

public sealed record TmdbTvDiscoverItem
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("original_name")]
    public string? OriginalName { get; init; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; init; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; init; }

    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; init; }

    [JsonPropertyName("origin_country")]
    public string[] OriginCountry { get; init; } = [];

    [JsonPropertyName("genre_ids")]
    public int[] GenreIds { get; init; } = [];

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; init; }

    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; init; }
}

public sealed record TmdbMovieDiscoverItem
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; init; }

    [JsonPropertyName("primary_release_date")]
    public string? PrimaryReleaseDate { get; init; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; init; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; init; }

    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; init; }

    [JsonPropertyName("origin_country")]
    public string[] OriginCountry { get; init; } = [];

    [JsonPropertyName("genre_ids")]
    public int[] GenreIds { get; init; } = [];

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; init; }

    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; init; }
}

public sealed record TmdbDetailsWithExtras
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("original_name")]
    public string? OriginalName { get; init; }

    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; init; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; init; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; init; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; init; }

    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; init; }

    [JsonPropertyName("origin_country")]
    public string[] OriginCountry { get; init; } = [];

    [JsonPropertyName("runtime")]
    public int? Runtime { get; init; }

    [JsonPropertyName("episode_run_time")]
    public int[] EpisodeRunTime { get; init; } = [];

    [JsonPropertyName("videos")]
    public TmdbVideoResponse? Videos { get; init; }

    [JsonPropertyName("external_ids")]
    public TmdbExternalIds? ExternalIds { get; init; }

    [JsonPropertyName("images")]
    public TmdbImages? Images { get; init; }

    [JsonPropertyName("genres")]
    public List<TmdbGenre> Genres { get; init; } = [];

    [JsonPropertyName("keywords")]
    public TmdbKeywordResponse? Keywords { get; init; }

    [JsonPropertyName("networks")]
    public List<TmdbNetwork> Networks { get; init; } = [];

    [JsonPropertyName("watch/providers")]
    public TmdbWatchProviders? WatchProviders { get; init; }

    [JsonPropertyName("production_countries")]
    public List<TmdbProductionCountry> ProductionCountries { get; init; } = [];

    [JsonPropertyName("release_dates")]
    public TmdbMovieReleaseDateResponse? ReleaseDates { get; init; }

    [JsonPropertyName("content_ratings")]
    public TmdbTvContentRatingResponse? ContentRatings { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("type")]
    public string? TvType { get; init; }
}

public sealed record TmdbNetwork
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record TmdbWatchProviders
{
    [JsonPropertyName("results")]
    public Dictionary<string, TmdbWatchProviderRegion> Results { get; init; } = [];
}

public sealed record TmdbWatchProviderRegion
{
    [JsonPropertyName("link")]
    public string? Link { get; init; }

    [JsonPropertyName("flatrate")]
    public List<TmdbWatchProvider> Flatrate { get; init; } = [];

    [JsonPropertyName("free")]
    public List<TmdbWatchProvider> Free { get; init; } = [];

    [JsonPropertyName("ads")]
    public List<TmdbWatchProvider> Ads { get; init; } = [];

    [JsonPropertyName("buy")]
    public List<TmdbWatchProvider> Buy { get; init; } = [];

    [JsonPropertyName("rent")]
    public List<TmdbWatchProvider> Rent { get; init; } = [];
}

public sealed record TmdbWatchProvider
{
    [JsonPropertyName("provider_id")]
    public int ProviderId { get; init; }

    [JsonPropertyName("provider_name")]
    public string? ProviderName { get; init; }

    [JsonPropertyName("display_priority")]
    public int? DisplayPriority { get; init; }

    [JsonPropertyName("logo_path")]
    public string? LogoPath { get; init; }
}

public sealed record TmdbGenre
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record TmdbKeywordResponse
{
    [JsonPropertyName("keywords")]
    public List<TmdbKeyword> Keywords { get; init; } = [];

    [JsonPropertyName("results")]
    public List<TmdbKeyword> Results { get; init; } = [];
}

public sealed record TmdbKeyword
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record TmdbVideoResponse
{
    [JsonPropertyName("results")]
    public List<TmdbVideo> Results { get; init; } = [];
}

public sealed record TmdbVideo
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("site")]
    public string? Site { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("official")]
    public bool Official { get; init; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; init; }
}

public sealed record TmdbExternalIds
{
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; init; }

    [JsonPropertyName("tvdb_id")]
    public int? TvdbId { get; init; }

    [JsonPropertyName("wikidata_id")]
    public string? WikidataId { get; init; }
}

public sealed record TmdbImages
{
    [JsonPropertyName("posters")]
    public List<TmdbImage> Posters { get; init; } = [];

    [JsonPropertyName("backdrops")]
    public List<TmdbImage> Backdrops { get; init; } = [];
}

public sealed record TmdbImage
{
    [JsonPropertyName("file_path")]
    public string? FilePath { get; init; }

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; init; }

    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; init; }
}

public sealed record TmdbProductionCountry
{
    [JsonPropertyName("iso_3166_1")]
    public string? Iso31661 { get; init; }
}

public sealed record TmdbFindResponse
{
    [JsonPropertyName("movie_results")]
    public List<TmdbFindResult> MovieResults { get; init; } = [];

    [JsonPropertyName("tv_results")]
    public List<TmdbFindResult> TvResults { get; init; } = [];
}

public sealed record TmdbFindResult
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
}

public sealed record TmdbTitleSearchResult
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; init; }

    [JsonPropertyName("original_name")]
    public string? OriginalName { get; init; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; init; }
}

public sealed record TmdbMovieReleaseDateResponse
{
    [JsonPropertyName("results")]
    public List<TmdbMovieReleaseDateRegion> Results { get; init; } = [];
}

public sealed record TmdbMovieReleaseDateRegion
{
    [JsonPropertyName("iso_3166_1")]
    public string? Iso31661 { get; init; }

    [JsonPropertyName("release_dates")]
    public List<TmdbMovieReleaseDate> ReleaseDates { get; init; } = [];
}

public sealed record TmdbMovieReleaseDate
{
    [JsonPropertyName("certification")]
    public string? Certification { get; init; }

    [JsonPropertyName("type")]
    public int Type { get; init; }
}

public sealed record TmdbTvContentRatingResponse
{
    [JsonPropertyName("results")]
    public List<TmdbTvContentRating> Results { get; init; } = [];
}

public sealed record TmdbTvContentRating
{
    [JsonPropertyName("iso_3166_1")]
    public string? Iso31661 { get; init; }

    [JsonPropertyName("rating")]
    public string? Rating { get; init; }
}

public sealed record TmdbGenreList
{
    [JsonPropertyName("genres")]
    public List<TmdbGenre> Genres { get; init; } = [];
}

public sealed record TmdbWatchProviderList
{
    [JsonPropertyName("results")]
    public List<TmdbWatchProvider> Results { get; init; } = [];
}

public sealed record TmdbConfigurationLanguage
{
    [JsonPropertyName("iso_639_1")]
    public string? Iso6391 { get; init; }

    [JsonPropertyName("english_name")]
    public string? EnglishName { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record TmdbConfigurationCountry
{
    [JsonPropertyName("iso_3166_1")]
    public string? Iso31661 { get; init; }

    [JsonPropertyName("english_name")]
    public string? EnglishName { get; init; }

    [JsonPropertyName("native_name")]
    public string? NativeName { get; init; }
}

public sealed record TmdbCertificationResponse
{
    [JsonPropertyName("certifications")]
    public Dictionary<string, List<TmdbCertification>> Certifications { get; init; } = [];
}

public sealed record TmdbCertification
{
    [JsonPropertyName("certification")]
    public string? Certification { get; init; }

    [JsonPropertyName("meaning")]
    public string? Meaning { get; init; }

    [JsonPropertyName("order")]
    public int Order { get; init; }
}
