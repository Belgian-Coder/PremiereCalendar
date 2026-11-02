using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NightlySnapshotRefreshDiscoversWithProviderTtlReusesEnrichmentAndDropsRemovedRows(bool combinedMedia)
    {
        var start = new DateOnly(2026, 11, 2);
        PremiereItem Cached(int id) => new()
        {
            CanonicalId = $"movie:{id}", TmdbId = id, MediaType = PremiereMediaType.Movie,
            Type = PremiereItemType.MovieFirstRelease, Title = $"Movie {id}", PremiereDate = start,
            OriginalLanguage = "en", RuntimeMinutes = 100, Overview = "Cached enrichment",
            PosterUrl = "https://example.test/poster.jpg", ImdbId = $"tt000000{id}", LastUpdatedUtc = DateTimeOffset.UtcNow
        };
        var cache = new FakeCalendarCache { Items = [Cached(1), Cached(2)] };
        var tmdb = new FakeTmdbClient
        {
            MovieItems = [new TmdbMovieDiscoverItem { Id = 2, Title = "Movie 2", ReleaseDate = "2026-11-02", OriginalLanguage = "en" }],
            // Reusing normalized enrichment must avoid the detail endpoint entirely.
            MovieDetailExceptions = { [2] = new InvalidOperationException("Cached enrichment was not reused") }
        };
        var filters = CalendarFilters.CreateDefault();
        filters.ShowSeries = combinedMedia;
        var updates = new List<PremiereLoadProgress>();
        var service = CreateService(tmdb, calendarCache: cache);
        await foreach (var update in service.StreamCoreAsync(start, start.AddDays(6), false, filters,
                           new ProviderWorkResumeState(new Dictionary<string, IReadOnlyList<PremiereItem>>(), null)
                           { RefreshCalendarSnapshot = true })) updates.Add(update);

        var final = updates.Last();
        Assert.True(final.IsFinal);
        Assert.False(final.FromCache);
        Assert.False(final.HasSourceErrors);
        Assert.Equal("movie:2", Assert.Single(final.Items).CanonicalId);
        Assert.Equal("Cached enrichment", final.Items[0].Overview);
        Assert.NotEmpty(tmdb.MovieCalls);
        Assert.All(tmdb.MovieCalls, call => Assert.False(call.ForceRefresh));
        if (combinedMedia) Assert.NotEmpty(tmdb.TvCalls); // Both fresh media cohorts must still be rediscovered.
        Assert.All(tmdb.TvCalls, call => Assert.False(call.ForceRefresh));
        Assert.DoesNotContain(cache.SetCalls.SelectMany(call => call.Items), item => item.CanonicalId == "movie:1");
    }

    [Fact]
    public async Task NightlySnapshotFailureKeepsPriorRowsProvisionalAndDoesNotRenewCompleteCache()
    {
        var start = new DateOnly(2026, 11, 2);
        var prior = ReliabilityItems(start, 1);
        var cache = new FakeCalendarCache { Items = prior };
        var service = CreateService(new FakeTmdbClient(), calendarCache: cache, discoveryProviders: [new ReliabilityFailedProvider()]);
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in service.StreamCoreAsync(start, start.AddDays(6), false,
                           new CalendarFilters { ShowSeries = false },
                           new ProviderWorkResumeState(new Dictionary<string, IReadOnlyList<PremiereItem>>(), null)
                           { RefreshCalendarSnapshot = true })) updates.Add(update);
        Assert.True(updates.Last().HasSourceErrors);
        Assert.Equal(prior[0].CanonicalId, Assert.Single(updates.Last().Items).CanonicalId);
        Assert.Empty(cache.SetCalls);
    }
}
