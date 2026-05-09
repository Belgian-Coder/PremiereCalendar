using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class TmdbFilterCatalogServiceTests
{
    [Fact]
    public async Task GetCatalogAsync_MergesSameNameGlobalProvidersWhenNoSourceRegionsAreConfigured()
    {
        var tmdb = new FakeTmdbClient
        {
            Providers =
            {
                [(PremiereMediaType.Movie, "")] =
                [
                    Provider(9, "Amazon Prime Video ", 3),
                    Provider(119, "Amazon Prime Video", 1),
                    Provider(8, "Netflix", 0)
                ]
            }
        };

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new TmdbFilterCatalogService(
            tmdb,
            cache,
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions()),
            NullLogger<TmdbFilterCatalogService>.Instance);

        var catalog = await service.GetCatalogAsync(CancellationToken.None, forceRefresh: true);

        var amazonOption = Assert.Single(catalog.MovieProviders, option => option.Label == "Amazon Prime Video");
        Assert.True(SourceFilterValue.TryGetProviderIds(amazonOption.Value, out var providerIds));
        Assert.Equal([9, 119], providerIds);
        Assert.Equal("Netflix", Assert.Single(catalog.MovieProviders, option => option.Label == "Netflix").Label);
    }

    [Fact]
    public async Task GetCatalogAsync_AddsRegionSuffixesForDuplicateProviderNames()
    {
        var tmdb = new FakeTmdbClient
        {
            Providers =
            {
                [(PremiereMediaType.Movie, "BE")] =
                [
                    Provider(119, "Amazon Prime Video", 1),
                    Provider(8, "Netflix", 0)
                ],
                [(PremiereMediaType.Movie, "AU")] =
                [
                    Provider(119, "Amazon Prime Video", 1),
                    Provider(8, "Netflix", 0)
                ],
                [(PremiereMediaType.Movie, "US")] =
                [
                    Provider(9, "Amazon Prime Video", 3),
                    Provider(8, "Netflix", 0)
                ],
                [(PremiereMediaType.Movie, "GB")] =
                [
                    Provider(9, "Amazon Prime Video", 3),
                    Provider(8, "Netflix", 0)
                ]
            }
        };

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new TmdbFilterCatalogService(
            tmdb,
            cache,
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions { SourceRegions = ["BE", "US", "GB", "AU"] }),
            NullLogger<TmdbFilterCatalogService>.Instance);

        var catalog = await service.GetCatalogAsync(CancellationToken.None, forceRefresh: true);

        Assert.Contains(catalog.MovieProviders, option =>
            option.Value == SourceFilterValue.Provider(119, "Amazon Prime Video")
            && option.Label == "Amazon Prime Video (BE/AU)");
        Assert.Contains(catalog.MovieProviders, option =>
            option.Value == SourceFilterValue.Provider(9, "Amazon Prime Video")
            && option.Label == "Amazon Prime Video (US/GB)");
        Assert.Contains(catalog.MovieProviders, option =>
            option.Value == SourceFilterValue.Provider(8, "Netflix")
            && option.Label == "Netflix (BE/US/GB/AU)");
    }

    private static TmdbWatchProvider Provider(int id, string name, int displayPriority)
    {
        return new TmdbWatchProvider
        {
            ProviderId = id,
            ProviderName = name,
            DisplayPriority = displayPriority
        };
    }

    private sealed class FakeTmdbClient : ITmdbClient
    {
        public Dictionary<(PremiereMediaType MediaType, string Region), IReadOnlyList<TmdbWatchProvider>> Providers { get; } = [];

        public Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(
            PremiereMediaType mediaType,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbGenre>>([]);
        }

        public Task<IReadOnlyList<TmdbConfigurationLanguage>> GetLanguagesAsync(
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbConfigurationLanguage>>([]);
        }

        public Task<IReadOnlyList<TmdbConfigurationCountry>> GetCountriesAsync(
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbConfigurationCountry>>([]);
        }

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(
            PremiereMediaType mediaType,
            string region,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult(Providers.GetValueOrDefault((mediaType, region), []));
        }

        public Task<TmdbCertificationResponse?> GetCertificationsAsync(
            PremiereMediaType mediaType,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<TmdbCertificationResponse?>(new TmdbCertificationResponse());
        }

        public Task<IReadOnlyList<TmdbTvDiscoverItem>> DiscoverTvAsync(
            DateOnly start,
            DateOnly end,
            TmdbDiscoverFilters filters,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<TmdbDiscoverBatch<TmdbTvDiscoverItem>> StreamDiscoverTvAsync(
            DateOnly start,
            DateOnly end,
            TmdbDiscoverFilters filters,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<TmdbTvDiscoverItem>> DiscoverTvByNetworksAsync(
            DateOnly start,
            DateOnly end,
            IReadOnlyList<int> networkIds,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<TmdbMovieDiscoverItem>> DiscoverMoviesAsync(
            DateOnly start,
            DateOnly end,
            TmdbDiscoverFilters filters,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<TmdbDiscoverBatch<TmdbMovieDiscoverItem>> StreamDiscoverMoviesAsync(
            DateOnly start,
            DateOnly end,
            TmdbDiscoverFilters filters,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            throw new NotSupportedException();
        }

        public Task<TmdbDetailsWithExtras?> GetTvDetailsAsync(
            int id,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            throw new NotSupportedException();
        }

        public Task<TmdbDetailsWithExtras?> GetMovieDetailsAsync(
            int id,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            throw new NotSupportedException();
        }

        public Task<int?> FindTmdbIdByExternalIdAsync(
            PremiereMediaType mediaType,
            string externalId,
            string externalSource,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(
            string query,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            throw new NotSupportedException();
        }
    }
}
