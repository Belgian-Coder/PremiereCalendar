using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class TmdbFilterCatalogService : IFilterCatalogService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(1);

    private readonly ITmdbClient _tmdbClient;
    private readonly IMemoryCache _cache;
    private readonly TmdbOptions _options;
    private readonly ILogger<TmdbFilterCatalogService> _logger;

    public TmdbFilterCatalogService(
        ITmdbClient tmdbClient,
        IMemoryCache cache,
        IOptions<TmdbOptions> options,
        ILogger<TmdbFilterCatalogService> logger)
    {
        _tmdbClient = tmdbClient;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<FilterCatalog> GetCatalogAsync(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        const string cacheKey = "tmdb:filter-catalog";
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out FilterCatalog? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var movieGenresTask = _tmdbClient.GetGenresAsync(PremiereMediaType.Movie, cancellationToken, forceRefresh);
            var seriesGenresTask = _tmdbClient.GetGenresAsync(PremiereMediaType.Series, cancellationToken, forceRefresh);
            var languagesTask = _tmdbClient.GetLanguagesAsync(cancellationToken, forceRefresh);
            var countriesTask = _tmdbClient.GetCountriesAsync(cancellationToken, forceRefresh);
            var movieCertificationsTask = _tmdbClient.GetCertificationsAsync(PremiereMediaType.Movie, cancellationToken, forceRefresh);
            var seriesCertificationsTask = _tmdbClient.GetCertificationsAsync(PremiereMediaType.Series, cancellationToken, forceRefresh);
            var movieProvidersTask = GetProviderOptionsAsync(PremiereMediaType.Movie, cancellationToken, forceRefresh);
            var seriesProvidersTask = GetProviderOptionsAsync(PremiereMediaType.Series, cancellationToken, forceRefresh);

            await Task.WhenAll(
                movieGenresTask,
                seriesGenresTask,
                languagesTask,
                countriesTask,
                movieCertificationsTask,
                seriesCertificationsTask,
                movieProvidersTask,
                seriesProvidersTask);

            var catalog = new FilterCatalog
            {
                MovieGenres = GenreOptions(await movieGenresTask),
                SeriesGenres = GenreOptions(await seriesGenresTask),
                Languages = LanguageOptions(await languagesTask),
                Countries = CountryOptions(await countriesTask),
                MovieProviders = await movieProvidersTask,
                SeriesProviders = await seriesProvidersTask,
                MovieCertifications = CertificationOptions(await movieCertificationsTask),
                SeriesCertifications = CertificationOptions(await seriesCertificationsTask)
            };

            _cache.Set(cacheKey, catalog, CacheDuration);
            return catalog;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not load TMDb filter catalog; using local fallback values.");
            return FallbackCatalog;
        }
    }

    private async Task<IReadOnlyList<FilterOption>> GetProviderOptionsAsync(
        PremiereMediaType mediaType,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var regions = _options.SourceRegions
            .Where(region => !string.IsNullOrWhiteSpace(region))
            .Select(region => region.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (regions.Length == 0)
        {
            regions = [""];
        }

        var tasks = regions.Select(async region => new RegionalProviderGroup(
            region,
            await _tmdbClient.GetWatchProvidersAsync(mediaType, region, cancellationToken, forceRefresh)));
        var providerGroups = await Task.WhenAll(tasks);

        return ProviderOptions(providerGroups
            .SelectMany(group => group.Providers.Select(provider => new RegionalProvider(provider, group.Region))));
    }

    private IReadOnlyList<FilterOption> ProviderOptions(IEnumerable<RegionalProvider> providers)
    {
        var providerEntries = providers
            .Where(entry => entry.Provider.ProviderId > 0 && !string.IsNullOrWhiteSpace(entry.Provider.ProviderName))
            .ToArray();

        var configuredRegions = _options.SourceRegions
            .Where(region => !string.IsNullOrWhiteSpace(region))
            .ToArray();

        if (configuredRegions.Length == 0)
        {
            return providerEntries
                .GroupBy(entry => entry.Provider.ProviderName!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var ordered = group
                        .OrderBy(entry => entry.Provider.DisplayPriority ?? int.MaxValue)
                        .ThenBy(entry => entry.Provider.ProviderId)
                        .ToArray();
                    var preferred = ordered[0];
                    var providerName = preferred.Provider.ProviderName!.Trim();

                    return new FilterOption(
                        SourceFilterValue.Providers(ordered.Select(entry => entry.Provider.ProviderId), providerName),
                        providerName);
                })
                .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var groups = providerEntries
            .GroupBy(
                entry => entry.Provider.ProviderId,
                entry => entry,
                EqualityComparer<int>.Default)
            .Select(group =>
            {
                var preferred = group
                    .OrderBy(entry => entry.Provider.DisplayPriority ?? int.MaxValue)
                    .ThenBy(entry => entry.Provider.ProviderName, StringComparer.OrdinalIgnoreCase)
                    .First();

                var regions = group
                    .Select(entry => entry.Region)
                    .Where(region => !string.IsNullOrWhiteSpace(region))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(SourceRegionOrder)
                    .ThenBy(region => region, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var providerName = preferred.Provider.ProviderName!.Trim();
                var label = regions.Length == 0
                    ? providerName
                    : $"{providerName} ({string.Join('/', regions)})";

                return new FilterOption(
                    SourceFilterValue.Provider(preferred.Provider.ProviderId, providerName),
                    label);
            })
            .ToArray();

        return groups
            .OrderBy(option => SourceFilterValue.Label(option.Value), StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<FilterOption> GenreOptions(IEnumerable<TmdbGenre> genres)
    {
        return genres
            .Where(genre => genre.Id > 0 && !string.IsNullOrWhiteSpace(genre.Name))
            .Select(genre => new FilterOption(genre.Id.ToString(CultureInfo.InvariantCulture), genre.Name!))
            .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<FilterOption> LanguageOptions(IEnumerable<TmdbConfigurationLanguage> languages)
    {
        return languages
            .Where(language => !string.IsNullOrWhiteSpace(language.Iso6391))
            .Select(language =>
            {
                var label = !string.IsNullOrWhiteSpace(language.EnglishName) && language.EnglishName != "No Language"
                    ? language.EnglishName!
                    : language.Name ?? language.Iso6391!;
                return new FilterOption(language.Iso6391!, $"{label} ({language.Iso6391!.ToUpperInvariant()})");
            })
            .GroupBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<FilterOption> CountryOptions(IEnumerable<TmdbConfigurationCountry> countries)
    {
        return countries
            .Where(country => !string.IsNullOrWhiteSpace(country.Iso31661))
            .Select(country =>
            {
                var label = !string.IsNullOrWhiteSpace(country.EnglishName)
                    ? country.EnglishName!
                    : country.NativeName ?? country.Iso31661!;
                return new FilterOption(country.Iso31661!, $"{label} ({country.Iso31661!.ToUpperInvariant()})");
            })
            .GroupBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private int SourceRegionOrder(string region)
    {
        var index = Array.FindIndex(
            _options.SourceRegions,
            configured => string.Equals(configured, region, StringComparison.OrdinalIgnoreCase));

        return index >= 0 ? index : int.MaxValue;
    }

    private static IReadOnlyList<FilterOption> CertificationOptions(TmdbCertificationResponse? response)
    {
        if (response?.Certifications is not { Count: > 0 } certifications)
        {
            return [];
        }

        return certifications
            .SelectMany(region => region.Value
                .Where(certification => !string.IsNullOrWhiteSpace(certification.Certification))
                .Select(certification => new FilterOption(
                    $"{region.Key}:{certification.Certification}",
                    $"{region.Key} {certification.Certification}")))
            .DistinctBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static readonly FilterCatalog FallbackCatalog = new()
    {
        MovieGenres =
        [
            new("28", "Action"),
            new("12", "Adventure"),
            new("16", "Animation"),
            new("35", "Comedy"),
            new("80", "Crime"),
            new("99", "Documentary"),
            new("18", "Drama"),
            new("10751", "Family"),
            new("14", "Fantasy"),
            new("36", "History"),
            new("27", "Horror"),
            new("10402", "Music"),
            new("9648", "Mystery"),
            new("10749", "Romance"),
            new("878", "Science Fiction"),
            new("10770", "TV Movie"),
            new("53", "Thriller"),
            new("10752", "War"),
            new("37", "Western")
        ],
        SeriesGenres =
        [
            new("10759", "Action & Adventure"),
            new("16", "Animation"),
            new("35", "Comedy"),
            new("80", "Crime"),
            new("99", "Documentary"),
            new("18", "Drama"),
            new("10751", "Family"),
            new("10762", "Kids"),
            new("9648", "Mystery"),
            new("10763", "News"),
            new("10764", "Reality"),
            new("10765", "Sci-Fi & Fantasy"),
            new("10766", "Soap"),
            new("10767", "Talk"),
            new("10768", "War & Politics"),
            new("37", "Western")
        ],
        Languages =
        [
            new("en", "English (EN)"),
            new("nl", "Dutch (NL)"),
            new("fr", "French (FR)")
        ],
        Countries =
        [
            new("BE", "Belgium (BE)"),
            new("US", "United States of America (US)"),
            new("GB", "United Kingdom (GB)"),
            new("AU", "Australia (AU)")
        ]
    };

    private sealed record RegionalProviderGroup(string Region, IReadOnlyList<TmdbWatchProvider> Providers);

    private sealed record RegionalProvider(TmdbWatchProvider Provider, string Region);
}
