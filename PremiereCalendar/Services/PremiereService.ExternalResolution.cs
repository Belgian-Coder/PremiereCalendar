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
    private async Task<IReadOnlyList<PremiereItem>> GetExternalPremiereItemsAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh,
        PremiereDiscoveryCriteria criteria)
    {
        if (_discoveryProviders.Count == 0)
        {
            return [];
        }

        var candidateTasks = _discoveryProviders.Select(provider =>
            GetCandidatesFromProviderAsync(provider, start, end, cancellationToken, forceRefresh));

        var candidateGroups = await Task.WhenAll(candidateTasks);
        var candidates = candidateGroups
            .SelectMany(group => group)
            .Where(candidate => candidate.PremiereDate >= start && candidate.PremiereDate <= end)
            .Where(candidate => candidate.MediaType != PremiereMediaType.Series || criteria.IncludeSeries)
            .Where(candidate => candidate.MediaType != PremiereMediaType.Movie || criteria.IncludeMovies)
            .Where(candidate => CandidateMatchesSeriesDateMode(candidate, criteria))
            .Where(candidate => CandidateMatchesKnownRequestFilters(candidate, criteria))
            .GroupBy(ExternalCandidateKey, StringComparer.OrdinalIgnoreCase)
            .Select(MergeExternalCandidateGroup)
            .ToArray();

        return await MapWithLimitedConcurrencyAsync(
            candidates,
            (candidate, token) => MapExternalCandidateAsync(
                candidate,
                token,
                forceRefresh,
                criteria,
                new Dictionary<string, PremiereItem>(StringComparer.Ordinal),
                start,
                end),
            cancellationToken);
    }

    private async IAsyncEnumerable<PremiereItemBatch> StreamExternalPremiereItemBatchesAsync(
        IPremiereDiscoveryProvider provider,
        DateOnly start,
        DateOnly end,
        bool forceRefresh,
        PremiereDiscoveryCriteria criteria,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var candidateBatchSize = Math.Clamp(_options.ExternalCandidateBatchSize, 1, 500);
        var seenCandidateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingCandidates = new List<ExternalPremiereCandidate>(candidateBatchSize);
        var rawCandidateCount = 0;
        var acceptedCandidateCount = 0;
        var emitted = false;

        await foreach (var providerBatch in StreamCandidatesFromProviderAsync(provider, start, end, forceRefresh, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            rawCandidateCount += providerBatch.Count;
            var candidates = providerBatch
                .Where(candidate => candidate.PremiereDate >= start && candidate.PremiereDate <= end)
                .Where(candidate => candidate.MediaType != PremiereMediaType.Series || criteria.IncludeSeries)
                .Where(candidate => candidate.MediaType != PremiereMediaType.Movie || criteria.IncludeMovies)
                .Where(candidate => CandidateMatchesSeriesDateMode(candidate, criteria))
                .Where(candidate => CandidateMatchesKnownRequestFilters(candidate, criteria))
                .GroupBy(ExternalCandidateKey, StringComparer.OrdinalIgnoreCase)
                .Select(MergeExternalCandidateGroup)
                .Where(candidate => seenCandidateKeys.Add(ExternalCandidateKey(candidate)))
                .ToArray();

            if (candidates.Length == 0)
            {
                continue;
            }

            acceptedCandidateCount += candidates.Length;
            pendingCandidates.AddRange(candidates);
            if (pendingCandidates.Count < candidateBatchSize)
            {
                continue;
            }

            await foreach (var mappedBatch in MapExternalCandidatesInProgressAsync(
                    pendingCandidates,
                    forceRefresh,
                    criteria,
                    cachedEnrichment,
                    start,
                    end,
                    cancellationToken)
                .WithCancellation(cancellationToken))
            {
                emitted = true;
                yield return mappedBatch;
            }

            pendingCandidates.Clear();
        }

        if (pendingCandidates.Count > 0)
        {
            await foreach (var mappedBatch in MapExternalCandidatesInProgressAsync(
                    pendingCandidates,
                    forceRefresh,
                    criteria,
                    cachedEnrichment,
                    start,
                    end,
                    cancellationToken)
                .WithCancellation(cancellationToken))
            {
                emitted = true;
                yield return mappedBatch;
            }
        }

        if (!emitted)
        {
            yield return EmptyExternalCandidateProgress(rawCandidateCount, acceptedCandidateCount);
        }
    }

    private static PremiereItemBatch EmptyExternalCandidateProgress(int rawCandidateCount, int acceptedCandidateCount)
    {
        if (rawCandidateCount == 0)
        {
            return new PremiereItemBatch([], 0, 0, "no candidates returned");
        }

        if (acceptedCandidateCount == 0)
        {
            return new PremiereItemBatch(
                [],
                rawCandidateCount,
                rawCandidateCount,
                $"0 of {rawCandidateCount:N0} candidates matched request filters");
        }

        return new PremiereItemBatch(
            [],
            acceptedCandidateCount,
            acceptedCandidateCount,
            $"0 of {acceptedCandidateCount:N0} accepted candidates resolved to cards");
    }

    private async IAsyncEnumerable<IReadOnlyList<ExternalPremiereCandidate>> StreamCandidatesFromProviderAsync(
        IPremiereDiscoveryProvider provider,
        DateOnly start,
        DateOnly end,
        bool forceRefresh,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (provider is IStreamingPremiereDiscoveryProvider streamingProvider)
        {
            await foreach (var batch in streamingProvider.StreamCandidatesAsync(start, end, forceRefresh, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return batch;
            }

            yield break;
        }

        yield return await GetCandidatesFromProviderAsync(provider, start, end, cancellationToken, forceRefresh);
    }

    private async IAsyncEnumerable<PremiereItemBatch> MapExternalCandidatesInProgressAsync(
        IReadOnlyList<ExternalPremiereCandidate> candidates,
        bool forceRefresh,
        PremiereDiscoveryCriteria criteria,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        DateOnly requestStart,
        DateOnly requestEnd,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var mappedBatch in MapInProgressBatchesAsync(
                candidates,
                (candidate, token) => MapExternalCandidateAsync(
                    candidate,
                    token,
                    forceRefresh,
                    criteria,
                    cachedEnrichment,
                    requestStart,
                    requestEnd),
                cancellationToken)
            .WithCancellation(cancellationToken))
        {
            yield return WithCandidateProgress(mappedBatch, candidates.Count);
        }
    }

    private async Task<int[]> SearchKeywordIdsAsync(string keywordText, CancellationToken cancellationToken, bool forceRefresh)
    {
        if (string.IsNullOrWhiteSpace(keywordText))
        {
            return [];
        }

        var keywords = await _tmdbClient.SearchKeywordsAsync(keywordText, cancellationToken, forceRefresh);
        return keywords
            .Where(keyword => keyword.Id > 0)
            .Select(keyword => keyword.Id)
            .Distinct()
            .Order()
            .ToArray();
    }

    private async Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesFromProviderAsync(
        IPremiereDiscoveryProvider provider,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        try
        {
            return await provider.GetCandidatesAsync(start, end, cancellationToken, forceRefresh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Skipping external discovery provider {ProviderType} for {StartDate} through {EndDate}.",
                provider.GetType().Name,
                start,
                end);

            return [];
        }
    }

    private async Task<PremiereItem?> MapExternalCandidateAsync(
        ExternalPremiereCandidate candidate,
        CancellationToken cancellationToken,
        bool forceRefresh,
        PremiereDiscoveryCriteria criteria,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        DateOnly requestStart,
        DateOnly requestEnd)
    {
        var canonicalCandidate = await CanonicalizeExternalSeriesPremiereCandidateAsync(
            candidate,
            criteria,
            requestStart,
            requestEnd,
            cancellationToken,
            forceRefresh);
        if (canonicalCandidate is null)
        {
            return null;
        }

        candidate = canonicalCandidate;
        if (TryReuseCachedExternalCandidate(cachedEnrichment, candidate, criteria, out var cachedCandidateItem))
        {
            return await HydrateExternalCandidateRatingsAsync(cachedCandidateItem, candidate, cancellationToken, forceRefresh);
        }

        var tmdbId = await ResolveCandidateTmdbIdAsync(candidate, cancellationToken, forceRefresh);
        if (tmdbId == ConflictingExternalIdsTmdbId)
        {
            return null;
        }

        if (tmdbId is not > 0)
        {
            return CreateUnverifiedPremiereItem(candidate, criteria);
        }

        var title = candidate.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            var details = candidate.MediaType == PremiereMediaType.Movie
                ? await TryGetDetailsAsync(
                    () => _tmdbClient.GetMovieDetailsAsync(tmdbId.Value, cancellationToken, forceRefresh),
                    candidate.MediaType,
                    tmdbId.Value,
                    cancellationToken)
                : await TryGetDetailsAsync(
                    () => _tmdbClient.GetTvDetailsAsync(tmdbId.Value, cancellationToken, forceRefresh),
                    candidate.MediaType,
                    tmdbId.Value,
                    cancellationToken);

            title = CoalesceText(details?.Title, details?.Name, details?.OriginalTitle, details?.OriginalName);
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var premiereDate = candidate.PremiereDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var mappedItem = candidate.MediaType == PremiereMediaType.Movie
            ? await MapMovieAsync(
                new TmdbMovieDiscoverItem
                {
                    Id = tmdbId.Value,
                    Title = title,
                    OriginalTitle = title,
                    ReleaseDate = premiereDate,
                    PrimaryReleaseDate = premiereDate,
                    OriginalLanguage = candidate.OriginalLanguage
                },
                cancellationToken,
                forceRefresh,
                cachedEnrichment,
                allowWatchmodeAvailabilityFallback: false)
            : await MapSeriesAsync(
                new TmdbTvDiscoverItem
                {
                    Id = tmdbId.Value,
                    Name = title,
                    OriginalName = title,
                    FirstAirDate = premiereDate,
                    OriginalLanguage = candidate.OriginalLanguage
                },
                cancellationToken,
                forceRefresh,
                cachedEnrichment,
                premiereDateOverride: candidate.PremiereDate,
                itemTypeOverride: criteria.Series.SeriesDateMode == SeriesDateMode.AllEpisodes
                    ? PremiereItemType.SeriesEpisode
                    : PremiereItemType.SeriesPremiere,
                episodeTitle: candidate.EpisodeTitle,
                seasonNumber: candidate.SeasonNumber,
                episodeNumber: candidate.EpisodeNumber,
                episodeSource: candidate.Source,
                allowWatchmodeAvailabilityFallback: false);

        if (mappedItem is null)
        {
            return null;
        }

        var mergedItem = MergeExternalCandidateSource(mappedItem, candidate);
        return await HydrateExternalCandidateRatingsAsync(mergedItem, candidate, cancellationToken, forceRefresh);
    }

    private async Task<ExternalPremiereCandidate?> CanonicalizeExternalSeriesPremiereCandidateAsync(
        ExternalPremiereCandidate candidate,
        PremiereDiscoveryCriteria criteria,
        DateOnly requestStart,
        DateOnly requestEnd,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (candidate.MediaType != PremiereMediaType.Series
            || criteria.Series.SeriesDateMode != SeriesDateMode.NewSeriesOnly
            || candidate.TmdbId is not > 0)
        {
            return candidate;
        }

        var canonicalDate = await GetSeasonOneEpisodeOneDateAsync(candidate.TmdbId.Value, cancellationToken, forceRefresh);
        if (canonicalDate is null)
        {
            return candidate;
        }

        if (canonicalDate < requestStart || canonicalDate > requestEnd)
        {
            return null;
        }

        return candidate with
        {
            PremiereDate = canonicalDate.Value,
            SeriesPremiereDate = canonicalDate.Value
        };
    }

    private static PremiereItem? CreateUnverifiedPremiereItem(
        ExternalPremiereCandidate candidate,
        PremiereDiscoveryCriteria criteria)
    {
        if (string.IsNullOrWhiteSpace(candidate.Title))
        {
            return null;
        }

        var type = candidate.MediaType == PremiereMediaType.Movie
            ? PremiereItemType.MovieFirstRelease
            : candidate.IsSeriesEpisode || criteria.Series.SeriesDateMode == SeriesDateMode.AllEpisodes
                ? PremiereItemType.SeriesEpisode
                : PremiereItemType.SeriesPremiere;
        var candidateKey = ExternalCandidateKey(candidate);
        var sourceNames = CandidateSourceNames(candidate);
        var posterUrl = CoalesceText(candidate.PosterUrl, candidate.BackdropUrl);

        return new PremiereItem
        {
            CanonicalId = UnverifiedCanonicalId(candidate, candidateKey),
            Type = type,
            MediaType = candidate.MediaType,
            TmdbId = 0,
            ImdbId = NormalizeExternalId(candidate.ImdbId),
            TvdbId = candidate.TvdbId,
            VerificationState = PremiereVerificationState.Unverified,
            VerificationNote = "Could not match to TMDb yet",
            ExternalProviderId = candidate.ExternalProviderId,
            ExternalUrl = candidate.ExternalUrl,
            ExternalCandidateKey = candidateKey,
            Title = candidate.Title.Trim(),
            PremiereDate = candidate.PremiereDate,
            PosterUrl = posterUrl,
            BackdropUrl = candidate.BackdropUrl,
            ImageSource = string.IsNullOrWhiteSpace(posterUrl)
                ? null
                : $"{sourceNames.FirstOrDefault() ?? candidate.Source} artwork",
            OriginalLanguage = candidate.OriginalLanguage ?? "",
            SourceNames = sourceNames,
            Sources = SourceEntriesWithCandidate([], candidate),
            EpisodeTitle = candidate.EpisodeTitle,
            SeasonNumber = candidate.SeasonNumber,
            EpisodeNumber = candidate.EpisodeNumber,
            EpisodeSource = candidate.Source,
            ImdbScore = candidate.ImdbScore,
            ImdbVoteCount = candidate.ImdbVoteCount,
            DateSemantics = new PremiereDateSemantics(
                candidate.PremiereDate,
                PremiereDateSourceKind.ExternalProviderDate,
                PremiereDataConfidence.Low,
                $"Unverified date from {candidate.Source}."),
            MergeContributions =
            [
                PremiereDiagnosticsFactory.ExternalContribution(
                    candidate,
                    "Unmapped external candidate",
                    "Could not resolve this external candidate to a TMDb ID.")
            ],
            NetworkName = candidate.MediaType == PremiereMediaType.Series ? candidate.Source : null
        };
    }

    private static ExternalPremiereCandidate MergeExternalCandidateGroup(IGrouping<string, ExternalPremiereCandidate> group)
    {
        var candidates = group.ToArray();
        var selected = candidates
            .OrderBy(candidate => candidate.PremiereDate)
            .ThenByDescending(candidate => CandidateSourceNames(candidate).Length)
            .ThenBy(candidate => candidate.Title ?? "", StringComparer.OrdinalIgnoreCase)
            .First();
        var sourceNames = candidates
            .SelectMany(CandidateSourceNames)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => source.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var title = candidates
            .OrderBy(candidate => string.IsNullOrWhiteSpace(candidate.Title))
            .ThenBy(candidate => candidate.PremiereDate)
            .Select(candidate => candidate.Title)
            .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));
        var posterUrl = candidates
            .Select(candidate => candidate.PosterUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        var backdropUrl = candidates
            .Select(candidate => candidate.BackdropUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        var externalUrl = candidates
            .Select(candidate => candidate.ExternalUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        var externalProviderId = candidates
            .Select(candidate => candidate.ExternalProviderId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        var imdbId = candidates
            .Select(candidate => NormalizeExternalId(candidate.ImdbId))
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        var tvdbId = candidates
            .Select(candidate => candidate.TvdbId)
            .FirstOrDefault(id => id is > 0);
        var imdbScore = candidates
            .Select(candidate => candidate.ImdbScore)
            .FirstOrDefault(score => score is not null);
        var imdbVoteCount = candidates
            .Select(candidate => candidate.ImdbVoteCount)
            .FirstOrDefault(votes => votes is not null);

        return selected with
        {
            Title = title ?? selected.Title,
            Source = sourceNames.FirstOrDefault() ?? selected.Source,
            SourceNames = sourceNames,
            PosterUrl = posterUrl ?? selected.PosterUrl,
            BackdropUrl = backdropUrl ?? selected.BackdropUrl,
            ExternalUrl = externalUrl ?? selected.ExternalUrl,
            ExternalProviderId = externalProviderId ?? selected.ExternalProviderId,
            ImdbId = imdbId ?? selected.ImdbId,
            TvdbId = tvdbId ?? selected.TvdbId,
            ImdbScore = imdbScore ?? selected.ImdbScore,
            ImdbVoteCount = imdbVoteCount ?? selected.ImdbVoteCount
        };
    }

    private static bool TryReuseCachedExternalCandidate(
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        ExternalPremiereCandidate candidate,
        PremiereDiscoveryCriteria criteria,
        out PremiereItem reusedItem)
    {
        foreach (var cachedItem in cachedEnrichment.Values)
        {
            if (!IsFreshReusableEnrichment(cachedItem)
                || cachedItem.VerificationState != PremiereVerificationState.Verified
                || cachedItem.MediaType != candidate.MediaType
                || cachedItem.PremiereDate != candidate.PremiereDate
                || !ExternalCandidateMatchesCachedItem(candidate, cachedItem)
                || !ExternalEpisodeMatchesCachedItem(candidate, criteria, cachedItem))
            {
                continue;
            }

            reusedItem = MergeCachedExternalCandidate(cachedItem, candidate);
            return true;
        }

        reusedItem = new PremiereItem
        {
            MediaType = candidate.MediaType,
            TmdbId = 0,
            Title = candidate.Title ?? "",
            PremiereDate = candidate.PremiereDate
        };
        return false;
    }

    private static bool ExternalCandidateMatchesCachedItem(ExternalPremiereCandidate candidate, PremiereItem cachedItem)
    {
        return (candidate.TmdbId is > 0 && candidate.TmdbId == cachedItem.TmdbId)
            || (candidate.TvdbId is > 0 && candidate.TvdbId == cachedItem.TvdbId)
            || (!string.IsNullOrWhiteSpace(candidate.ImdbId)
                && string.Equals(candidate.ImdbId, cachedItem.ImdbId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ExternalEpisodeMatchesCachedItem(
        ExternalPremiereCandidate candidate,
        PremiereDiscoveryCriteria criteria,
        PremiereItem cachedItem)
    {
        if (candidate.MediaType != PremiereMediaType.Series
            || (!candidate.IsSeriesEpisode && criteria.Series.SeriesDateMode != SeriesDateMode.AllEpisodes))
        {
            return true;
        }

        if (criteria.Series.SeriesDateMode == SeriesDateMode.NewSeriesOnly
            && IsSeasonOneEpisodeOne(candidate))
        {
            if (candidate.SeriesPremiereDate is { } canonicalPremiereDate
                && canonicalPremiereDate != candidate.PremiereDate)
            {
                return false;
            }

            return cachedItem.Type == PremiereItemType.SeriesPremiere
                || cachedItem is
                {
                    Type: PremiereItemType.SeriesEpisode,
                    SeasonNumber: 1,
                    EpisodeNumber: 1
                };
        }

        if (candidate.SeasonNumber is > 0 && candidate.EpisodeNumber is > 0)
        {
            return candidate.SeasonNumber == cachedItem.SeasonNumber
                && candidate.EpisodeNumber == cachedItem.EpisodeNumber;
        }

        return cachedItem.Type == PremiereItemType.SeriesEpisode;
    }

    private static bool CandidateMatchesSeriesDateMode(
        ExternalPremiereCandidate candidate,
        PremiereDiscoveryCriteria criteria)
    {
        if (candidate.MediaType != PremiereMediaType.Series
            || criteria.Series.SeriesDateMode == SeriesDateMode.AllEpisodes
            || !candidate.IsSeriesEpisode)
        {
            return true;
        }

        return IsSeasonOneEpisodeOne(candidate)
            && (candidate.SeriesPremiereDate is null || candidate.SeriesPremiereDate == candidate.PremiereDate);
    }

    private static bool IsSeasonOneEpisodeOne(ExternalPremiereCandidate candidate)
    {
        return candidate is
        {
            SeasonNumber: 1,
            EpisodeNumber: 1
        };
    }

    private static PremiereItem MergeCachedExternalCandidate(
        PremiereItem cachedItem,
        ExternalPremiereCandidate candidate)
    {
        var sourceNames = SourceNamesWithCandidate(cachedItem.SourceNames, candidate);
        var candidateImdbId = NormalizeExternalId(candidate.ImdbId);
        var imdbId = CoalesceText(cachedItem.ImdbId, candidateImdbId);
        return cachedItem with
        {
            ImdbId = imdbId,
            ImdbUrl = CoalesceText(cachedItem.ImdbUrl, BuildImdbUrl(imdbId)),
            TvdbId = cachedItem.TvdbId ?? candidate.TvdbId,
            Title = CoalesceText(candidate.Title, cachedItem.Title) ?? cachedItem.Title,
            PremiereDate = candidate.PremiereDate,
            EpisodeTitle = CoalesceText(candidate.EpisodeTitle, cachedItem.EpisodeTitle),
            SeasonNumber = candidate.SeasonNumber ?? cachedItem.SeasonNumber,
            EpisodeNumber = candidate.EpisodeNumber ?? cachedItem.EpisodeNumber,
            EpisodeSource = CoalesceText(candidate.Source, cachedItem.EpisodeSource),
            ImdbScore = cachedItem.ImdbScore ?? candidate.ImdbScore,
            ImdbVoteCount = cachedItem.ImdbVoteCount ?? candidate.ImdbVoteCount,
            SourceNames = sourceNames,
            Sources = SourceEntriesWithCandidate(cachedItem.Sources, candidate),
            MergeContributions = cachedItem.MergeContributions
                .Append(PremiereDiagnosticsFactory.ExternalContribution(
                    candidate,
                    ExternalCandidateMatchMethod(candidate),
                    "Reused cached TMDb-backed enrichment for this external candidate."))
                .DistinctBy(MergeContributionKey, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            NetworkName = CoalesceText(candidate.Source, cachedItem.NetworkName)
        };
    }

    private static PremiereItem MergeExternalCandidateSource(
        PremiereItem item,
        ExternalPremiereCandidate candidate)
    {
        var candidateImdbId = NormalizeExternalId(candidate.ImdbId);
        var imdbId = CoalesceText(item.ImdbId, candidateImdbId);
        return item with
        {
            ImdbId = imdbId,
            ImdbUrl = CoalesceText(item.ImdbUrl, BuildImdbUrl(imdbId)),
            TvdbId = item.TvdbId ?? candidate.TvdbId,
            ImdbScore = item.ImdbScore ?? candidate.ImdbScore,
            ImdbVoteCount = item.ImdbVoteCount ?? candidate.ImdbVoteCount,
            SourceNames = SourceNamesWithCandidate(item.SourceNames, candidate),
            Sources = SourceEntriesWithCandidate(item.Sources, candidate),
            DateSemantics = item.DateSemantics?.SourceKind == PremiereDateSourceKind.TmdbSeasonOneEpisodeOne
                ? item.DateSemantics
                : new PremiereDateSemantics(
                    item.PremiereDate,
                    PremiereDateSourceKind.ExternalProviderDate,
                    PremiereDataConfidence.High,
                    $"Date accepted from {candidate.Source} and mapped back to TMDb."),
            MergeContributions = item.MergeContributions
                .Append(PremiereDiagnosticsFactory.ExternalContribution(
                    candidate,
                    ExternalCandidateMatchMethod(candidate),
                    "Merged external candidate into the TMDb-backed row."))
                .DistinctBy(MergeContributionKey, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static string ExternalCandidateMatchMethod(ExternalPremiereCandidate candidate)
    {
        if (candidate.TmdbId is > 0)
        {
            return "TMDb ID";
        }

        if (!string.IsNullOrWhiteSpace(candidate.ImdbId))
        {
            return "IMDb ID";
        }

        if (candidate.TvdbId is > 0)
        {
            return "TVDB ID";
        }

        if (!string.IsNullOrWhiteSpace(candidate.ExternalProviderId))
        {
            return "Provider ID";
        }

        return "Title/date";
    }

    private async Task<PremiereItem> HydrateExternalCandidateRatingsAsync(
        PremiereItem item,
        ExternalPremiereCandidate candidate,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var candidateImdbId = NormalizeExternalId(candidate.ImdbId);
        if (string.IsNullOrWhiteSpace(candidateImdbId)
            || !string.Equals(candidateImdbId, item.ImdbId, StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }

        var ratings = await GetExternalRatingsAsync(candidateImdbId, cancellationToken, forceRefresh);
        var rottenTomatoesScores = await GetRottenTomatoesScoresAsync(
            candidate.MediaType,
            CoalesceText(candidate.Title, item.Title) ?? item.Title,
            candidate.ReleaseYear ?? item.PremiereDate.Year,
            item.WikidataId,
            cancellationToken,
            forceRefresh);
        return item with
        {
            ImdbScore = ratings.ImdbScore ?? item.ImdbScore,
            ImdbVoteCount = ratings.ImdbVoteCount ?? item.ImdbVoteCount,
            RottenTomatoesScore = ratings.RottenTomatoesScore ?? rottenTomatoesScores.CriticScore ?? item.RottenTomatoesScore,
            RottenTomatoesAudienceScore = ratings.RottenTomatoesAudienceScore ?? rottenTomatoesScores.AudienceScore ?? item.RottenTomatoesAudienceScore,
            MetacriticScore = ratings.MetacriticScore ?? item.MetacriticScore,
            Overview = CoalesceText(item.Overview, ratings.Plot),
            PosterUrl = CoalesceText(item.PosterUrl, ratings.PosterUrl)
        };
    }

    private static string[] SourceNamesWithCandidate(IReadOnlyList<string> sourceNames, ExternalPremiereCandidate candidate)
    {
        return CandidateSourceNames(candidate)
            .Concat(sourceNames)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] SourceNamesWithCandidate(IReadOnlyList<string> sourceNames, string? candidateSource)
    {
        return (string.IsNullOrWhiteSpace(candidateSource) ? Enumerable.Empty<string>() : [candidateSource.Trim()])
            .Concat(sourceNames)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PremiereSource[] SourceEntriesWithCandidate(
        IReadOnlyList<PremiereSource> sources,
        ExternalPremiereCandidate candidate)
    {
        return SourceEntriesWithCandidate(sources, CandidateSourceNames(candidate));
    }

    private static PremiereSource[] SourceEntriesWithCandidate(
        IReadOnlyList<PremiereSource> sources,
        string? candidateSource)
    {
        return SourceEntriesWithCandidate(
            sources,
            string.IsNullOrWhiteSpace(candidateSource) ? [] : [candidateSource.Trim()]);
    }

    private static PremiereSource[] SourceEntriesWithCandidate(
        IReadOnlyList<PremiereSource> sources,
        IReadOnlyList<string> candidateSources)
    {
        var candidateEntries = candidateSources
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source =>
            {
                return new PremiereSource
                {
                    Name = source.Trim(),
                    Kind = "schedule"
                };
            });

        return candidateEntries
            .Concat(sources)
            .Where(source => !string.IsNullOrWhiteSpace(source.Name))
            .DistinctBy(source => $"{source.Kind}:{source.Id}:{source.Name}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] CandidateSourceNames(ExternalPremiereCandidate candidate)
    {
        return (candidate.SourceNames is { Count: > 0 }
                ? candidate.SourceNames
                : [candidate.Source])
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => source.Trim())
            .Where(IsDisplayableCandidateSource)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsDisplayableCandidateSource(string source)
    {
        return !string.Equals(source, "Trakt", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(source, "Simkl", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int?> ResolveCandidateTmdbIdAsync(
        ExternalPremiereCandidate candidate,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (candidate.TmdbId is > 0)
        {
            return candidate.TmdbId.Value;
        }

        var matches = new List<(string Source, int Id)>();
        if (candidate.MediaType == PremiereMediaType.Series && candidate.TvdbId is > 0)
        {
            var tvdbMatch = await TryFindTmdbIdAsync(
                candidate.MediaType,
                candidate.TvdbId.Value.ToString(CultureInfo.InvariantCulture),
                "tvdb_id",
                candidate.Source,
                cancellationToken,
                forceRefresh);

            if (tvdbMatch is > 0)
            {
                matches.Add(("tvdb_id", tvdbMatch.Value));
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate.ImdbId))
        {
            var imdbMatch = await TryFindTmdbIdAsync(
                candidate.MediaType,
                candidate.ImdbId,
                "imdb_id",
                candidate.Source,
                cancellationToken,
                forceRefresh);
            if (imdbMatch is > 0)
            {
                matches.Add(("imdb_id", imdbMatch.Value));
            }
        }

        var distinctIds = matches.Select(match => match.Id).Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            return await TryResolveCandidateByStrictTitleAsync(candidate, cancellationToken, forceRefresh);
        }

        if (distinctIds.Length > 1)
        {
            _logger.LogWarning(
                "Skipping {ProviderName} candidate {Title} because external IDs resolved to multiple TMDb IDs: {ResolvedIds}.",
                candidate.Source,
                candidate.Title,
                string.Join(", ", matches.Select(match => $"{match.Source}:{match.Id}")));
            return ConflictingExternalIdsTmdbId;
        }

        return distinctIds[0];
    }

    private async Task<int?> TryResolveCandidateByStrictTitleAsync(
        ExternalPremiereCandidate candidate,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (string.IsNullOrWhiteSpace(candidate.Title))
        {
            return null;
        }

        var candidateYear = CandidateYear(candidate);
        try
        {
            var results = await _tmdbClient.SearchTitlesAsync(
                candidate.MediaType,
                candidate.Title,
                candidateYear,
                cancellationToken,
                forceRefresh);
            var candidateTitleKey = NormalizeTitleForIdentity(candidate.Title);
            var matchingIds = results
                .Where(result => TitleSearchResultMatches(candidate.MediaType, result, candidateTitleKey, candidateYear))
                .Select(result => result.Id)
                .Where(id => id > 0)
                .Distinct()
                .ToArray();

            if (matchingIds.Length == 1)
            {
                return matchingIds[0];
            }

            if (matchingIds.Length > 1)
            {
                _logger.LogInformation(
                    "Keeping {ProviderName} candidate {Title} unverified because strict TMDb title search returned multiple exact {Year} matches.",
                    candidate.Source,
                    candidate.Title,
                    candidateYear);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Skipping strict TMDb title lookup for {ProviderName} candidate {Title} after a request timeout.",
                candidate.Source,
                candidate.Title);
        }
        catch (ExternalApiException ex)
        {
            _logger.LogWarning(
                ex,
                "Skipping strict TMDb title lookup for {ProviderName} candidate {Title}.",
                candidate.Source,
                candidate.Title);
        }

        return null;
    }

    private static int CandidateYear(ExternalPremiereCandidate candidate)
    {
        return candidate.ReleaseYear
            ?? candidate.SeriesPremiereDate?.Year
            ?? candidate.PremiereDate.Year;
    }

    private static bool TitleSearchResultMatches(
        PremiereMediaType mediaType,
        TmdbTitleSearchResult result,
        string candidateTitleKey,
        int candidateYear)
    {
        if (result.Id <= 0 || string.IsNullOrWhiteSpace(candidateTitleKey))
        {
            return false;
        }

        var title = mediaType == PremiereMediaType.Movie
            ? result.Title
            : result.Name;
        var originalTitle = mediaType == PremiereMediaType.Movie
            ? result.OriginalTitle
            : result.OriginalName;
        var dateText = mediaType == PremiereMediaType.Movie
            ? result.ReleaseDate
            : result.FirstAirDate;

        return (string.Equals(NormalizeTitleForIdentity(title), candidateTitleKey, StringComparison.Ordinal)
                || string.Equals(NormalizeTitleForIdentity(originalTitle), candidateTitleKey, StringComparison.Ordinal))
            && TryParseTmdbDate(dateText, out var resultDate)
            && resultDate.Year == candidateYear;
    }

    private async Task<int?> TryFindTmdbIdAsync(
        PremiereMediaType mediaType,
        string externalId,
        string externalSource,
        string providerName,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        try
        {
            return await _tmdbClient.FindTmdbIdByExternalIdAsync(
                mediaType,
                externalId,
                externalSource,
                cancellationToken,
                forceRefresh);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Skipping {ProviderName} candidate because TMDb lookup for {ExternalSource}:{ExternalId} timed out.",
                providerName,
                externalSource,
                externalId);

            return null;
        }
        catch (ExternalApiException ex)
        {
            _logger.LogWarning(
                ex,
                "Skipping {ProviderName} candidate because TMDb could not resolve {ExternalSource}:{ExternalId}.",
                providerName,
                externalSource,
                externalId);

            return null;
        }
    }

    private static string ExternalCandidateKey(ExternalPremiereCandidate candidate)
    {
        if (candidate.TmdbId is > 0)
        {
            return candidate.IsSeriesEpisode
                ? $"{candidate.MediaType}:tmdb:{candidate.TmdbId.Value}:{candidate.PremiereDate:yyyyMMdd}:{candidate.SeasonNumber}:{candidate.EpisodeNumber}"
                : $"{candidate.MediaType}:tmdb:{candidate.TmdbId.Value}";
        }

        if (candidate.MediaType == PremiereMediaType.Series && candidate.TvdbId is > 0)
        {
            return candidate.IsSeriesEpisode
                ? $"{candidate.MediaType}:tvdb:{candidate.TvdbId.Value}:{candidate.PremiereDate:yyyyMMdd}:{candidate.SeasonNumber}:{candidate.EpisodeNumber}"
                : $"{candidate.MediaType}:tvdb:{candidate.TvdbId.Value}";
        }

        if (!string.IsNullOrWhiteSpace(candidate.ImdbId))
        {
            return candidate.IsSeriesEpisode
                ? $"{candidate.MediaType}:imdb:{candidate.ImdbId}:{candidate.PremiereDate:yyyyMMdd}:{candidate.SeasonNumber}:{candidate.EpisodeNumber}"
                : $"{candidate.MediaType}:imdb:{candidate.ImdbId}";
        }

        if (!string.IsNullOrWhiteSpace(candidate.ExternalProviderId))
        {
            return candidate.IsSeriesEpisode
                ? $"{candidate.MediaType}:provider:{candidate.Source}:{candidate.ExternalProviderId}:{candidate.PremiereDate:yyyyMMdd}:{candidate.SeasonNumber}:{candidate.EpisodeNumber}"
                : $"{candidate.MediaType}:provider:{candidate.Source}:{candidate.ExternalProviderId}";
        }

        var year = CandidateYear(candidate);
        return $"{candidate.MediaType}:title:{candidate.PremiereDate:yyyyMMdd}:{year}:{NormalizeTitleForIdentity(candidate.Title)}";
    }

    private static string UnverifiedCanonicalId(ExternalPremiereCandidate candidate, string candidateKey)
    {
        var media = candidate.MediaType == PremiereMediaType.Movie ? "movie" : "series";
        var titleSegment = SlugForCanonicalId(candidate.Title);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(candidateKey));
        var hash = Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
        return $"unverified:{media}:{titleSegment}:{hash}";
    }

    private static string SlugForCanonicalId(string? value)
    {
        var normalized = NormalizeTitleForIdentity(value).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "external";
        }

        return normalized.Length <= 48 ? normalized : normalized[..48];
    }

    private static string NormalizeTitleForIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string? NormalizeExternalId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool CandidateMatchesKnownRequestFilters(
        ExternalPremiereCandidate candidate,
        PremiereDiscoveryCriteria criteria)
    {
        var languageFilters = candidate.MediaType == PremiereMediaType.Series
            ? criteria.Series.OriginalLanguages
            : criteria.Movies.OriginalLanguages;

        if (languageFilters.Length > 0
            && !string.IsNullOrWhiteSpace(candidate.OriginalLanguage)
            && !languageFilters.Contains(candidate.OriginalLanguage, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private async Task<IReadOnlyList<PremiereItem>> MapWithLimitedConcurrencyAsync<T>(
        IEnumerable<T> rawItems,
        Func<T, CancellationToken, Task<PremiereItem?>> mapItem,
        CancellationToken cancellationToken)
    {
        var concurrency = Math.Clamp(_options.MaxEnrichmentConcurrency, 1, 32);
        using var gate = new SemaphoreSlim(concurrency);

        var tasks = rawItems.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);

            try
            {
                return await mapItem(item, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });

        var mappedItems = await Task.WhenAll(tasks);
        return mappedItems.Where(item => item is not null).Select(item => item!).ToList();
    }

    private async IAsyncEnumerable<PremiereItemBatch> MapInProgressBatchesAsync<T>(
        IEnumerable<T> rawItems,
        Func<T, CancellationToken, Task<PremiereItem?>> mapItem,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var concurrency = Math.Clamp(_options.MaxEnrichmentConcurrency, 1, 32);
        var progressBatchSize = Math.Clamp(_options.EnrichmentProgressBatchSize, 1, 100);
        var rawItemList = rawItems as IReadOnlyCollection<T> ?? rawItems.ToArray();
        using var enumerator = rawItemList.GetEnumerator();
        using var mapperCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var mapToken = mapperCancellation.Token;
        var pending = new List<Task<PremiereItem?>>(concurrency);
        var batch = new List<PremiereItem>(progressBatchSize);
        var hasMore = true;
        var completedWork = 0;
        var totalWork = rawItemList.Count;
        var lastEmittedCompletedWork = 0;

        void StartPending()
        {
            while (hasMore && pending.Count < concurrency)
            {
                mapToken.ThrowIfCancellationRequested();
                if (!enumerator.MoveNext())
                {
                    hasMore = false;
                    break;
                }

                var item = enumerator.Current;
                pending.Add(mapItem(item, mapToken));
            }
        }

        try
        {
            StartPending();

            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);

                PremiereItem? mapped;
                try
                {
                    mapped = await completed;
                }
                catch
                {
                    mapperCancellation.Cancel();
                    await ObservePendingMappingsAsync(pending);
                    throw;
                }

                completedWork++;
                if (mapped is not null)
                {
                    batch.Add(mapped);
                }

                StartPending();

                if (batch.Count >= progressBatchSize)
                {
                    lastEmittedCompletedWork = completedWork;
                    yield return new PremiereItemBatch(batch.ToArray(), completedWork, totalWork);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                lastEmittedCompletedWork = completedWork;
                yield return new PremiereItemBatch(batch.ToArray(), completedWork, totalWork);
                batch.Clear();
            }
            else if (totalWork > 0 && lastEmittedCompletedWork < completedWork)
            {
                yield return new PremiereItemBatch([], completedWork, totalWork);
            }
        }
        finally
        {
            mapperCancellation.Cancel();
            await ObservePendingMappingsAsync(pending);
        }
    }

    private static async Task ObservePendingMappingsAsync(IEnumerable<Task<PremiereItem?>> pending)
    {
        foreach (var task in pending)
        {
            try
            {
                await task;
            }
            catch
            {
            }
        }
    }

    private static PremiereItemBatch WithTmdbProgress<T>(
        PremiereItemBatch batch,
        TmdbDiscoverBatch<T> rawBatch,
        int completedRawItemsBeforeBatch)
    {
        var completed = Math.Max(0, completedRawItemsBeforeBatch + (batch.CompletedWork ?? rawBatch.Results.Count));
        var total = EstimateTmdbWork(rawBatch, completed);
        return batch with
        {
            CompletedWork = Math.Min(completed, total),
            TotalWork = total,
            ProgressText = $"{TmdbPageText(rawBatch)} · processed {Math.Min(completed, total):N0} of {total:N0} rows"
        };
    }

    private static PremiereItemBatch WithTmdbMetadataProgress<T>(
        PremiereItemBatch batch,
        TmdbDiscoverBatch<T> rawBatch,
        int completedRawItemsBeforeBatch)
    {
        var completed = Math.Max(0, completedRawItemsBeforeBatch + rawBatch.Results.Count);
        var total = EstimateTmdbWork(rawBatch, completed);
        return batch with
        {
            CompletedWork = Math.Min(completed, total),
            TotalWork = total,
            ProgressText = $"{TmdbPageText(rawBatch)} · metadata {Math.Min(completed, total):N0} of {total:N0} rows"
        };
    }

    private static PremiereItemBatch WithCandidateProgress(PremiereItemBatch batch, int candidateCount)
    {
        var total = Math.Max(0, candidateCount);
        var completed = Math.Clamp(batch.CompletedWork ?? total, 0, Math.Max(1, total));
        var unmapped = CountUnverified(batch.Items);
        var progressText = total == 1
            ? "resolved 1 of 1 candidate"
            : $"resolved {completed:N0} of {total:N0} candidates";
        if (unmapped > 0)
        {
            progressText = $"{progressText} · {unmapped:N0} unverified";
        }

        return batch with
        {
            CompletedWork = completed,
            TotalWork = total,
            ProgressText = progressText,
            UnmappedCount = unmapped
        };
    }

    private static string SourceCompletionProgressText(
        int itemCount,
        string? previousProgressText,
        int filteredCount = 0)
    {
        var summary = itemCount == 0 ? "Done - no matching cards" : "Done";
        var progressText = ProgressTextWithFilteredCount(previousProgressText, filteredCount);
        return string.IsNullOrWhiteSpace(progressText)
            ? summary
            : $"{summary} - {progressText}";
    }

    private static string? ProgressTextWithFilteredCount(string? progressText, int filteredCount)
    {
        if (filteredCount <= 0)
        {
            return progressText;
        }

        var filteredText = filteredCount == 1
            ? "1 filtered by active filters"
            : $"{filteredCount:N0} filtered by active filters";
        return string.IsNullOrWhiteSpace(progressText)
            ? filteredText
            : $"{progressText} · {filteredText}";
    }

    private static string SourceFailureProgressText(Exception error)
    {
        return error is OperationCanceledException
            ? "Skipped - source timed out"
            : "Skipped - source failed";
    }

    private static int EstimateTmdbWork<T>(TmdbDiscoverBatch<T> rawBatch, int completed)
    {
        var pageLimitedTotal = rawBatch.TotalPages > 0
            ? rawBatch.TotalPages * 20
            : rawBatch.TotalResults;
        var total = rawBatch.TotalResults > 0
            ? Math.Min(rawBatch.TotalResults, pageLimitedTotal)
            : rawBatch.Results.Count;

        return Math.Max(Math.Max(total, completed), 1);
    }

    private static string TmdbPageText<T>(TmdbDiscoverBatch<T> rawBatch)
    {
        var pageStart = Math.Max(1, rawBatch.PageStart);
        var pageEnd = Math.Max(pageStart, rawBatch.PageEnd);
        var totalPages = Math.Max(1, rawBatch.TotalPages);
        return $"pages {pageStart:N0}-{pageEnd:N0} of {totalPages:N0}";
    }

}
