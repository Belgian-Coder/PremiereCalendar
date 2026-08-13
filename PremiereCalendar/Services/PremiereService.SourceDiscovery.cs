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
    private async Task<IReadOnlyList<PremiereItem>> GetSeriesAsync(
        DateOnly start,
        DateOnly end,
        PremiereDiscoveryCriteria criteria,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Series.KeywordText, cancellationToken, forceRefresh);

        if (criteria.Series.SeriesDateMode == SeriesDateMode.AllEpisodes)
        {
            var dayResults = await Task.WhenAll(EachDay(start, end)
                .Select(day => GetSeriesEpisodesForDayAsync(day, criteria, keywordIds, cancellationToken, forceRefresh)));

            return dayResults.SelectMany(items => items).ToArray();
        }

        var languageRequestValues = LanguageRequestValues(criteria.Series);
        var rawItemGroups = await Task.WhenAll(languageRequestValues.Select(language =>
            _tmdbClient.DiscoverTvAsync(
                start,
                end,
                criteria.ToTmdbFilters(PremiereMediaType.Series, keywordIds, language),
                cancellationToken,
                forceRefresh)));
        var rawItems = rawItemGroups.SelectMany(items => items);

        return await MapWithLimitedConcurrencyAsync(
            rawItems,
            (item, token) => MapSeriesAsync(
                item,
                token,
                forceRefresh,
                requestedStart: start,
                requestedEnd: end,
                canonicalizeSeriesPremiereDate: true),
            cancellationToken);
    }

    private async IAsyncEnumerable<PremiereItemBatch> StreamSeriesBatchesAsync(
        DateOnly start,
        DateOnly end,
        PremiereDiscoveryCriteria criteria,
        string? language,
        bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Series.KeywordText, cancellationToken, forceRefresh);

        var completedRawItems = 0;
        await foreach (var rawBatch in _tmdbClient.StreamDiscoverTvAsync(
                start,
                end,
                criteria.ToTmdbFilters(PremiereMediaType.Series, keywordIds, language),
                cancellationToken,
                forceRefresh)
            .WithCancellation(cancellationToken))
        {
            var metadataItems = await MapWithLimitedConcurrencyAsync(
                rawBatch.Results,
                (item, token) => MapSeriesPremiereMetadataAsync(item, token, forceRefresh, start, end),
                cancellationToken);
            if (metadataItems.Count > 0)
            {
                yield return WithTmdbMetadataProgress(new PremiereItemBatch(metadataItems), rawBatch, completedRawItems);
            }

            await foreach (var mappedBatch in MapInProgressBatchesAsync(
                    rawBatch.Results,
                    (item, token) => MapSeriesAsync(
                        item,
                        token,
                        forceRefresh,
                        cachedEnrichment: cachedEnrichment,
                        requestedStart: start,
                        requestedEnd: end,
                        canonicalizeSeriesPremiereDate: true),
                    cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return WithTmdbProgress(mappedBatch, rawBatch, completedRawItems);
            }

            completedRawItems += rawBatch.Results.Count;
        }
    }

    private async Task<IReadOnlyList<PremiereItem>> GetSeriesEpisodesForDayAsync(
        DateOnly day,
        PremiereDiscoveryCriteria criteria,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Series.KeywordText, cancellationToken, forceRefresh);
        return await GetSeriesEpisodesForDayAsync(day, criteria, keywordIds, cancellationToken, forceRefresh);
    }

    private async Task<IReadOnlyList<PremiereItem>> GetSeriesEpisodesForDayAsync(
        DateOnly day,
        PremiereDiscoveryCriteria criteria,
        IReadOnlyList<int> keywordIds,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var languageRequestValues = LanguageRequestValues(criteria.Series);
        var rawItemGroups = await Task.WhenAll(languageRequestValues.Select(language =>
            _tmdbClient.DiscoverTvAsync(
                day,
                day,
                criteria.ToTmdbFilters(PremiereMediaType.Series, keywordIds, language),
                cancellationToken,
                forceRefresh)));

        var rawItems = rawItemGroups
            .SelectMany(items => items)
            .Select(item => (Date: day, Item: item));

        return await MapWithLimitedConcurrencyAsync(
            rawItems,
            (result, token) => MapSeriesAsync(
                result.Item,
                token,
                forceRefresh,
                premiereDateOverride: result.Date,
                itemTypeOverride: PremiereItemType.SeriesEpisode,
                episodeSource: "TMDb air date"),
            cancellationToken);
    }

    private async IAsyncEnumerable<PremiereItemBatch> StreamSeriesEpisodeBatchesForDayAsync(
        DateOnly day,
        PremiereDiscoveryCriteria criteria,
        string? language,
        bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Series.KeywordText, cancellationToken, forceRefresh);

        var completedRawItems = 0;
        await foreach (var rawBatch in _tmdbClient.StreamDiscoverTvAsync(
                day,
                day,
                criteria.ToTmdbFilters(PremiereMediaType.Series, keywordIds, language),
                cancellationToken,
                forceRefresh)
            .WithCancellation(cancellationToken))
        {
            var metadataItems = rawBatch.Results
                .Select(item => MapSeriesMetadata(
                    item,
                    premiereDateOverride: day,
                    itemTypeOverride: PremiereItemType.SeriesEpisode,
                    episodeSource: "TMDb air date"))
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray();
            if (metadataItems.Length > 0)
            {
                yield return WithTmdbMetadataProgress(new PremiereItemBatch(metadataItems), rawBatch, completedRawItems);
            }

            var rawItems = rawBatch.Results.Select(item => (Date: day, Item: item));
            await foreach (var mappedBatch in MapInProgressBatchesAsync(
                    rawItems,
                    (result, token) => MapSeriesAsync(
                        result.Item,
                        token,
                        forceRefresh,
                        cachedEnrichment,
                        premiereDateOverride: result.Date,
                        itemTypeOverride: PremiereItemType.SeriesEpisode,
                        episodeSource: "TMDb air date"),
                    cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return WithTmdbProgress(mappedBatch, rawBatch, completedRawItems);
            }

            completedRawItems += rawBatch.Results.Count;
        }
    }

    private async Task<IReadOnlyList<PremiereItem>> GetMoviesAsync(
        DateOnly start,
        DateOnly end,
        PremiereDiscoveryCriteria criteria,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Movies.KeywordText, cancellationToken, forceRefresh);
        return await GetMoviesForDateRangeAsync(start, end, criteria, keywordIds, cancellationToken, forceRefresh);
    }

    private async Task<IReadOnlyList<PremiereItem>> GetMoviesForDateRangeAsync(
        DateOnly start,
        DateOnly end,
        PremiereDiscoveryCriteria criteria,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Movies.KeywordText, cancellationToken, forceRefresh);
        return await GetMoviesForDateRangeAsync(start, end, criteria, keywordIds, cancellationToken, forceRefresh);
    }

    private async Task<IReadOnlyList<PremiereItem>> GetMoviesForDateRangeAsync(
        DateOnly start,
        DateOnly end,
        PremiereDiscoveryCriteria criteria,
        IReadOnlyList<int> keywordIds,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var rawItemGroups = await Task.WhenAll(LanguageRequestValues(criteria.Movies).Select(language =>
            _tmdbClient.DiscoverMoviesAsync(
                start,
                end,
                criteria.ToTmdbFilters(PremiereMediaType.Movie, keywordIds, language),
                cancellationToken,
                forceRefresh)));
        var rawItems = rawItemGroups.SelectMany(items => items);

        return await MapWithLimitedConcurrencyAsync(
            rawItems,
            (item, token) => MapMovieAsync(item, token, forceRefresh),
            cancellationToken);
    }

    private async IAsyncEnumerable<PremiereItemBatch> StreamMovieBatchesForDateRangeAsync(
        DateOnly start,
        DateOnly end,
        PremiereDiscoveryCriteria criteria,
        string? language,
        bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Movies.KeywordText, cancellationToken, forceRefresh);

        var completedRawItems = 0;
        await foreach (var rawBatch in _tmdbClient.StreamDiscoverMoviesAsync(
                start,
                end,
                criteria.ToTmdbFilters(PremiereMediaType.Movie, keywordIds, language),
                cancellationToken,
                forceRefresh)
            .WithCancellation(cancellationToken))
        {
            var metadataItems = rawBatch.Results
                .Select(MapMovieMetadata)
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray();
            if (metadataItems.Length > 0)
            {
                yield return WithTmdbMetadataProgress(new PremiereItemBatch(metadataItems), rawBatch, completedRawItems);
            }

            await foreach (var mappedBatch in MapInProgressBatchesAsync(
                    rawBatch.Results,
                    (item, token) => MapMovieAsync(item, token, forceRefresh, cachedEnrichment),
                    cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return WithTmdbProgress(mappedBatch, rawBatch, completedRawItems);
            }

            completedRawItems += rawBatch.Results.Count;
        }
    }


}
