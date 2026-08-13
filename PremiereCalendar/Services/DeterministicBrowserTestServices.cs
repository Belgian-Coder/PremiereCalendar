using System.Runtime.CompilerServices;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

internal sealed class DeterministicBrowserPremiereService : IPremiereService
{
    public async IAsyncEnumerable<PremiereLoadProgress> StreamPremieresAsync(
        DateOnly start, DateOnly end, bool forceRefresh = false, CalendarFilters? filters = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var items = CreateItems(start, end);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new PremiereLoadProgress(
            "Deterministic fixture", items.Count, items.Count, items, IsFinal: true,
            CompletedWork: items.Count, TotalWork: items.Count,
            ProgressText: "Deterministic provider fixture complete.", ElapsedMilliseconds: 1);
    }

    public Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(
        DateOnly start, DateOnly end, CancellationToken cancellationToken, bool forceRefresh = false,
        IProgress<PremiereLoadProgress>? progress = null, CalendarFilters? filters = null)
    {
        IReadOnlyList<PremiereItem> items = CreateItems(start, end);
        progress?.Report(new PremiereLoadProgress("Deterministic fixture", items.Count, items.Count, items, true));
        return Task.FromResult(items);
    }

    private static IReadOnlyList<PremiereItem> CreateItems(DateOnly start, DateOnly end)
    {
        var items = new List<PremiereItem>();
        var index = 1;
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            for (var itemIndex = 0; itemIndex < 12; itemIndex++, index++)
            {
                var movie = itemIndex % 3 == 0;
                items.Add(new PremiereItem
                {
                    CanonicalId = $"browser:{day:yyyyMMdd}:{itemIndex}",
                    Type = movie ? PremiereItemType.MovieFirstRelease : itemIndex % 2 == 0 ? PremiereItemType.SeriesPremiere : PremiereItemType.SeriesEpisode,
                    MediaType = movie ? PremiereMediaType.Movie : PremiereMediaType.Series,
                    TmdbId = index,
                    Title = movie ? $"Fixture Movie {index}" : $"Fixture Series {index}",
                    EpisodeTitle = movie ? null : $"Fixture episode {index}",
                    SeasonNumber = movie ? null : 1,
                    EpisodeNumber = movie ? null : itemIndex + 1,
                    PremiereDate = day,
                    Overview = $"Deterministic description for item {index}.",
                    OriginalLanguage = itemIndex % 2 == 0 ? "en" : "nl",
                    OriginCountries = itemIndex % 2 == 0 ? ["US"] : ["BE"],
                    SourceNames = itemIndex % 2 == 0 ? ["Fixture Stream"] : ["Fixture Broadcast"],
                    Genres = movie ? ["Drama"] : ["Comedy"],
                    RuntimeMinutes = movie ? 105 : 48,
                    TmdbScore = 6 + (index % 30) / 10d,
                    TmdbVoteCount = 100 + index,
                    ImdbScore = 6.5 + (index % 20) / 10d,
                    TrailerUrl = itemIndex % 2 == 0 ? $"https://example.test/trailer/{index}" : null,
                    TmdbUrl = $"https://example.test/title/{index}"
                });
            }
        }
        return items;
    }
}

internal sealed class DeterministicBrowserFilterCatalogService : IFilterCatalogService
{
    private static readonly FilterCatalog Catalog = new()
    {
        MovieGenres = [new FilterOption("18", "Drama")],
        SeriesGenres = [new FilterOption("35", "Comedy")],
        Languages = [new FilterOption("en", "English"), new FilterOption("nl", "Dutch")],
        Countries = [new FilterOption("BE", "Belgium"), new FilterOption("US", "United States")],
        MovieProviders = [new FilterOption("fixture", "Fixture Stream")],
        SeriesProviders = [new FilterOption("fixture", "Fixture Broadcast")]
    };

    public Task<FilterCatalog> GetCatalogAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        => Task.FromResult(Catalog);
}

internal sealed class DeterministicBrowserIntegrationSettingsStore : IIntegrationSettingsStore
{
    private IntegrationSettings _settings = new()
    {
        Sources = new SourceIntegrationSettings
        {
            Tmdb = new TmdbSourceSettings { BearerToken = "deterministic-browser-token" }
        }
    };

    public Task<IntegrationSettings> GetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_settings);

    public Task SaveAsync(IntegrationSettings settings, CancellationToken cancellationToken = default)
    {
        _settings = settings;
        return Task.CompletedTask;
    }
}
