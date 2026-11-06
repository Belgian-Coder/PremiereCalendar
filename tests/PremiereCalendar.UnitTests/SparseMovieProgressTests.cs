using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SparseForceAllMovieProgressPreservesCompletedDetails(bool includeBackdrop)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var tmdb = SparseMovieProgressClient(date, includeBackdrop);
        var service = CreateService(tmdb, itemEnrichmentCache: ItemCacheCoordinator(store, clock));

        var result = Assert.Single(await service.GetPremieresAsync(date, date, default, forceRefresh: true,
            filters: new CalendarFilters { ShowSeries = false, MovieFilters = { OriginalLanguages = ["en"] } }));

        Assert.Equal(90, result.RuntimeMinutes);
        Assert.Equal("Completed sparse synopsis", result.Overview);
        Assert.Equal("Completed original title", result.OriginalTitle);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.Equal(result.Overview, metadata.Overview);
        Assert.Equal(result.OriginalTitle, metadata.OriginalTitle);
        Assert.Equal(result.BackdropUrl, metadata.BackdropUrl);
        Assert.False(metadata.CoreMetadataOnly);
        if (includeBackdrop) Assert.EndsWith("/sparse-backdrop.jpg", result.BackdropUrl);
        else Assert.Null(result.BackdropUrl);
        Assert.True(Assert.Single(tmdb.MovieDetailCalls).ForceRefresh);
    }

    [Fact]
    public async Task LateSparseMovieHintsCannotReplaceCompletedSourceDetails()
    {
        var date = new DateOnly(2026, 11, 2);
        var tmdb = SparseMovieProgressClient(date, includeBackdrop: false, duplicatePages: true);
        var reports = new List<PremiereLoadProgress>();
        var result = Assert.Single(await CreateService(tmdb).GetPremieresAsync(date, date, default, forceRefresh: true,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add),
            filters: new CalendarFilters { ShowSeries = false, MovieFilters = { OriginalLanguages = ["en"] } }));

        // The second page repeats the discovery/core hints after page one's
        // completed mapping. Those hints must retain the completed source row.
        var lateHints = reports.Where(report => report.ProgressText?.Contains("pages 2-2", StringComparison.Ordinal) == true
            && report.ProgressText.Contains("metadata", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(lateHints);
        Assert.All(lateHints, report =>
        {
            var item = Assert.Single(report.Items);
            Assert.Equal("Completed sparse synopsis", item.Overview);
            Assert.Equal("Completed original title", item.OriginalTitle);
        });
        Assert.Equal("Completed sparse synopsis", result.Overview);
        Assert.Equal("Completed original title", result.OriginalTitle);
        Assert.Equal(2, tmdb.MovieDetailCalls.Count);
    }

    private static FakeTmdbClient SparseMovieProgressClient(DateOnly date, bool includeBackdrop, bool duplicatePages = false)
    {
        var discovery = new TmdbMovieDiscoverItem
        {
            Id = 901, Title = "Sparse movie", ReleaseDate = date.ToString("yyyy-MM-dd"), OriginalLanguage = "en"
        };
        var client = new FakeTmdbClient
        {
            MovieItems = [discovery],
            MovieStreamBatches = duplicatePages ? [new(1, 1, 2, 2, [discovery]), new(2, 2, 2, 2, [discovery])] : null
        };
        client.MovieDetailsById[901] = new()
        {
            Id = 901, Title = discovery.Title, OriginalLanguage = "en", Runtime = 90,
            Overview = "Completed sparse synopsis", OriginalTitle = "Completed original title",
            BackdropPath = includeBackdrop ? "/sparse-backdrop.jpg" : null
        };
        return client;
    }
}
