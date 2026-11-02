using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task NewSeriesOnlyRevalidatesFirstDebutBeforeReusingPreviouslyMisclassifiedCachedSeries()
    {
        var date = new DateOnly(2026, 5, 4);
        var cached = new PremiereItem
        {
            CanonicalId = "tv:182", Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series, TmdbId = 182, TvdbId = 82,
            Title = "Previously misclassified show", PremiereDate = date,
            VerificationState = PremiereVerificationState.Verified,
            LastUpdatedUtc = DateTimeOffset.UtcNow, ImdbScore = 7
        };
        var discovery = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(
                PremiereMediaType.Series, date, cached.Title, null, null, 82, "Schedule provider")]
        };
        var tmdb = new FakeTmdbClient();
        tmdb.SeasonOneEpisodeOneDatesById[182] = date.AddYears(-3);
        var service = CreateService(tmdb, discoveryProviders: [discovery],
            calendarCache: new FakeCalendarCache { Items = [cached] });

        var items = await service.GetPremieresAsync(date, date.AddDays(6), CancellationToken.None,
            forceRefresh: true, filters: NewSeriesOnlyFilters());

        Assert.Empty(items);
        Assert.Empty(tmdb.FindCalls);
    }

    [Fact]
    public async Task NewSeriesOnlyRejectsShowLevelCandidateWithKnownEarlierDebutBeforeResolution()
    {
        var date = new DateOnly(2026, 5, 4);
        var discovery = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(
                PremiereMediaType.Series, date, "Returning show", null, null, 82,
                "Schedule provider", SeriesPremiereDate: date.AddYears(-3))]
        };
        var tmdb = new FakeTmdbClient();
        var service = CreateService(tmdb, discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(date, date.AddDays(6), CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        Assert.Empty(items);
        Assert.Empty(tmdb.FindCalls);
    }

    [Fact]
    public async Task NewSeriesOnlyValidatesFirstDebutAfterResolvingAnExternalSeriesId()
    {
        var date = new DateOnly(2026, 5, 4);
        var discovery = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(
                PremiereMediaType.Series, date, "Resolved returning show", null, null, 82,
                "Schedule provider")]
        };
        var tmdb = new FakeTmdbClient();
        tmdb.FindResults[(PremiereMediaType.Series, "tvdb_id", "82")] = 182;
        tmdb.SeasonOneEpisodeOneDatesById[182] = date.AddYears(-3);
        var service = CreateService(tmdb, discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(date, date.AddDays(6), CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        Assert.Empty(items);
        Assert.Single(tmdb.FindCalls);
    }
}
