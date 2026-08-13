using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed partial class PremiereService
{
    private PremiereItem? MapSeriesMetadata(
        TmdbTvDiscoverItem item,
        DateOnly? premiereDateOverride = null,
        PremiereItemType? itemTypeOverride = null,
        string? episodeTitle = null,
        int? seasonNumber = null,
        int? episodeNumber = null,
        string? episodeSource = null)
    {
        var itemType = itemTypeOverride ?? PremiereItemType.SeriesPremiere;
        if (item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name))
        {
            return null;
        }

        DateOnly premiereDate;
        if (premiereDateOverride is { } dateOverride)
        {
            premiereDate = dateOverride;
        }
        else if (!TryParseTmdbDate(item.FirstAirDate, out premiereDate))
        {
            return null;
        }

        var posterUrl = BuildImageUrl(_options.PosterSize, item.PosterPath);
        var backdropUrl = BuildImageUrl(_options.BackdropSize, item.BackdropPath);
        var dateSemantics = itemType == PremiereItemType.SeriesEpisode
            ? new PremiereDateSemantics(
                premiereDate,
                string.IsNullOrWhiteSpace(episodeSource)
                    ? PremiereDateSourceKind.TmdbEpisodeAirDate
                    : PremiereDateSourceKind.ExternalProviderDate,
                string.IsNullOrWhiteSpace(episodeSource)
                    ? PremiereDataConfidence.Medium
                    : PremiereDataConfidence.High,
                string.IsNullOrWhiteSpace(episodeSource)
                    ? "TMDb episode air-date discovery."
                    : $"Episode date from {episodeSource}.")
            : new PremiereDateSemantics(
                premiereDate,
                premiereDateOverride is not null
                    ? PremiereDateSourceKind.TmdbSeasonOneEpisodeOne
                    : PremiereDateSourceKind.TmdbFirstAirDate,
                premiereDateOverride is not null
                    ? PremiereDataConfidence.High
                    : PremiereDataConfidence.Medium,
                premiereDateOverride is not null
                    ? "Season 1 episode 1 air date from TMDb."
                    : "TMDb first air date.");
        return new PremiereItem
        {
            CanonicalId = itemType == PremiereItemType.SeriesEpisode
                ? PremiereIdentity.SeriesEpisodeCanonicalId(item.Id, premiereDate, seasonNumber, episodeNumber)
                : PremiereIdentity.CanonicalId(PremiereMediaType.Series, item.Id),
            Type = itemType,
            MediaType = PremiereMediaType.Series,
            TmdbId = item.Id,
            Title = item.Name,
            OriginalTitle = item.OriginalName,
            PremiereDate = premiereDate,
            Overview = item.Overview,
            PosterUrl = posterUrl,
            BackdropUrl = backdropUrl,
            ImageSource = posterUrl is null ? null : "TMDb poster",
            TmdbUrl = $"https://www.themoviedb.org/tv/{item.Id}",
            OriginalLanguage = item.OriginalLanguage ?? "",
            OriginCountries = item.OriginCountry,
            GenreIds = item.GenreIds,
            EpisodeTitle = episodeTitle,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            EpisodeSource = episodeSource,
            TmdbScore = item.VoteAverage,
            TmdbVoteCount = item.VoteCount,
            DateSemantics = dateSemantics,
            MergeContributions = [PremiereDiagnosticsFactory.TmdbContribution(PremiereMediaType.Series, item.Id)]
        };
    }

    private async Task<PremiereItem?> MapSeriesPremiereMetadataAsync(
        TmdbTvDiscoverItem item,
        CancellationToken cancellationToken,
        bool forceRefresh,
        DateOnly requestedStart,
        DateOnly requestedEnd)
    {
        var premiereDate = await GetSeasonOneEpisodeOneDateAsync(item.Id, cancellationToken, forceRefresh);
        if (premiereDate is { } canonicalDate
            && (canonicalDate < requestedStart || canonicalDate > requestedEnd))
        {
            return null;
        }

        return MapSeriesMetadata(item, premiereDateOverride: premiereDate);
    }

    private PremiereItem? MapMovieMetadata(TmdbMovieDiscoverItem item)
    {
        var releaseDateValue = CoalesceText(item.ReleaseDate, item.PrimaryReleaseDate);
        if (item.Id <= 0
            || string.IsNullOrWhiteSpace(item.Title)
            || !TryParseTmdbDate(releaseDateValue, out var premiereDate))
        {
            return null;
        }

        var posterUrl = BuildImageUrl(_options.PosterSize, item.PosterPath);
        var backdropUrl = BuildImageUrl(_options.BackdropSize, item.BackdropPath);
        var usedReleaseDate = !string.IsNullOrWhiteSpace(item.ReleaseDate);
        return new PremiereItem
        {
            CanonicalId = PremiereIdentity.CanonicalId(PremiereMediaType.Movie, item.Id),
            Type = PremiereIdentity.ItemType(PremiereMediaType.Movie),
            MediaType = PremiereMediaType.Movie,
            TmdbId = item.Id,
            Title = item.Title,
            OriginalTitle = item.OriginalTitle,
            PremiereDate = premiereDate,
            Overview = item.Overview,
            PosterUrl = posterUrl,
            BackdropUrl = backdropUrl,
            ImageSource = posterUrl is null ? null : "TMDb poster",
            TmdbUrl = $"https://www.themoviedb.org/movie/{item.Id}",
            OriginalLanguage = item.OriginalLanguage ?? "",
            OriginCountries = item.OriginCountry,
            GenreIds = item.GenreIds,
            TmdbScore = item.VoteAverage,
            TmdbVoteCount = item.VoteCount,
            DateSemantics = new PremiereDateSemantics(
                premiereDate,
                usedReleaseDate ? PremiereDateSourceKind.TmdbMovieReleaseDate : PremiereDateSourceKind.TmdbMoviePrimaryReleaseDate,
                PremiereDataConfidence.Medium,
                usedReleaseDate ? "TMDb movie release date." : "TMDb primary release date."),
            MergeContributions = [PremiereDiagnosticsFactory.TmdbContribution(PremiereMediaType.Movie, item.Id)]
        };
    }

    private async Task<PremiereItem?> MapSeriesAsync(
        TmdbTvDiscoverItem item,
        CancellationToken cancellationToken,
        bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem>? cachedEnrichment = null,
        DateOnly? premiereDateOverride = null,
        PremiereItemType? itemTypeOverride = null,
        string? episodeTitle = null,
        int? seasonNumber = null,
        int? episodeNumber = null,
        string? episodeSource = null,
        bool allowWatchmodeAvailabilityFallback = true,
        DateOnly? requestedStart = null,
        DateOnly? requestedEnd = null,
        bool canonicalizeSeriesPremiereDate = false)
    {
        var itemType = itemTypeOverride ?? PremiereItemType.SeriesPremiere;
        if (item.Id <= 0
            || string.IsNullOrWhiteSpace(item.Name))
        {
            return null;
        }

        DateOnly premiereDate;
        if (premiereDateOverride is { } dateOverride)
        {
            premiereDate = dateOverride;
        }
        else if (!TryParseTmdbDate(item.FirstAirDate, out premiereDate))
        {
            return null;
        }

        if (canonicalizeSeriesPremiereDate)
        {
            var canonicalDate = await GetSeasonOneEpisodeOneDateAsync(item.Id, cancellationToken, forceRefresh);
            if (canonicalDate is { } seasonOneEpisodeOneDate)
            {
                premiereDate = seasonOneEpisodeOneDate;
            }

            if (requestedStart is { } start && premiereDate < start)
            {
                return null;
            }

            if (requestedEnd is { } end && premiereDate > end)
            {
                return null;
            }
        }

        var discoveredItem = MapSeriesMetadata(
            item,
            premiereDate,
            itemType,
            episodeTitle,
            seasonNumber,
            episodeNumber,
            episodeSource);
        if (discoveredItem is not null && TryReuseCachedEnrichment(cachedEnrichment, discoveredItem, out var cachedItem))
        {
            return cachedItem;
        }

        var details = await TryGetDetailsAsync(
            () => _tmdbClient.GetTvDetailsAsync(item.Id, cancellationToken, forceRefresh),
            PremiereMediaType.Series,
            item.Id,
            cancellationToken);

        var ratingsTask = GetExternalRatingsAsync(details?.ExternalIds?.ImdbId, cancellationToken, forceRefresh);
        var tvmazeTask = GetTvSeriesEnrichmentAsync(details?.ExternalIds, item.Name, cancellationToken, forceRefresh);
        await Task.WhenAll(ratingsTask, tvmazeTask);
        var ratings = await ratingsTask;
        var tvmaze = await tvmazeTask;
        var rottenTomatoesScores = await GetRottenTomatoesScoresAsync(
            PremiereMediaType.Series,
            item.Name,
            premiereDate.Year,
            details?.ExternalIds?.WikidataId,
            cancellationToken,
            forceRefresh);
        var bestBackdropPath = CoalesceText(
            item.BackdropPath,
            details?.BackdropPath,
            SelectBestImagePath(details?.Images?.Backdrops));
        var tmdbPosterUrl = BuildImageUrl(
            _options.PosterSize,
            CoalesceText(item.PosterPath, details?.PosterPath, SelectBestImagePath(details?.Images?.Posters)));
        var tmdbBackdropUrl = BuildImageUrl(_options.BackdropSize, bestBackdropPath);
        var artwork = await ResolveArtworkAsync(
            tmdbPosterUrl,
            ratings.PosterUrl,
            tvmaze.ImageUrl,
            tmdbBackdropUrl,
            new ArtworkRequest(
                PremiereMediaType.Series,
                item.Id,
                details?.ExternalIds?.ImdbId,
                details?.ExternalIds?.TvdbId,
                details?.ExternalIds?.WikidataId,
                item.Name),
            cancellationToken,
            forceRefresh);
        var baseSources = SourceEntries(details, _options.SourceRegions, tvmaze.NetworkName, tvmaze.WebChannelName);
        var sources = allowWatchmodeAvailabilityFallback
            ? await SourceEntriesWithWatchmodeFallbackAsync(
                baseSources,
                PremiereMediaType.Series,
                item.Id,
                details?.ExternalIds?.ImdbId,
                cancellationToken,
                forceRefresh)
            : baseSources;
        var tmdbRuntime = details?.EpisodeRunTime.FirstOrDefault(runtime => runtime > 0);
        var dateSemantics = BuildSeriesDateSemantics(
            itemType,
            premiereDate,
            premiereDateOverride,
            canonicalizeSeriesPremiereDate,
            episodeSource);

        return new PremiereItem
        {
            CanonicalId = itemType == PremiereItemType.SeriesEpisode
                ? PremiereIdentity.SeriesEpisodeCanonicalId(item.Id, premiereDate, seasonNumber, episodeNumber)
                : PremiereIdentity.CanonicalId(PremiereMediaType.Series, item.Id),
            Type = itemType,
            MediaType = PremiereMediaType.Series,
            TmdbId = item.Id,
            ImdbId = details?.ExternalIds?.ImdbId,
            TvdbId = details?.ExternalIds?.TvdbId,
            WikidataId = details?.ExternalIds?.WikidataId,
            Title = CoalesceText(item.Name, details?.Name, details?.OriginalName) ?? item.Name,
            OriginalTitle = CoalesceText(item.OriginalName, details?.OriginalName),
            PremiereDate = premiereDate,
            Overview = CoalesceText(item.Overview, details?.Overview, ratings.Plot, tvmaze.Summary),
            PosterUrl = artwork?.Url,
            BackdropUrl = tmdbBackdropUrl,
            ImageSource = artwork?.Source,
            TrailerUrl = _trailerSelector.SelectBestYouTubeTrailer(details?.Videos?.Results),
            TmdbUrl = $"https://www.themoviedb.org/tv/{item.Id}",
            ImdbUrl = BuildImdbUrl(details?.ExternalIds?.ImdbId),
            OriginalLanguage = CoalesceText(item.OriginalLanguage, details?.OriginalLanguage) ?? "",
            OriginCountries = OriginCountriesOrFallback(details, item.OriginCountry),
            SourceNames = SourceNames(sources),
            Sources = sources,
            GenreIds = GenreIdsOrFallback(details, item.GenreIds),
            Genres = GenreNames(details),
            Keywords = KeywordNames(details?.Keywords),
            Certifications = TvCertifications(details, _options.SourceRegions),
            TvStatus = details?.Status,
            TvType = details?.TvType,
            EpisodeTitle = episodeTitle,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            EpisodeSource = episodeSource,
            RuntimeMinutes = tmdbRuntime is > 0 ? tmdbRuntime : tvmaze.AverageRuntimeMinutes,
            TmdbScore = item.VoteAverage,
            TmdbVoteCount = item.VoteCount,
            ImdbScore = ratings.ImdbScore,
            ImdbVoteCount = ratings.ImdbVoteCount,
            RottenTomatoesScore = ratings.RottenTomatoesScore ?? rottenTomatoesScores.CriticScore,
            RottenTomatoesAudienceScore = ratings.RottenTomatoesAudienceScore ?? rottenTomatoesScores.AudienceScore,
            MetacriticScore = ratings.MetacriticScore,
            DateSemantics = dateSemantics,
            MergeContributions = [PremiereDiagnosticsFactory.TmdbContribution(PremiereMediaType.Series, item.Id)],
            NetworkName = tvmaze.NetworkName,
            WebChannelName = tvmaze.WebChannelName,
            TvmazeAverageRuntimeMinutes = tvmaze.AverageRuntimeMinutes,
            TvmazeRating = tvmaze.TvmazeRating,
            OfficialSiteUrl = tvmaze.OfficialSiteUrl,
            TvmazeUrl = tvmaze.TvmazeUrl
        };
    }

    private async Task<PremiereItem?> MapMovieAsync(
        TmdbMovieDiscoverItem item,
        CancellationToken cancellationToken,
        bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem>? cachedEnrichment = null,
        bool allowWatchmodeAvailabilityFallback = true)
    {
        var releaseDateValue = CoalesceText(item.ReleaseDate, item.PrimaryReleaseDate);
        if (item.Id <= 0
            || string.IsNullOrWhiteSpace(item.Title)
            || !TryParseTmdbDate(releaseDateValue, out var premiereDate))
        {
            return null;
        }

        var discoveredItem = MapMovieMetadata(item);
        if (discoveredItem is not null && TryReuseCachedEnrichment(cachedEnrichment, discoveredItem, out var cachedItem))
        {
            return cachedItem;
        }

        var details = await TryGetDetailsAsync(
            () => _tmdbClient.GetMovieDetailsAsync(item.Id, cancellationToken, forceRefresh),
            PremiereMediaType.Movie,
            item.Id,
            cancellationToken);

        var ratingsTask = GetExternalRatingsAsync(details?.ExternalIds?.ImdbId, cancellationToken, forceRefresh);
        var sourcesTask = allowWatchmodeAvailabilityFallback
            ? SourceEntriesWithWatchmodeFallbackAsync(
                SourceEntries(details, _options.SourceRegions),
                PremiereMediaType.Movie,
                item.Id,
                details?.ExternalIds?.ImdbId,
                cancellationToken,
                forceRefresh)
            : Task.FromResult(SourceEntries(details, _options.SourceRegions));
        var ratings = await ratingsTask;
        var rottenTomatoesScores = await GetRottenTomatoesScoresAsync(
            PremiereMediaType.Movie,
            item.Title,
            premiereDate.Year,
            details?.ExternalIds?.WikidataId,
            cancellationToken,
            forceRefresh);
        var bestBackdropPath = CoalesceText(
            item.BackdropPath,
            details?.BackdropPath,
            SelectBestImagePath(details?.Images?.Backdrops));
        var tmdbPosterUrl = BuildImageUrl(
            _options.PosterSize,
            CoalesceText(item.PosterPath, details?.PosterPath, SelectBestImagePath(details?.Images?.Posters)));
        var tmdbBackdropUrl = BuildImageUrl(_options.BackdropSize, bestBackdropPath);
        var artwork = await ResolveArtworkAsync(
            tmdbPosterUrl,
            ratings.PosterUrl,
            null,
            tmdbBackdropUrl,
            new ArtworkRequest(
                PremiereMediaType.Movie,
                item.Id,
                details?.ExternalIds?.ImdbId,
                details?.ExternalIds?.TvdbId,
                details?.ExternalIds?.WikidataId,
                item.Title),
            cancellationToken,
            forceRefresh);
        var sources = await sourcesTask;
        var usedReleaseDate = !string.IsNullOrWhiteSpace(item.ReleaseDate);

        return new PremiereItem
        {
            CanonicalId = PremiereIdentity.CanonicalId(PremiereMediaType.Movie, item.Id),
            Type = PremiereIdentity.ItemType(PremiereMediaType.Movie),
            MediaType = PremiereMediaType.Movie,
            TmdbId = item.Id,
            ImdbId = details?.ExternalIds?.ImdbId,
            WikidataId = details?.ExternalIds?.WikidataId,
            Title = CoalesceText(item.Title, details?.Title, details?.OriginalTitle) ?? item.Title,
            OriginalTitle = CoalesceText(item.OriginalTitle, details?.OriginalTitle),
            PremiereDate = premiereDate,
            Overview = CoalesceText(item.Overview, details?.Overview, ratings.Plot),
            PosterUrl = artwork?.Url,
            BackdropUrl = tmdbBackdropUrl,
            ImageSource = artwork?.Source,
            TrailerUrl = _trailerSelector.SelectBestYouTubeTrailer(details?.Videos?.Results),
            TmdbUrl = $"https://www.themoviedb.org/movie/{item.Id}",
            ImdbUrl = BuildImdbUrl(details?.ExternalIds?.ImdbId),
            OriginalLanguage = CoalesceText(item.OriginalLanguage, details?.OriginalLanguage) ?? "",
            OriginCountries = ProductionCountriesOrFallback(details, item.OriginCountry),
            SourceNames = SourceNames(sources),
            Sources = sources,
            GenreIds = GenreIdsOrFallback(details, item.GenreIds),
            Genres = GenreNames(details),
            Keywords = KeywordNames(details?.Keywords),
            MovieReleaseTypes = MovieReleaseTypes(details, _options.SourceRegions),
            Certifications = MovieCertifications(details, _options.SourceRegions),
            RuntimeMinutes = details?.Runtime,
            TmdbScore = item.VoteAverage,
            TmdbVoteCount = item.VoteCount,
            ImdbScore = ratings.ImdbScore,
            ImdbVoteCount = ratings.ImdbVoteCount,
            RottenTomatoesScore = ratings.RottenTomatoesScore ?? rottenTomatoesScores.CriticScore,
            RottenTomatoesAudienceScore = ratings.RottenTomatoesAudienceScore ?? rottenTomatoesScores.AudienceScore,
            MetacriticScore = ratings.MetacriticScore,
            DateSemantics = new PremiereDateSemantics(
                premiereDate,
                usedReleaseDate ? PremiereDateSourceKind.TmdbMovieReleaseDate : PremiereDateSourceKind.TmdbMoviePrimaryReleaseDate,
                PremiereDataConfidence.Medium,
                usedReleaseDate ? "TMDb movie release date." : "TMDb primary release date."),
            MergeContributions = [PremiereDiagnosticsFactory.TmdbContribution(PremiereMediaType.Movie, item.Id)]
        };
    }

    private static PremiereDateSemantics BuildSeriesDateSemantics(
        PremiereItemType itemType,
        DateOnly premiereDate,
        DateOnly? premiereDateOverride,
        bool canonicalizedSeasonOneEpisodeOne,
        string? episodeSource)
    {
        if (itemType == PremiereItemType.SeriesEpisode)
        {
            return new PremiereDateSemantics(
                premiereDate,
                string.IsNullOrWhiteSpace(episodeSource)
                    ? PremiereDateSourceKind.TmdbEpisodeAirDate
                    : PremiereDateSourceKind.ExternalProviderDate,
                string.IsNullOrWhiteSpace(episodeSource)
                    ? PremiereDataConfidence.Medium
                    : PremiereDataConfidence.High,
                string.IsNullOrWhiteSpace(episodeSource)
                    ? "TMDb episode air-date discovery."
                    : $"Episode date from {episodeSource}.");
        }

        if (canonicalizedSeasonOneEpisodeOne || premiereDateOverride is not null)
        {
            return new PremiereDateSemantics(
                premiereDate,
                PremiereDateSourceKind.TmdbSeasonOneEpisodeOne,
                PremiereDataConfidence.High,
                "Season 1 episode 1 air date from TMDb.");
        }

        return new PremiereDateSemantics(
            premiereDate,
            PremiereDateSourceKind.TmdbFirstAirDate,
            PremiereDataConfidence.Medium,
            "TMDb first air date.");
    }

    private async Task<TmdbDetailsWithExtras?> TryGetDetailsAsync(
        Func<Task<TmdbDetailsWithExtras?>> getDetails,
        PremiereMediaType mediaType,
        int tmdbId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await getDetails();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Skipping TMDb detail enrichment for {MediaType} {TmdbId} after a request timeout.", mediaType, tmdbId);
            return null;
        }
        catch (ExternalApiException ex)
        {
            _logger.LogWarning(ex, "Skipping TMDb detail enrichment for {MediaType} {TmdbId}.", mediaType, tmdbId);
            return null;
        }
    }

    private async Task<DateOnly?> GetSeasonOneEpisodeOneDateAsync(
        int tmdbId,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var season = await TryGetSeasonDetailsAsync(
            () => _tmdbClient.GetTvSeasonDetailsAsync(tmdbId, 1, cancellationToken, forceRefresh),
            tmdbId,
            cancellationToken);
        var episode = season?.Episodes.FirstOrDefault(episode =>
            episode.SeasonNumber == 1 && episode.EpisodeNumber == 1);

        return TryParseTmdbDate(episode?.AirDate, out var airDate) ? airDate : null;
    }

    private async Task<TmdbSeasonDetails?> TryGetSeasonDetailsAsync(
        Func<Task<TmdbSeasonDetails?>> getDetails,
        int tmdbId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await getDetails();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Skipping TMDb season detail enrichment for series {TmdbId} after a request timeout.", tmdbId);
            return null;
        }
        catch (ExternalApiException ex)
        {
            _logger.LogWarning(ex, "Skipping TMDb season detail enrichment for series {TmdbId}.", tmdbId);
            return null;
        }
    }

    private async Task<ExternalRatings> GetExternalRatingsAsync(string? imdbId, CancellationToken cancellationToken, bool forceRefresh)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return new ExternalRatings(null, null);
        }

        ImdbRatingRecord? imdbRating = null;
        if (_imdbRatingsStore is not null)
        {
            try
            {
                imdbRating = await _imdbRatingsStore.GetByImdbIdAsync(imdbId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Skipping IMDb dataset rating lookup for IMDb ID {ImdbId}.", imdbId);
            }
        }

        try
        {
            var omdbItem = await _omdbClient.GetByImdbIdAsync(imdbId, cancellationToken, forceRefresh);
            var omdbRatings = _ratingMapper.Map(omdbItem);
            return MergeExternalRatings(imdbRating, omdbRatings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Skipping OMDb ratings enrichment for IMDb ID {ImdbId}.", imdbId);
            return MergeExternalRatings(imdbRating, new ExternalRatings(null, null));
        }
    }

    private async Task<RottenTomatoesScores> GetRottenTomatoesScoresAsync(
        PremiereMediaType mediaType,
        string? title,
        int? year,
        string? wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (_rottenTomatoesClient is null || string.IsNullOrWhiteSpace(title))
        {
            return RottenTomatoesScores.Empty;
        }

        try
        {
            return await _rottenTomatoesClient.GetScoresAsync(
                mediaType,
                title,
                year,
                wikidataId,
                cancellationToken,
                forceRefresh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Skipping Rotten Tomatoes enrichment for {MediaType} {Title}.", mediaType, title);
            return RottenTomatoesScores.Empty;
        }
    }

    private static ExternalRatings MergeExternalRatings(ImdbRatingRecord? imdbRating, ExternalRatings omdbRatings)
    {
        return imdbRating is null
            ? omdbRatings
            : omdbRatings with
            {
                ImdbScore = imdbRating.AverageRating,
                ImdbVoteCount = imdbRating.VoteCount
            };
    }

    private async Task<ArtworkCandidate?> ResolveArtworkAsync(
        string? tmdbPosterUrl,
        string? omdbPosterUrl,
        string? tvmazeEnrichmentImageUrl,
        string? tmdbBackdropUrl,
        ArtworkRequest request,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var knownCover = ArtworkResolver.ResolveKnownCover(
            tmdbPosterUrl,
            omdbPosterUrl,
            tvmazeEnrichmentImageUrl);
        if (knownCover is not null)
        {
            return knownCover;
        }

        foreach (var provider in _artworkProviders)
        {
            var candidate = await GetArtworkCandidateFromProviderAsync(provider, request, cancellationToken, forceRefresh);
            if (candidate is not null && !string.IsNullOrWhiteSpace(candidate.Url))
            {
                return candidate;
            }
        }

        return string.IsNullOrWhiteSpace(tmdbBackdropUrl)
            ? null
            : new ArtworkCandidate(tmdbBackdropUrl, "TMDb backdrop");
    }

    private async Task<ArtworkCandidate?> GetArtworkCandidateFromProviderAsync(
        IArtworkProvider provider,
        ArtworkRequest request,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        try
        {
            return await provider.GetArtworkAsync(request, cancellationToken, forceRefresh);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Skipping artwork provider {ProviderType} for {MediaType} {TmdbId} after a request timeout.",
                provider.GetType().Name,
                request.MediaType,
                request.TmdbId);

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Skipping artwork provider {ProviderType} for {MediaType} {TmdbId}.",
                provider.GetType().Name,
                request.MediaType,
                request.TmdbId);

            return null;
        }
    }

    private async Task<TvSeriesEnrichment> GetTvSeriesEnrichmentAsync(
        TmdbExternalIds? externalIds,
        string title,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        TvmazeShow? show = null;
        TvmazeShow? imageShow = null;
        if (externalIds?.TvdbId is not null || !string.IsNullOrWhiteSpace(externalIds?.ImdbId))
        {
            try
            {
                show = await _tvmazeClient.LookupShowAsync(externalIds.TvdbId, externalIds.ImdbId, cancellationToken, forceRefresh);
                if (show is not null && HasTvmazeImage(show))
                {
                    imageShow = show;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Skipping TVmaze lookup enrichment for TVDB ID {TvdbId} / IMDb ID {ImdbId}.",
                    externalIds.TvdbId,
                    externalIds.ImdbId);
            }
        }

        if ((show is null || !HasTvmazeImage(show)) && !string.IsNullOrWhiteSpace(title))
        {
            TvmazeShow? titleMatch = null;
            try
            {
                titleMatch = await _tvmazeClient.SearchShowByNameAsync(title, cancellationToken, forceRefresh);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Skipping TVmaze title enrichment for {Title}.", title);
            }

            if (titleMatch is not null)
            {
                show ??= titleMatch;
                if (HasTvmazeImage(titleMatch))
                {
                    imageShow = titleMatch;
                }
            }
        }

        if (show is null)
        {
            return EmptyTvSeriesEnrichment;
        }

        return new TvSeriesEnrichment(
            show.Network?.Name,
            show.WebChannel?.Name,
            show.AverageRuntime ?? show.Runtime,
            show.Rating?.Average,
            show.OfficialSite,
            show.Url,
            StripHtml(show.Summary),
            imageShow?.Image?.Original ?? imageShow?.Image?.Medium ?? show.Image?.Original ?? show.Image?.Medium);
    }

    private static bool HasTvmazeImage(TvmazeShow show)
    {
        return !string.IsNullOrWhiteSpace(show.Image?.Original)
            || !string.IsNullOrWhiteSpace(show.Image?.Medium);
    }

    private string? BuildImageUrl(string size, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return $"{_options.ImageBaseUrl.TrimEnd('/')}/{size.Trim('/')}/{path.TrimStart('/')}";
    }

    private static string[] ProductionCountriesOrFallback(TmdbDetailsWithExtras? details, string[] fallback)
    {
        var countries = details?.ProductionCountries
            .Select(country => country.Iso31661)
            .Where(country => !string.IsNullOrWhiteSpace(country))
            .Select(country => country!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return countries is { Length: > 0 } ? countries : fallback;
    }

    private static string[] OriginCountriesOrFallback(TmdbDetailsWithExtras? details, string[] fallback)
    {
        var countries = details?.OriginCountry
            .Where(country => !string.IsNullOrWhiteSpace(country))
            .Select(country => country.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return countries is { Length: > 0 } ? countries : fallback;
    }

    private static int[] GenreIdsOrFallback(TmdbDetailsWithExtras? details, int[] fallback)
    {
        var ids = details?.Genres
            .Where(genre => genre.Id > 0)
            .Select(genre => genre.Id)
            .Distinct()
            .ToArray();

        return ids is { Length: > 0 } ? ids : fallback;
    }

    private static string[] GenreNames(TmdbDetailsWithExtras? details)
    {
        return details?.Genres
            .Select(genre => genre.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    private static string[] KeywordNames(TmdbKeywordResponse? keywords)
    {
        return (keywords?.Keywords ?? [])
            .Concat(keywords?.Results ?? [])
            .Select(keyword => keyword.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PremiereSource[] SourceEntries(
        TmdbDetailsWithExtras? details,
        IReadOnlyList<string> sourceRegions,
        string? tvmazeNetworkName = null,
        string? tvmazeWebChannelName = null)
    {
        var sources = new List<PremiereSource>();

        AddSource(sources, tvmazeNetworkName, null, "network");
        AddSource(sources, tvmazeWebChannelName, null, "web");

        foreach (var network in details?.Networks ?? [])
        {
            AddSource(sources, network.Name, network.Id > 0 ? network.Id : null, "network");
        }

        sources.AddRange(WatchProviderEntries(details?.WatchProviders, sourceRegions));

        return sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Name))
            .DistinctBy(source => $"{source.Kind}:{source.Id}:{source.Name}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<PremiereSource[]> SourceEntriesWithWatchmodeFallbackAsync(
        PremiereSource[] sources,
        PremiereMediaType mediaType,
        int tmdbId,
        string? imdbId,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (HasWatchProviderSource(sources))
        {
            return sources;
        }

        IReadOnlyList<PremiereSource> watchmodeSources;
        try
        {
            watchmodeSources = await _watchmodeClient.GetTitleSourcesAsync(
                mediaType,
                tmdbId,
                imdbId,
                _options.SourceRegions,
                cancellationToken,
                forceRefresh);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Skipping Watchmode availability fallback for {MediaType} {TmdbId} after a request timeout.",
                mediaType,
                tmdbId);
            return sources;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Skipping Watchmode availability fallback for {MediaType} {TmdbId}.",
                mediaType,
                tmdbId);
            return sources;
        }

        if (watchmodeSources.Count == 0)
        {
            return sources;
        }

        return sources
            .Concat(watchmodeSources)
            .Where(source => !string.IsNullOrWhiteSpace(source.Name))
            .DistinctBy(source => $"{source.Kind}:{source.Id}:{source.Name}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasWatchProviderSource(IEnumerable<PremiereSource> sources)
    {
        return sources.Any(source => source.Kind.Equals("flatrate", StringComparison.OrdinalIgnoreCase)
            || source.Kind.Equals("free", StringComparison.OrdinalIgnoreCase)
            || source.Kind.Equals("ads", StringComparison.OrdinalIgnoreCase)
            || source.Kind.Equals("buy", StringComparison.OrdinalIgnoreCase)
            || source.Kind.Equals("rent", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] SourceNames(IEnumerable<PremiereSource> sources)
    {
        return sources
            .Select(source => source.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddSource(List<PremiereSource> sources, string? name, int? id, string kind)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            sources.Add(new PremiereSource
            {
                Name = name.Trim(),
                Id = id,
                Kind = kind
            });
        }
    }

    private static IEnumerable<PremiereSource> WatchProviderEntries(
        TmdbWatchProviders? watchProviders,
        IReadOnlyList<string> sourceRegions)
    {
        if (watchProviders?.Results is not { Count: > 0 } results)
        {
            yield break;
        }

        var preferredRegions = PreferredSourceRegions(sourceRegions)
            .Select(region => results.TryGetValue(region, out var providers) ? providers : null)
            .Where(region => region is not null)
            .Select(region => region!)
            .ToArray();

        var regions = preferredRegions.Length > 0
            ? preferredRegions
            : results.OrderBy(region => region.Key, StringComparer.OrdinalIgnoreCase).Select(region => region.Value);

        foreach (var region in regions)
        {
            foreach (var provider in WatchProvidersFor(region))
            {
                yield return provider;
            }
        }
    }

    private static string[] PreferredSourceRegions(IReadOnlyList<string> sourceRegions)
    {
        return sourceRegions
            .Where(region => !string.IsNullOrWhiteSpace(region))
            .Select(region => region.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<PremiereSource> WatchProvidersFor(TmdbWatchProviderRegion region)
    {
        return ProviderEntries(region.Flatrate, "flatrate")
            .Concat(ProviderEntries(region.Free, "free"))
            .Concat(ProviderEntries(region.Ads, "ads"))
            .Concat(ProviderEntries(region.Buy, "buy"))
            .Concat(ProviderEntries(region.Rent, "rent"));
    }

    private static IEnumerable<PremiereSource> ProviderEntries(IEnumerable<TmdbWatchProvider> providers, string kind)
    {
        return OrderedProviders(providers)
            .Select(provider => new PremiereSource
            {
                Name = provider.ProviderName!,
                Id = provider.ProviderId > 0 ? provider.ProviderId : null,
                Kind = kind
            });
    }

    private static IEnumerable<TmdbWatchProvider> OrderedProviders(IEnumerable<TmdbWatchProvider> providers)
    {
        return providers
            .Where(provider => !string.IsNullOrWhiteSpace(provider.ProviderName))
            .OrderBy(provider => provider.DisplayPriority ?? int.MaxValue)
            .ThenBy(provider => provider.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    private static int[] MovieReleaseTypes(TmdbDetailsWithExtras? details, IReadOnlyList<string> sourceRegions)
    {
        return PreferredMovieReleaseDateRegions(details?.ReleaseDates, sourceRegions)
            .SelectMany(region => region.ReleaseDates)
            .Where(releaseDate => releaseDate.Type > 0)
            .Select(releaseDate => releaseDate.Type)
            .Distinct()
            .Order()
            .ToArray();
    }

    private static string[] MovieCertifications(TmdbDetailsWithExtras? details, IReadOnlyList<string> sourceRegions)
    {
        return PreferredMovieReleaseDateRegions(details?.ReleaseDates, sourceRegions)
            .SelectMany(region => region.ReleaseDates.Select(releaseDate => CertificationValue(region.Iso31661, releaseDate.Certification)))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] TvCertifications(TmdbDetailsWithExtras? details, IReadOnlyList<string> sourceRegions)
    {
        if (details?.ContentRatings?.Results is not { Count: > 0 } results)
        {
            return [];
        }

        var preferredRegions = PreferredSourceRegions(sourceRegions);
        var preferred = preferredRegions.Length == 0
            ? Array.Empty<TmdbTvContentRating>()
            : preferredRegions
                .SelectMany(region => results.Where(rating => string.Equals(rating.Iso31661, region, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

        var ratings = preferred.Length > 0 ? preferred : results.ToArray();
        return ratings
            .Select(rating => CertificationValue(rating.Iso31661, rating.Rating))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<TmdbMovieReleaseDateRegion> PreferredMovieReleaseDateRegions(
        TmdbMovieReleaseDateResponse? releaseDates,
        IReadOnlyList<string> sourceRegions)
    {
        if (releaseDates?.Results is not { Count: > 0 } results)
        {
            return [];
        }

        var preferredRegions = PreferredSourceRegions(sourceRegions);
        var preferred = preferredRegions.Length == 0
            ? Array.Empty<TmdbMovieReleaseDateRegion>()
            : preferredRegions
                .SelectMany(region => results.Where(result => string.Equals(result.Iso31661, region, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

        return preferred.Length > 0
            ? preferred
            : results;
    }

    private static string? CertificationValue(string? region, string? certification)
    {
        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(certification))
        {
            return null;
        }

        return $"{region.Trim().ToUpperInvariant()}:{certification.Trim()}";
    }

    private static string? SelectBestImagePath(IEnumerable<TmdbImage>? images)
    {
        return images?
            .Where(image => !string.IsNullOrWhiteSpace(image.FilePath))
            .OrderByDescending(image => image.VoteCount ?? 0)
            .ThenByDescending(image => image.VoteAverage ?? 0)
            .Select(image => image.FilePath)
            .FirstOrDefault();
    }

    private static string? BuildImdbUrl(string? imdbId)
    {
        return string.IsNullOrWhiteSpace(imdbId)
            ? null
            : $"https://www.imdb.com/title/{Uri.EscapeDataString(imdbId)}/";
    }

    private static string? CoalesceText(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var withoutTags = System.Text.RegularExpressions.Regex.Replace(value, "<.*?>", " ");
        return System.Text.RegularExpressions.Regex.Replace(withoutTags, "\\s+", " ").Trim();
    }

    private static bool TryParseTmdbDate(string? value, out DateOnly date)
    {
        return DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static readonly TvSeriesEnrichment EmptyTvSeriesEnrichment = new(null, null, null, null, null, null, null, null);

    private sealed record SharedMediaCacheSnapshot(
        IReadOnlyList<PremiereItem>? SeriesItems,
        IReadOnlyList<PremiereItem>? MovieItems)
    {
        public bool HasSeries => SeriesItems is not null;
        public bool HasMovies => MovieItems is not null;
        public bool HasAny => HasSeries || HasMovies;
        public IReadOnlyList<PremiereItem> Items => (SeriesItems ?? [])
            .Concat(MovieItems ?? [])
            .ToArray();
    }

    private sealed class ActivePremiereSource(
        string key,
        string providerKey,
        IAsyncEnumerator<PremiereSourceBatch> enumerator,
        Task<bool> moveNextTask)
    {
        public string Key { get; } = key;
        public string ProviderKey { get; } = providerKey;
        public IAsyncEnumerator<PremiereSourceBatch> Enumerator { get; } = enumerator;
        public Task<bool> MoveNextTask { get; set; } = moveNextTask;
    }

    private static async ValueTask DisposeActiveSourceAsync(
        ActivePremiereSource source,
        CancellationToken cancellationToken)
    {
        if (!source.MoveNextTask.IsCompleted)
        {
            try
            {
                await source.MoveNextTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (Exception ex) when (IsExpectedSourceShutdownException(ex))
            {
            }
        }

        try
        {
            await source.Enumerator.DisposeAsync();
        }
        catch (Exception ex) when (IsExpectedSourceShutdownException(ex))
        {
        }
    }

    private static bool IsExpectedSourceShutdownException(Exception ex)
    {
        return ex is OperationCanceledException
            or ObjectDisposedException
            or NotSupportedException;
    }

    private sealed record PremiereSourceFactory(
        string Key,
        string Name,
        DateOnly Start,
        DateOnly End,
        Func<IAsyncEnumerable<PremiereSourceBatch>> Open);

    private sealed record PremiereItemBatch(
        IReadOnlyList<PremiereItem> Items,
        int? CompletedWork = null,
        int? TotalWork = null,
        string? ProgressText = null,
        int? UnmappedCount = null);

    private sealed record PremiereSourceBatch(
        string Name,
        IReadOnlyList<PremiereItem> Items,
        Exception? Error = null,
        int? CompletedWork = null,
        int? TotalWork = null,
        string? ProgressText = null,
        long? ElapsedMilliseconds = null,
        bool IsComplete = false,
        int? UnmappedCount = null,
        int? FilteredCount = null);
}
