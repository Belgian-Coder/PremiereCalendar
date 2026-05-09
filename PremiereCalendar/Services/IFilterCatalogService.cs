using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IFilterCatalogService
{
    Task<FilterCatalog> GetCatalogAsync(CancellationToken cancellationToken, bool forceRefresh = false);
}

public sealed record FilterCatalog
{
    public IReadOnlyList<FilterOption> MovieGenres { get; init; } = [];
    public IReadOnlyList<FilterOption> SeriesGenres { get; init; } = [];
    public IReadOnlyList<FilterOption> Languages { get; init; } = [];
    public IReadOnlyList<FilterOption> Countries { get; init; } = [];
    public IReadOnlyList<FilterOption> MovieProviders { get; init; } = [];
    public IReadOnlyList<FilterOption> SeriesProviders { get; init; } = [];
    public IReadOnlyList<FilterOption> MovieCertifications { get; init; } = [];
    public IReadOnlyList<FilterOption> SeriesCertifications { get; init; } = [];
}

public sealed record FilterOption(string Value, string Label);
