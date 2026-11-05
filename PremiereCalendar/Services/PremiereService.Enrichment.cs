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
            EnrichmentVersion = _itemCacheSession.Value?.Find(new ItemEnrichmentIdentity(PremiereMediaType.Series, item.Id))?.Version,
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
        // Keep the reservation that preceded the awaited date validation. A later
        // flush can accept a new generation; stamping that version would hide a race.
        var reservedVersion = _itemCacheSession.Value?.Find(new ItemEnrichmentIdentity(PremiereMediaType.Series, item.Id))?.Version;
        var premiereDate = await GetSeasonOneEpisodeOneDateAsync(item.Id, cancellationToken, forceRefresh);
        if (premiereDate is { } canonicalDate
            && (canonicalDate < requestedStart || canonicalDate > requestedEnd))
        {
            return null;
        }

        var mapped = MapSeriesMetadata(item, premiereDateOverride: premiereDate);
        return mapped is null ? null : mapped with { EnrichmentVersion = reservedVersion };
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
            EnrichmentVersion = _itemCacheSession.Value?.Find(new ItemEnrichmentIdentity(PremiereMediaType.Movie, item.Id))?.Version,
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

    private async Task<PremiereItem?> MapSeriesUncachedCoreAsync(
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
            () => _tmdbClient.GetTvDetailsAsync(item.Id, cancellationToken, forceRefresh || _itemEnrichmentAttempt.Value?.ForceMetadata == true),
            PremiereMediaType.Series,
            item.Id,
            cancellationToken);
        if (details is null && discoveredItem is not null && _itemEnrichmentAttempt.Value?.PreviousMetadata is { } previousMetadata)
            return await RefreshItemRatingsAsync(previousMetadata.Apply(discoveredItem), cancellationToken);

        var startedTasks = new List<Task>(5);
        try
        {
        var ratingsTask = GetExternalRatingsAsync(details?.ExternalIds?.ImdbId, cancellationToken, forceRefresh);
        var tvmazeTask = GetTvSeriesEnrichmentAsync(details?.ExternalIds, item.Name, cancellationToken, forceRefresh);
        var rottenTomatoesTask = GetRottenTomatoesScoresAsync(
            PremiereMediaType.Series,
            item.Name,
            premiereDate.Year,
            details?.ExternalIds?.WikidataId,
            cancellationToken,
            forceRefresh);
        startedTasks.AddRange([ratingsTask, tvmazeTask, rottenTomatoesTask]);
        await Task.WhenAll(ratingsTask, tvmazeTask);
        var ratings = await ratingsTask;
        var tvmaze = await tvmazeTask;
        var bestBackdropPath = CoalesceText(
            item.BackdropPath,
            details?.BackdropPath,
            SelectBestImagePath(details?.Images?.Backdrops));
        var tmdbPosterUrl = BuildImageUrl(
            _options.PosterSize,
            CoalesceText(item.PosterPath, details?.PosterPath, SelectBestImagePath(details?.Images?.Posters)));
        var tmdbBackdropUrl = BuildImageUrl(_options.BackdropSize, bestBackdropPath);
        if (details is null && tmdbBackdropUrl is null && _itemEnrichmentAttempt.Value is { PreviousMetadata.BackdropUrl: { } previousBackdrop } backdropAttempt)
        {
            tmdbBackdropUrl = previousBackdrop;
            backdropAttempt.BackdropReused = true;
        }
        var artworkTask = ResolveArtworkAsync(
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
        startedTasks.Add(artworkTask);
        var baseSources = EnrichmentSourceEntries(details, _options.SourceRegions, tvmaze.NetworkName, tvmaze.WebChannelName);
        var sourcesTask = allowWatchmodeAvailabilityFallback
            ? SourceEntriesWithWatchmodeFallbackAsync(
                baseSources,
                PremiereMediaType.Series,
                item.Id,
                details?.ExternalIds?.ImdbId,
                cancellationToken,
                forceRefresh)
            : Task.FromResult(baseSources);
        startedTasks.Add(sourcesTask);
        await Task.WhenAll(artworkTask, sourcesTask, rottenTomatoesTask);
        var artwork = await artworkTask;
        var sources = await sourcesTask;
        var rottenTomatoesScores = await rottenTomatoesTask;
        var tmdbRuntime = details?.EpisodeRunTime.FirstOrDefault(runtime => runtime > 0);
        if (_itemEnrichmentAttempt.Value is { } runtimeAttempt) runtimeAttempt.RuntimeFromTvmaze = tmdbRuntime is not > 0;
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
        catch
        {
            await ObserveStartedEnrichmentTasksAsync(startedTasks);
            throw;
        }
    }

    private async Task<PremiereItem?> MapMovieUncachedCoreAsync(
        TmdbMovieDiscoverItem item,
        CancellationToken cancellationToken,
        bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem>? cachedEnrichment = null,
        bool allowWatchmodeAvailabilityFallback = true,
        MediaDiscoveryCriteria? requestedMovieCriteria = null, bool coreOnly = false)
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

        var details = await TraceEnrichmentStageAsync("movie.details", () => TryGetDetailsAsync(
            () => _tmdbClient.GetMovieDetailsAsync(item.Id, cancellationToken, forceRefresh || _itemEnrichmentAttempt.Value?.ForceMetadata == true),
            PremiereMediaType.Movie,
            item.Id,
            cancellationToken));
        if (details is null && discoveredItem is not null && _itemEnrichmentAttempt.Value?.PreviousMetadata is { } previousMetadata)
            return coreOnly ? previousMetadata.Apply(discoveredItem) : await RefreshItemRatingsAsync(previousMetadata.Apply(discoveredItem), cancellationToken);

        PremiereItem? CoreMetadata()
        {
            if (details is null) return discoveredItem;
            var sources = EnrichmentSourceEntries(details, _options.SourceRegions);
            var poster = BuildImageUrl(_options.PosterSize, CoalesceText(item.PosterPath, details.PosterPath, SelectBestImagePath(details.Images?.Posters)));
            return discoveredItem! with
            {
                ImdbId = details.ExternalIds?.ImdbId, WikidataId = details.ExternalIds?.WikidataId,
                Title = CoalesceText(item.Title, details.Title, details.OriginalTitle) ?? item.Title,
                OriginalTitle = CoalesceText(item.OriginalTitle, details.OriginalTitle),
                Overview = CoalesceText(item.Overview, details.Overview),
                PosterUrl = poster, ImageSource = poster is null ? null : "TMDb poster",
                BackdropUrl = BuildImageUrl(_options.BackdropSize, CoalesceText(item.BackdropPath, details.BackdropPath, SelectBestImagePath(details.Images?.Backdrops))),
                TrailerUrl = _trailerSelector.SelectBestYouTubeTrailer(details.Videos?.Results),
                ImdbUrl = BuildImdbUrl(details.ExternalIds?.ImdbId),
                OriginalLanguage = CoalesceText(item.OriginalLanguage, details.OriginalLanguage) ?? "",
                OriginCountries = ProductionCountriesOrFallback(details, item.OriginCountry),
                Sources = sources, SourceNames = SourceNames(sources), GenreIds = GenreIdsOrFallback(details, item.GenreIds),
                Genres = GenreNames(details), Keywords = KeywordNames(details.Keywords),
                MovieReleaseTypes = MovieReleaseTypes(details, _options.SourceRegions),
                Certifications = MovieCertifications(details, _options.SourceRegions), RuntimeMinutes = details.Runtime
            };
        }
        if (coreOnly) return CoreMetadata();

        if (requestedMovieCriteria is not null)
        {
            var originalLanguage = CoalesceText(item.OriginalLanguage, details?.OriginalLanguage);
            var hasLanguageMismatch = requestedMovieCriteria.OriginalLanguages.Length > 0
                && !string.IsNullOrWhiteSpace(originalLanguage)
                && !requestedMovieCriteria.OriginalLanguages.Any(language => string.Equals(
                    originalLanguage,
                    language.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            var runtimeMinutes = details?.Runtime;
            var hasRuntimeFilter = requestedMovieCriteria.RuntimeMinMinutes > 0
                || requestedMovieCriteria.RuntimeMaxMinutes < 360;
            var hasRuntimeMismatch = hasRuntimeFilter
                && runtimeMinutes is > 0
                && (runtimeMinutes < requestedMovieCriteria.RuntimeMinMinutes
                    || requestedMovieCriteria.RuntimeMaxMinutes < 360
                        && runtimeMinutes > requestedMovieCriteria.RuntimeMaxMinutes);

            if (hasLanguageMismatch || hasRuntimeMismatch)
            {
                _telemetry.RecordDiagnostic("enrichment.skipped", details: new()
                {
                    Stage = "movie",
                    Outcome = "skipped",
                    Reason = "request_filter",
                    ItemId = item.Id
                });
                if (_itemEnrichmentAttempt.Value is { MetadataSucceeded: true } rejected)
                {
                    rejected.CoreMetadataOnly = true;
                    return CoreMetadata();
                }
                return null;
            }
        }

        var startedTasks = new List<Task>(4);
        try
        {
            var ratingsTask = TraceEnrichmentStageAsync("movie.ratings",
                () => GetExternalRatingsAsync(details?.ExternalIds?.ImdbId, cancellationToken, forceRefresh));
            startedTasks.Add(ratingsTask);
            var rottenTomatoesTask = TraceEnrichmentStageAsync("movie.rotten_tomatoes", () => GetRottenTomatoesScoresAsync(
                PremiereMediaType.Movie,
                item.Title,
                premiereDate.Year,
                details?.ExternalIds?.WikidataId,
                cancellationToken,
                forceRefresh));
            startedTasks.Add(rottenTomatoesTask);
            var sourcesTask = allowWatchmodeAvailabilityFallback
                ? SourceEntriesWithWatchmodeFallbackAsync(
                    EnrichmentSourceEntries(details, _options.SourceRegions),
                    PremiereMediaType.Movie,
                    item.Id,
                    details?.ExternalIds?.ImdbId,
                    cancellationToken,
                    forceRefresh)
                : Task.FromResult(EnrichmentSourceEntries(details, _options.SourceRegions));
            startedTasks.Add(sourcesTask);

            // Rotten Tomatoes and availability are independent of OMDb ratings. Start
            // both before waiting for ratings so their latency overlaps.
            var ratings = await ratingsTask;
            var bestBackdropPath = CoalesceText(
                item.BackdropPath,
                details?.BackdropPath,
                SelectBestImagePath(details?.Images?.Backdrops));
            var tmdbPosterUrl = BuildImageUrl(
                _options.PosterSize,
                CoalesceText(item.PosterPath, details?.PosterPath, SelectBestImagePath(details?.Images?.Posters)));
            var tmdbBackdropUrl = BuildImageUrl(_options.BackdropSize, bestBackdropPath);
            if (details is null && tmdbBackdropUrl is null && _itemEnrichmentAttempt.Value is { PreviousMetadata.BackdropUrl: { } previousBackdrop } backdropAttempt)
            {
                tmdbBackdropUrl = previousBackdrop;
                backdropAttempt.BackdropReused = true;
            }
            var artworkTask = TraceEnrichmentStageAsync("movie.artwork", () => ResolveArtworkAsync(
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
                forceRefresh));
            startedTasks.Add(artworkTask);

            // Artwork needs the resolved ratings poster, but does not depend on
            // Rotten Tomatoes. Await all remaining branches together and observe
            // every started branch if the caller cancels or one branch fails.
            await Task.WhenAll(rottenTomatoesTask, sourcesTask, artworkTask);
            var rottenTomatoesScores = await rottenTomatoesTask;
            var artwork = await artworkTask;
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
        catch
        {
            await ObserveStartedEnrichmentTasksAsync(startedTasks);
            throw;
        }
    }

    private static async Task ObserveStartedEnrichmentTasksAsync(IReadOnlyCollection<Task> tasks)
    {
        if (tasks.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Preserve the original failure while ensuring all sibling tasks have
            // completed and their exceptions have been observed.
        }
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
        var started = Stopwatch.GetTimestamp();
        try
        {
            var details = await getDetails();
            if (details is not null && details.Id == tmdbId && _itemEnrichmentAttempt.Value is { } attempt)
                attempt.MetadataSucceeded = true;
            else if (_itemEnrichmentAttempt.Value is { } failed) failed.MetadataFailed = true;
            RecordItemComponent("tmdb", "metadata", details is not null && details.Id == tmdbId ? "provider_fetch_success" : "preserved_failed",
                started, itemId: tmdbId, mediaType: mediaType);
            return details;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (_itemEnrichmentAttempt.Value is { } attempt) attempt.MetadataFailed = true;
            RecordItemComponent("tmdb", "metadata", "preserved_failed", started, "timeout", itemId: tmdbId, mediaType: mediaType);
            _logger.LogWarning(ex, "Skipping TMDb detail enrichment for {MediaType} {TmdbId} after a request timeout.", mediaType, tmdbId);
            return null;
        }
        catch (ExternalApiException ex)
        {
            if (_itemEnrichmentAttempt.Value is { } attempt) attempt.MetadataFailed = true;
            RecordItemComponent("tmdb", "metadata", "preserved_failed", started, "request_failed", itemId: tmdbId, mediaType: mediaType);
            _logger.LogWarning(ex, "Skipping TMDb detail enrichment for {MediaType} {TmdbId}.", mediaType, tmdbId);
            return null;
        }
    }

    private async Task<DateOnly?> GetSeasonOneEpisodeOneDateAsync(
        int tmdbId,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var started = Stopwatch.GetTimestamp();
        if (_itemCacheSession.Value is { } session)
        {
            var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, tmdbId);
            var cached = session.Find(identity);
            var pendingInvalidation = cached?.Record.MetadataInvalidatedUtc is { } invalidated
                && (cached.Record.MetadataFetchedUtc is null || cached.Record.MetadataFetchedUtc < invalidated);
            if (!forceRefresh && !pendingInvalidation && (session.MetadataFresh(identity) || !session.MetadataRefreshNeeded(identity))
                && cached?.Metadata is { FirstEpisodeDateChecked: true } metadata)
            {
                RecordItemComponent("tmdb", "canonical_date", "reused", started, "checked_date",
                    itemId: tmdbId, mediaType: PremiereMediaType.Series);
                return metadata.FirstEpisodeDate;
            }
            var key = (TmdbId: tmdbId, Generation: cached?.Record.Generation ?? -1);
            var baselineMetadataRevision = cached?.Record.MetadataRevision;
            var baselineMetadataFetchedUtc = cached?.Record.MetadataFetchedUtc;
            var validationTask = session.FirstEpisodeDates.GetOrAdd(key, _ =>
            {
                Lazy<Task<FirstEpisodeValidation>>? flight = null;
                flight = new Lazy<Task<FirstEpisodeValidation>>(async () =>
                {
                    try
                    {
                        var validation = await ValidateFirstEpisodeDateAsync(tmdbId, session.LifetimeToken,
                            forceRefresh || !session.MetadataFresh(identity));
                        return validation with
                        {
                            BaselineMetadataRevision = baselineMetadataRevision,
                            BaselineMetadataFetchedUtc = baselineMetadataFetchedUtc
                        };
                    }
                    catch
                    {
                        // A source can abandon its wait without canceling the session's
                        // provider request. Remove only this failed flight, never its replacement.
                        ((ICollection<KeyValuePair<(int TmdbId, long Generation), Lazy<Task<FirstEpisodeValidation>>>>)session.FirstEpisodeDates)
                            .Remove(new(key, flight!));
                        throw;
                    }
                });
                return flight;
            });
            var joined = validationTask.IsValueCreated;
            FirstEpisodeValidation validation;
            try { validation = await validationTask.Value.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                RecordItemComponent("tmdb", "canonical_date", "deferred", started, "canceled",
                    itemId: tmdbId, mediaType: PremiereMediaType.Series);
                throw;
            }
            catch
            {
                RecordItemComponent("tmdb", "canonical_date", "preserved_failed", started, "validation_failed",
                    itemId: tmdbId, mediaType: PremiereMediaType.Series);
                throw;
            }
            var generationChanged = session.Find(identity)?.Record.Generation != key.Generation;
            if (generationChanged)
                session.MarkDiscoveryUnverified(PremiereMediaType.Series);
            RecordItemComponent("tmdb", "canonical_date", generationChanged ? "deferred"
                    : !validation.Successful ? "preserved_failed" : joined ? "reused" : "provider_fetch_success",
                started, generationChanged ? "metadata_generation_changed" : !validation.Successful ? "validation_unverified" : "checked_date",
                itemId: tmdbId, mediaType: PremiereMediaType.Series);
            session.RememberFirstEpisodeDate(identity, key.Generation, validation);
            if (!validation.Successful && pendingInvalidation) session.MarkDiscoveryUnverified(PremiereMediaType.Series);
            return validation.Date ?? (validation.Successful ? null : cached?.Metadata?.FirstEpisodeDate);
        }
        var uncached = await ValidateFirstEpisodeDateAsync(tmdbId, cancellationToken, forceRefresh);
        RecordItemComponent("tmdb", "canonical_date", uncached.Successful ? "provider_fetch_success" : "preserved_failed",
            started, uncached.Successful ? "checked_date" : "validation_unverified", itemId: tmdbId, mediaType: PremiereMediaType.Series);
        return uncached.Date;
    }

    private async Task<FirstEpisodeValidation> ValidateFirstEpisodeDateAsync(int tmdbId, CancellationToken cancellationToken, bool forceRefresh)
    {
        var season = await TryGetSeasonDetailsAsync(
            () => _tmdbClient.GetTvSeasonDetailsAsync(tmdbId, 1, cancellationToken, forceRefresh),
            tmdbId,
            cancellationToken);
        var episode = season?.Episodes.FirstOrDefault(episode =>
            episode.SeasonNumber == 1 && episode.EpisodeNumber == 1);

        return new(TryParseTmdbDate(episode?.AirDate, out var airDate) ? airDate : null, season is not null);
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
        var attempt = _itemEnrichmentAttempt.Value;
        var started = Stopwatch.GetTimestamp();
        if (attempt is not null && _itemCacheSession.Value is { } session)
        {
            attempt.ExternalIdentity = ItemEnrichmentCacheCoordinator.ItemEnrichmentSession.ComponentIdentity("omdb", imdbId);
            var identity = new ItemEnrichmentIdentity(attempt.MediaType ?? PremiereMediaType.Movie, attempt.ItemId);
            attempt.ExternalRetryAllowed = attempt.ExplicitForceRefresh || session.ComponentRetryAllowed(identity, "omdb", attempt.ExternalIdentity);
            attempt.DatasetRetryAllowed = attempt.ExplicitForceRefresh || session.ComponentRetryAllowed(identity, "imdb_dataset", attempt.ExternalIdentity);
        }
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            if (attempt is not null)
            {
                attempt.ExternalNotRequired = true;
                attempt.DatasetRequired = false;
            }
            RecordItemComponent("omdb", "external_ratings", "reused", started, "no_external_identity");
            return new ExternalRatings(null, null);
        }
        var externalNeeded = attempt?.ExternalNeeded(imdbId) ?? true;
        var dataset = attempt?.PreviousRatings?.Dataset is { } previousDataset
            && string.Equals(previousDataset.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase) ? previousDataset : null;
        if (_imdbRatingsStore is not null && (attempt?.DatasetNeeded(imdbId) ?? true))
        {
            var datasetStarted = Stopwatch.GetTimestamp();
            try
            {
                var rating = await ImdbRatingLookupBatcher.GetAsync(_imdbRatingsStore, imdbId, cancellationToken);
                dataset = new(imdbId, rating?.AverageRating, rating?.VoteCount, attempt?.CurrentUtc ?? DateTimeOffset.UtcNow);
                if (attempt is not null)
                {
                    attempt.DatasetSucceeded = true;
                    attempt.DatasetResult = dataset;
                }
                RecordItemComponent("imdb_dataset", "dataset", rating is null ? "provider_verified_empty" : "provider_fetch_success", datasetStarted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt is not null) attempt.RatingsFailed = true;
                RecordItemComponent("imdb_dataset", "dataset", "preserved_failed", datasetStarted, "lookup_failed");
                _logger.LogWarning(ex, "Skipping IMDb dataset rating lookup for IMDb ID {ImdbId}.", imdbId);
            }
        }
        else if (_imdbRatingsStore is not null)
            RecordItemComponent("imdb_dataset", "dataset", attempt?.RatingsRetryAllowed == false ? "deferred" : "reused", started,
                attempt?.RatingsRetryAllowed == false ? "failure_retry_pending" : null);
        if (!externalNeeded && attempt is not null)
        {
            RecordItemComponent("omdb", "external_ratings", attempt.RatingsRetryAllowed ? "reused" : "deferred", started,
                attempt.RatingsRetryAllowed ? null : "failure_retry_pending");
            var cached = attempt.PreviousRatings;
            var matches = cached?.MatchesImdb(imdbId) == true;
            var values = matches ? cached!.ProviderFreshnessTracked ? cached.ExternalValues
                : new ItemEnrichmentRatingValues(cached.ImdbScore, cached.ImdbVoteCount, cached.RottenTomatoesScore,
                    cached.RottenTomatoesAudienceScore, cached.MetacriticScore) : null;
            var reused = new ExternalRatings(values?.ImdbScore, values?.RottenTomatoesScore,
                PosterUrl: matches ? attempt.PreviousMetadata?.OmdbPosterUrl
                    ?? (attempt.PreviousMetadata?.ImageSource == "OMDb poster" ? attempt.PreviousMetadata.PosterUrl : null) : null,
                Plot: matches ? attempt.PreviousMetadata?.OmdbPlot ?? attempt.PreviousMetadata?.Overview : null,
                ImdbVoteCount: values?.ImdbVoteCount, MetacriticScore: values?.MetacriticScore,
                RottenTomatoesAudienceScore: values?.RottenTomatoesAudienceScore);
            attempt.ExternalReused = matches;
            var merged = MergeExternalRatings(dataset, reused);
            attempt.ExternalResult = merged;
            return merged;
        }
        if (attempt is not null) attempt.ExternalNotRequired = false;
        forceRefresh = attempt is null ? forceRefresh : true;

        try
        {
            var outcome = await _omdbClient.GetByImdbIdWithStatusAsync(imdbId, cancellationToken, forceRefresh);
            var omdbItem = outcome.Value;
            if (outcome.Successful && (outcome.Failure?.Reason is "disabled" or "not_configured" or "not_applicable") && attempt is not null)
            {
                attempt.ExternalNotRequired = true;
                RecordItemComponent("omdb", "external_ratings", "reused", started, "disabled");
                var datasetOnly = MergeExternalRatings(dataset, new ExternalRatings(null, null));
                attempt.ExternalResult = datasetOnly;
                return datasetOnly;
            }
            if (attempt is not null)
            {
                if (outcome.Successful) attempt.ExternalSucceeded = true;
                else attempt.RatingsFailed = true;
            }
            var omdbRatings = _ratingMapper.Map(omdbItem);
            var merged = MergeExternalRatings(dataset, omdbRatings);
            if (attempt is not null)
            {
                attempt.ExternalProviderResult = omdbRatings;
                attempt.ExternalResult = merged;
            }
            RecordItemComponent("omdb", "external_ratings", !outcome.Successful ? "preserved_failed"
                : omdbRatings.ImdbScore is null && omdbRatings.ImdbVoteCount is null && omdbRatings.RottenTomatoesScore is null
                    && omdbRatings.RottenTomatoesAudienceScore is null && omdbRatings.MetacriticScore is null ? "provider_verified_empty" : "provider_fetch_success",
                started, outcome.Failure?.Reason, outcome.Failure?.StatusCode, outcome.Failure?.RetryAtUtc);
            return merged;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (attempt is not null) attempt.RatingsFailed = true;
            RecordItemComponent("omdb", "external_ratings", "preserved_failed", started, "request_failed");
            _logger.LogWarning(ex, "Skipping OMDb ratings enrichment for IMDb ID {ImdbId}.", imdbId);
            var fallback = MergeExternalRatings(dataset, new ExternalRatings(null, null));
            if (attempt is not null) attempt.ExternalResult = fallback;
            return fallback;
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
        var attempt = _itemEnrichmentAttempt.Value;
        var started = Stopwatch.GetTimestamp();
        if (attempt is not null && _itemCacheSession.Value is { } session)
        {
            attempt.TomatoesIdentity = ItemEnrichmentCacheCoordinator.ItemEnrichmentSession.ComponentIdentity("rottentomatoes",
                wikidataId: wikidataId, title: title, year: year);
            attempt.TomatoesRetryAllowed = attempt.ExplicitForceRefresh || session.ComponentRetryAllowed(
                new(attempt.MediaType ?? mediaType, attempt.ItemId), "rottentomatoes", attempt.TomatoesIdentity);
        }
        if (_rottenTomatoesClient is null && attempt is not null)
        {
            attempt.TomatoesNotRequired = true;
            RecordItemComponent("rottentomatoes", "tomatoes", "reused", started, "disabled");
            return RottenTomatoesScores.Empty;
        }
        if (attempt is not null && !attempt.TomatoesNeeded(wikidataId, title, year))
        {
            RecordItemComponent("rottentomatoes", "tomatoes", attempt.RatingsRetryAllowed ? "reused" : "deferred", started,
                attempt.RatingsRetryAllowed ? null : "failure_retry_pending");
            if (attempt.PreviousRatings?.MatchesTomatoes(wikidataId, title, year) != true) return RottenTomatoesScores.Empty;
            attempt.TomatoesReused = true;
            return new(attempt.PreviousRatings?.RottenTomatoesScore, attempt.PreviousRatings?.RottenTomatoesAudienceScore);
        }
        if (attempt is not null) attempt.TomatoesNotRequired = false;
        forceRefresh = attempt is null ? forceRefresh : true;
        if (_rottenTomatoesClient is null || string.IsNullOrWhiteSpace(title))
        {
            if (attempt is not null)
            {
                attempt.TomatoesSucceeded = true;
                attempt.TomatoesResult = RottenTomatoesScores.Empty;
            }
            return RottenTomatoesScores.Empty;
        }

        try
        {
            var outcome = await _rottenTomatoesClient.GetScoresWithRefreshPolicyAsync(
                mediaType,
                title,
                year,
                wikidataId,
                cancellationToken,
                forceRefresh, attempt?.ForceIdentifierRefresh ?? forceRefresh);
            if (outcome.Failure?.Reason is "disabled" or "not_applicable" && attempt is not null)
            {
                attempt.TomatoesNotRequired = true;
                RecordItemComponent("rottentomatoes", "tomatoes", "reused", started, outcome.Failure.Reason);
                return RottenTomatoesScores.Empty;
            }
            if (attempt is not null)
            {
                if (outcome.Successful) attempt.TomatoesSucceeded = true;
                else attempt.RatingsFailed = true;
                attempt.TomatoesResult = outcome.Value;
            }
            RecordItemComponent("rottentomatoes", "tomatoes", !outcome.Successful ? "preserved_failed"
                : outcome.Value.CriticScore is null && outcome.Value.AudienceScore is null ? "provider_verified_empty" : "provider_fetch_success",
                started, outcome.Failure?.Reason, outcome.Failure?.StatusCode, outcome.Failure?.RetryAtUtc);
            return outcome.Value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (attempt is not null) attempt.RatingsFailed = true;
            RecordItemComponent("rottentomatoes", "tomatoes", "preserved_failed", started, "timeout");
            // Optional HTTP deadlines must not discard an otherwise valid discovery
            // batch. Cancellation of the source or caller still propagates unchanged.
            _telemetry.RecordDiagnostic("enrichment.optional.timeout", details: new()
            {
                Provider = "rottentomatoes", Stage = "rotten_tomatoes", Outcome = "skipped", Reason = "dependency_timeout"
            });
            _logger.LogWarning("Skipping Rotten Tomatoes enrichment after its optional request timed out.");
            return RottenTomatoesScores.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (attempt is not null) attempt.RatingsFailed = true;
            RecordItemComponent("rottentomatoes", "tomatoes", "preserved_failed", started, "request_failed");
            _logger.LogWarning(ex, "Skipping Rotten Tomatoes enrichment for {MediaType} {Title}.", mediaType, title);
            return RottenTomatoesScores.Empty;
        }
    }

    private static ExternalRatings MergeExternalRatings(ItemEnrichmentDatasetRating? imdbRating, ExternalRatings omdbRatings)
    {
        return imdbRating?.ImdbScore is null
            ? omdbRatings
            : omdbRatings with
            {
                ImdbScore = imdbRating.ImdbScore,
                ImdbVoteCount = imdbRating.ImdbVoteCount
            };
    }

    private void RecordItemComponent(string provider, string stage, string outcome, long started,
        string? reason = null, int? statusCode = null, DateTimeOffset? retryAtUtc = null, int? itemId = null, PremiereMediaType? mediaType = null)
    {
        var details = new PerformanceDiagnosticDetails
        {
            ItemId = itemId ?? _itemEnrichmentAttempt.Value?.ItemId, Provider = provider, Stage = stage, Outcome = outcome,
            MediaType = (mediaType ?? _itemEnrichmentAttempt.Value?.MediaType)?.ToString(),
            Reason = reason, StatusCode = statusCode, RetryAtUtc = retryAtUtc,
            DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
        };
        if (_itemCacheSession.Value is { } session && details.ItemId is > 0 && _itemEnrichmentAttempt.Value?.MediaType is { } type)
        {
            var identity = new ItemEnrichmentIdentity(type, details.ItemId.Value);
            var component = stage == "artwork" ? "artwork" : provider;
            var componentIdentity = component switch
            {
                "omdb" or "imdb_dataset" => _itemEnrichmentAttempt.Value.ExternalIdentity,
                "rottentomatoes" => _itemEnrichmentAttempt.Value.TomatoesIdentity,
                "tvmaze" => _itemEnrichmentAttempt.Value.TvmazeIdentity,
                _ => null
            };
            if (outcome == "preserved_failed"
                && (reason is not ("local_backoff" or "local_circuit") || retryAtUtc > _itemEnrichmentAttempt.Value.CurrentUtc))
                session.RecordComponentOutcome(identity, component, false, retryAtUtc, componentIdentity);
            else if (outcome is "provider_fetch_success" or "provider_verified_empty"
                && !(stage == "artwork" && _itemEnrichmentAttempt.Value.ArtworkFailed))
                session.RecordComponentOutcome(identity, component, true, componentIdentity: componentIdentity);
        }
        _telemetry.RecordDiagnostic("item_cache.component", details: details);
        if (outcome == "preserved_failed" && (retryAtUtc > DateTimeOffset.UtcNow || reason is "local_backoff" or "local_circuit"))
            _telemetry.RecordDiagnostic("item_cache.component.deferred", details: details with { Outcome = "deferred" });
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
        forceRefresh |= _itemEnrichmentAttempt.Value?.ForceMetadata == true;
        var started = Stopwatch.GetTimestamp();
        var currentAttempt = _itemEnrichmentAttempt.Value;
        if (currentAttempt?.PreviousMetadata is { PosterUrl: not null } owned)
        {
            if (owned.ImageSource == "TMDb poster" && currentAttempt.CoreMetadataVerified)
                currentAttempt.PosterOwnerVerifiedEmpty = string.IsNullOrWhiteSpace(tmdbPosterUrl);
            else if (owned.ImageSource == "TMDb backdrop" && currentAttempt.CoreMetadataVerified)
                currentAttempt.PosterOwnerVerifiedEmpty = string.IsNullOrWhiteSpace(tmdbBackdropUrl);
            else if (owned.ImageSource == "OMDb poster" && currentAttempt.ExternalSucceeded)
                currentAttempt.PosterOwnerVerifiedEmpty = string.IsNullOrWhiteSpace(omdbPosterUrl);
            else if (owned.ImageSource == ArtworkSources.TvmazeImage && currentAttempt.TvmazeSucceeded)
                currentAttempt.PosterOwnerVerifiedEmpty = string.IsNullOrWhiteSpace(tvmazeEnrichmentImageUrl);
        }
        var refreshedBackdropPoster = currentAttempt is { CoreMetadataVerified: true } && !string.IsNullOrWhiteSpace(tmdbBackdropUrl)
            && (currentAttempt.PreviousMetadata?.ImageSource == "TMDb backdrop" || currentAttempt.PosterOwnerVerifiedEmpty);
        if (currentAttempt is { } retry && (retry.PreviousMetadata?.ArtworkComplete == false || retry.PosterOwnerVerifiedEmpty))
        {
            forceRefresh = true;
            if (!retry.CoreMetadataVerified) tmdbPosterUrl = null;
            if (!retry.ExternalSucceeded && !(retry.ExternalReused && !retry.PosterOwnerVerifiedEmpty
                && retry.PreviousMetadata is { CoreMetadataOnly: true, CoreOmdbCoverReusable: true })) omdbPosterUrl = null;
            if (!retry.TvmazeSucceeded) tvmazeEnrichmentImageUrl = null;
        }
        var knownCover = ArtworkResolver.ResolveKnownCover(
            tmdbPosterUrl,
            omdbPosterUrl,
            tvmazeEnrichmentImageUrl);
        if (knownCover is not null)
        {
            if (currentAttempt is not null) currentAttempt.PosterOwnerVerifiedEmpty = false;
            if (_itemEnrichmentAttempt.Value is { } attempt
                && (knownCover.Source == "OMDb poster" && attempt.ExternalReused
                    || knownCover.Source == ArtworkSources.TvmazeImage && (attempt.TvmazeFailed || attempt.TvmazeSkipped)))
                attempt.ArtworkReused = true;
            RecordItemComponent("known_cover", "artwork", "reused", started);
            return knownCover;
        }
        if (!forceRefresh && !refreshedBackdropPoster && _itemEnrichmentAttempt.Value?.PreviousMetadata is { PosterUrl: { } cachedPoster } metadata)
        {
            if (_itemEnrichmentAttempt.Value is { } cachedAttempt) cachedAttempt.ArtworkReused = true;
            RecordItemComponent("cache", "artwork", "reused", started);
            return new ArtworkCandidate(cachedPoster, metadata.ImageSource ?? "Cached artwork");
        }

        if (currentAttempt is { ExplicitForceRefresh: false } deferredArtwork && _itemCacheSession.Value is { } artworkSession
            && !artworkSession.ComponentRetryAllowed(new(request.MediaType, request.TmdbId), "artwork"))
        {
            deferredArtwork.ArtworkFailed = true;
            deferredArtwork.ArtworkReused = true;
            RecordItemComponent("cache", "artwork", "deferred", started, "failure_retry_pending");
            return deferredArtwork.PreviousMetadata is { PosterUrl: { } poster } retained
                ? new ArtworkCandidate(poster, retained.ImageSource ?? "Cached artwork") : null;
        }
        var allProvidersSkipped = _artworkProviders.Count > 0;
        foreach (var provider in _artworkProviders)
        {
            var (candidate, skipped) = await GetArtworkCandidateFromProviderAsync(provider, request, cancellationToken, forceRefresh);
            allProvidersSkipped &= skipped;
            if (candidate is not null && !string.IsNullOrWhiteSpace(candidate.Url))
            {
                if (currentAttempt is not null) currentAttempt.PosterOwnerVerifiedEmpty = false;
                return candidate;
            }
        }
        if (_itemEnrichmentAttempt.Value is { } artworkAttempt) artworkAttempt.ArtworkSkipped = allProvidersSkipped;

        if (refreshedBackdropPoster || currentAttempt is { CoreMetadataVerified: true, PosterOwnerVerifiedEmpty: true }
            && !string.IsNullOrWhiteSpace(tmdbBackdropUrl))
        {
            currentAttempt!.PosterOwnerVerifiedEmpty = false;
            currentAttempt.ArtworkReused = false;
            currentAttempt.ArtworkSkipped = false;
            return new ArtworkCandidate(tmdbBackdropUrl!, "TMDb backdrop");
        }

        if (_itemEnrichmentAttempt.Value is { PreviousMetadata: { PosterUrl: { } previousPoster } previous } previousAttempt
            && (previousAttempt.ArtworkFailed || previousAttempt.ArtworkSkipped
                || !previousAttempt.PosterOwnerVerifiedEmpty))
        {
            previousAttempt.ArtworkReused = true;
            return new ArtworkCandidate(previousPoster, previous.ImageSource ?? "Cached artwork");
        }

        if (currentAttempt is not null)
        {
            currentAttempt.PosterOwnerVerifiedEmpty = false;
            if (!string.IsNullOrWhiteSpace(tmdbBackdropUrl))
            {
                if (currentAttempt.CoreMetadataVerified) currentAttempt.ArtworkSkipped = false;
                else currentAttempt.ArtworkReused = true;
            }
        }
        return string.IsNullOrWhiteSpace(tmdbBackdropUrl) ? null : new ArtworkCandidate(tmdbBackdropUrl, "TMDb backdrop");
    }

    private async Task<(ArtworkCandidate? Candidate, bool Skipped)> GetArtworkCandidateFromProviderAsync(
        IArtworkProvider provider,
        ArtworkRequest request,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var outcome = await provider.GetArtworkWithStatusAsync(request, cancellationToken, forceRefresh);
            if (!outcome.Successful)
            {
                if (_itemEnrichmentAttempt.Value is { } attempt) attempt.MetadataFailed = attempt.OptionalMetadataFailed = attempt.ArtworkFailed = true;
                RecordItemComponent(provider.GetType().Name, "artwork", "preserved_failed", started,
                    outcome.Failure?.Reason, outcome.Failure?.StatusCode, outcome.Failure?.RetryAtUtc);
                return (null, false);
            }
            var skipped = outcome.Failure?.Reason is "disabled" or "not_applicable" or "not_configured";
            if (!skipped && outcome.Value is null && provider.OwnedSource is { } owner
                && _itemEnrichmentAttempt.Value is { PreviousMetadata: { PosterUrl: not null } previous } owningAttempt
                && string.Equals(owner, previous.ImageSource, StringComparison.OrdinalIgnoreCase))
                owningAttempt.PosterOwnerVerifiedEmpty = true;
            RecordItemComponent(provider.GetType().Name, "artwork", outcome.Failure?.Reason is "disabled" or "not_configured" ? "reused"
                : outcome.Value is null ? "provider_verified_empty" : "provider_fetch_success", started, outcome.Failure?.Reason);
            return (outcome.Value, skipped);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (_itemEnrichmentAttempt.Value is { } attempt) attempt.MetadataFailed = attempt.OptionalMetadataFailed = attempt.ArtworkFailed = true;
            RecordItemComponent(provider.GetType().Name, "artwork", "preserved_failed", started, "timeout");
            _logger.LogWarning(
                ex,
                "Skipping artwork provider {ProviderType} for {MediaType} {TmdbId} after a request timeout.",
                provider.GetType().Name,
                request.MediaType,
                request.TmdbId);

            return (null, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_itemEnrichmentAttempt.Value is { } attempt) attempt.MetadataFailed = attempt.OptionalMetadataFailed = attempt.ArtworkFailed = true;
            RecordItemComponent(provider.GetType().Name, "artwork", "preserved_failed", started, "request_failed");
            _logger.LogWarning(
                ex,
                "Skipping artwork provider {ProviderType} for {MediaType} {TmdbId}.",
                provider.GetType().Name,
                request.MediaType,
                request.TmdbId);

            return (null, false);
        }
    }

    private async Task<TvSeriesEnrichment> GetTvSeriesEnrichmentAsync(
        TmdbExternalIds? externalIds, string title, CancellationToken cancellationToken, bool forceRefresh)
    {
        var attempt = _itemEnrichmentAttempt.Value;
        var started = Stopwatch.GetTimestamp();
        if (attempt is not null)
            attempt.TvmazeIdentity = ItemEnrichmentCacheCoordinator.ItemEnrichmentSession.ComponentIdentity("tvmaze",
                imdbId: externalIds?.ImdbId, tvdbId: externalIds?.TvdbId);
        forceRefresh |= attempt?.ForceMetadata == true;
        if (attempt is { ExplicitForceRefresh: false } deferred && _itemCacheSession.Value is { } session
            && !session.ComponentRetryAllowed(new(deferred.MediaType ?? PremiereMediaType.Series, deferred.ItemId), "tvmaze", deferred.TvmazeIdentity))
        {
            deferred.TvmazeFailed = deferred.OptionalMetadataFailed = true;
            RecordItemComponent("tvmaze", "tvmaze", "deferred", started, "failure_retry_pending");
            return deferred.PreviousMetadata is { } prior
                ? new(prior.NetworkName, prior.WebChannelName, prior.TvmazeAverageRuntimeMinutes, prior.TvmazeRating,
                    prior.OfficialSiteUrl, prior.TvmazeUrl, prior.Overview, prior.ImageSource == ArtworkSources.TvmazeImage ? prior.PosterUrl : null)
                : EmptyTvSeriesEnrichment;
        }
        TvmazeShow? show = null;
        TvmazeShow? imageShow = null;
        var failed = false;
        ProviderFailure? failure = null;
        var skipped = false;
        if (externalIds?.TvdbId is not null || !string.IsNullOrWhiteSpace(externalIds?.ImdbId))
        {
            try
            {
                var outcome = await _tvmazeClient.LookupShowWithStatusAsync(externalIds.TvdbId, externalIds.ImdbId, cancellationToken, forceRefresh);
                failed |= !outcome.Successful;
                if (!outcome.Successful) failure = outcome.Failure;
                skipped |= outcome.Failure?.Reason == "disabled";
                show = outcome.Value;
                if (show is not null && HasTvmazeImage(show)) imageShow = show;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                failed = true;
                failure = ProviderFailure.FromException(ex);
                _logger.LogWarning(ex, "Skipping TVmaze lookup enrichment after its optional request timed out.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed = true;
                failure = ProviderFailure.FromException(ex);
                _logger.LogWarning(ex, "Skipping TVmaze lookup enrichment for TVDB ID {TvdbId} / IMDb ID {ImdbId}.", externalIds.TvdbId, externalIds.ImdbId);
            }
        }
        if ((show is null || !HasTvmazeImage(show)) && !string.IsNullOrWhiteSpace(title))
        {
            try
            {
                var outcome = await _tvmazeClient.SearchShowByNameWithStatusAsync(title, cancellationToken, forceRefresh);
                failed |= !outcome.Successful;
                if (!outcome.Successful) failure = outcome.Failure;
                skipped |= outcome.Failure?.Reason is "disabled" or "not_applicable";
                var match = outcome.Value;
                if (match is not null)
                {
                    show ??= match;
                    if (HasTvmazeImage(match)) imageShow = match;
                }
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                failed = true;
                failure = ProviderFailure.FromException(ex);
                _logger.LogWarning(ex, "Skipping TVmaze title enrichment after its optional request timed out.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed = true;
                failure = ProviderFailure.FromException(ex);
                _logger.LogWarning(ex, "Skipping TVmaze title enrichment for {Title}.", title);
            }
        }
        if (failed)
        {
            RecordItemComponent("tvmaze", "tvmaze", "preserved_failed", started,
                failure?.Reason ?? "request_failed", failure?.StatusCode, failure?.RetryAtUtc);
            if (attempt is not null) attempt.MetadataFailed = attempt.OptionalMetadataFailed = attempt.TvmazeFailed = true;
            if (attempt?.PreviousMetadata is { } previous)
                return new(previous.NetworkName, previous.WebChannelName, previous.TvmazeAverageRuntimeMinutes,
                    previous.TvmazeRating, previous.OfficialSiteUrl, previous.TvmazeUrl, previous.Overview,
                    previous.ImageSource == ArtworkSources.TvmazeImage ? previous.PosterUrl : null);
        }
        else if (skipped)
        {
            if (attempt is not null) attempt.TvmazeSkipped = true;
            RecordItemComponent("tvmaze", "tvmaze", "reused", started, "not_applicable");
            if (attempt?.PreviousMetadata is { } previous)
                return new(previous.NetworkName, previous.WebChannelName, previous.TvmazeAverageRuntimeMinutes,
                    previous.TvmazeRating, previous.OfficialSiteUrl, previous.TvmazeUrl, previous.Overview,
                    previous.ImageSource == ArtworkSources.TvmazeImage ? previous.PosterUrl : null);
        }
        else
        {
            if (attempt is not null) attempt.TvmazeSucceeded = true;
            RecordItemComponent("tvmaze", "tvmaze", show is null ? "provider_verified_empty" : "provider_fetch_success", started);
        }
        if (show is null) return EmptyTvSeriesEnrichment;
        if (show.Id > 0 && attempt is not null) attempt.TvmazeShowId = show.Id;
        return new(show.Network?.Name, show.WebChannel?.Name, show.AverageRuntime ?? show.Runtime,
            show.Rating?.Average, show.OfficialSite, show.Url, StripHtml(show.Summary),
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

    private PremiereSource[] EnrichmentSourceEntries(TmdbDetailsWithExtras? details,
        IReadOnlyList<string> sourceRegions, string? tvmazeNetworkName = null, string? tvmazeWebChannelName = null)
    {
        if (details is not null && _itemEnrichmentAttempt.Value is { } attempt)
            attempt.AvailabilityOwnedByWatchmode = false;
        return SourceEntries(details, sourceRegions, tvmazeNetworkName, tvmazeWebChannelName);
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
        forceRefresh |= _itemEnrichmentAttempt.Value?.ForceMetadata == true;
        var started = Stopwatch.GetTimestamp();
        if (HasWatchProviderSource(sources))
        {
            RecordItemComponent("tmdb", "availability", "provider_fetch_success", started, "tmdb_availability");
            return sources;
        }

        if (_itemCacheSession.Value is { } session && _itemEnrichmentAttempt.Value is { } deferred
            && !session.ComponentRetryAllowed(new(mediaType, tmdbId), "watchmode") && !deferred.ExplicitForceRefresh)
        {
            deferred.AvailabilityFailed = true;
            RecordItemComponent("watchmode", "availability", "deferred", started, "failure_retry_pending");
            return deferred.PreviousMetadata?.Sources ?? sources;
        }
        IReadOnlyList<PremiereSource> watchmodeSources;
        try
        {
            var outcome = await _watchmodeClient.GetTitleSourcesWithRefreshPolicyAsync(
                mediaType,
                tmdbId,
                imdbId,
                _options.SourceRegions,
                cancellationToken,
                forceRefresh, _itemEnrichmentAttempt.Value?.ForceIdentifierRefresh ?? forceRefresh);
            if (!outcome.Successful)
            {
                if (_itemEnrichmentAttempt.Value is { } attempt) attempt.MetadataFailed = attempt.AvailabilityFailed = true;
                RecordItemComponent("watchmode", "availability", "preserved_failed", started,
                    outcome.Failure?.Reason, outcome.Failure?.StatusCode, outcome.Failure?.RetryAtUtc);
                return SourcesWithPreviousAvailability(sources);
            }
            if (outcome.Failure?.Reason is "disabled" or "not_configured" or "not_applicable")
            {
                if (_itemEnrichmentAttempt.Value is { } skipped) skipped.AvailabilitySkipped = true;
                RecordItemComponent("watchmode", "availability", "reused", started, outcome.Failure.Reason);
                return SourcesWithPreviousAvailability(sources);
            }
            watchmodeSources = outcome.Value;
            if (_itemEnrichmentAttempt.Value is { } successful) successful.AvailabilityOwnedByWatchmode = true;
            RecordItemComponent("watchmode", "availability", watchmodeSources.Count == 0 ? "provider_verified_empty" : "provider_fetch_success", started);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (_itemEnrichmentAttempt.Value is { } attempt) attempt.MetadataFailed = attempt.AvailabilityFailed = true;
            RecordItemComponent("watchmode", "availability", "preserved_failed", started, "timeout");
            _logger.LogWarning(
                ex,
                "Skipping Watchmode availability fallback for {MediaType} {TmdbId} after a request timeout.",
                mediaType,
                tmdbId);
            return SourcesWithPreviousAvailability(sources);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_itemEnrichmentAttempt.Value is { } attempt) attempt.MetadataFailed = attempt.AvailabilityFailed = true;
            RecordItemComponent("watchmode", "availability", "preserved_failed", started, "request_failed");
            _logger.LogWarning(
                ex,
                "Skipping Watchmode availability fallback for {MediaType} {TmdbId}.",
                mediaType,
                tmdbId);
            return SourcesWithPreviousAvailability(sources);
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

    private PremiereSource[] SourcesWithPreviousAvailability(PremiereSource[] sources) => sources
        .Concat(_itemEnrichmentAttempt.Value?.PreviousMetadata?.Sources.Where(source => HasWatchProviderSource([source])) ?? [])
        .DistinctBy(source => $"{source.Kind}:{source.Id}:{source.Name}", StringComparer.OrdinalIgnoreCase).ToArray();

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
                if (!source.MoveNextTask.IsCompleted)
                {
                    _ = FinishSourceShutdownAsync(source);
                    return;
                }
            }
        }

        try
        {
            var disposal = source.Enumerator.DisposeAsync().AsTask();
            try { await disposal.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
            catch (TimeoutException) { _ = ObserveSourceShutdownAsync(disposal); }
        }
        catch (Exception ex) when (IsExpectedSourceShutdownException(ex))
        {
        }
    }

    private static async Task FinishSourceShutdownAsync(ActivePremiereSource source)
    {
        await ObserveSourceShutdownAsync(source.MoveNextTask);
        try { await source.Enumerator.DisposeAsync(); }
        catch (Exception) { /* Cleanup must not replace the load's original failure. */ }
    }

    private static async Task ObserveSourceShutdownAsync(Task task)
    {
        try { await task; }
        catch (Exception) { /* Observe detached shutdown work after the cleanup budget. */ }
    }

    private static bool IsExpectedSourceShutdownException(Exception ex)
    {
        return ex is OperationCanceledException
            or TimeoutException
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
