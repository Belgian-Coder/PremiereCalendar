using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class CacheFilterIdentityReviewTests
{
    [Fact]
    public void MixedCountryCertificationsHaveDistinctMembershipAndCacheIdentity()
    {
        var unfiltered = new CalendarFilters { ShowSeries = false };
        var selected = new CalendarFilters { ShowSeries = false, MovieFilters = { Certifications = ["US:PG", "GB:12"] } };
        var rows = new[] { Item(1) with { Certifications = ["US:PG"] }, Item(2) with { Certifications = ["US:R"] } };
        Assert.Equal(2, PremiereFilter.Apply(rows, unfiltered).Count);
        Assert.Single(PremiereFilter.Apply(rows, selected));
        Assert.NotEqual(PremiereDiscoveryCriteria.FromFilters(unfiltered).CacheKey(), PremiereDiscoveryCriteria.FromFilters(selected).CacheKey());
        Assert.Empty(PremiereDiscoveryCriteria.FromFilters(selected).ToTmdbFilters(PremiereMediaType.Movie, []).MovieCertifications);
        var reordered = PremiereDiscoveryCriteria.FromFilters(selected).CacheKey();
        selected.MovieFilters.Certifications.Reverse();
        Assert.Equal(reordered, PremiereDiscoveryCriteria.FromFilters(selected).CacheKey());
        selected.MovieFilters.Certifications = ["gb:12", "us:pg", "US:PG"];
        Assert.Equal(reordered, PremiereDiscoveryCriteria.FromFilters(selected).CacheKey());
    }

    [Fact]
    public void ExplicitCountryDoesNotPushAnIncompleteMixedCountryOrSelectionToTmdb()
    {
        var filters = new CalendarFilters { ShowSeries = false, MovieFilters =
            { CertificationCountry = "US", Certifications = ["US:PG", "GB:12"] } };
        var row = Item(1) with { Certifications = ["US:R", "GB:12"] };
        Assert.Single(PremiereFilter.Apply([row], filters));
        var query = PremiereDiscoveryCriteria.FromFilters(filters).ToTmdbFilters(PremiereMediaType.Movie, []);
        Assert.Equal("US", query.MovieCertificationCountry);
        Assert.Empty(query.MovieCertifications);
    }

    [Fact]
    public void ExistingDefaultMediaCacheKeysRemainStable()
    {
        var movies = CalendarFilters.CreateDefault();
        movies.ShowSeries = false;
        Assert.Equal("1e2f7dc0cb5893cdfb37", PremiereDiscoveryCriteria.FromFilters(movies).CacheKey());
        var series = CalendarFilters.CreateDefault();
        series.ShowMovies = false;
        Assert.Equal("4da65c5f968e894b2adf", PremiereDiscoveryCriteria.FromFilters(series).CacheKey());
    }

    [Theory]
    [InlineData("global-search")]
    [InlineData("global-keyword")]
    [InlineData("global-source")]
    [InlineData("movie-search")]
    [InlineData("movie-keyword")]
    [InlineData("movie-source")]
    public void TextMembershipMatchesCacheKeyWhitespaceNormalization(string field)
    {
        CalendarFilters Filters(bool padded)
        {
            var filters = new CalendarFilters { ShowSeries = false };
            string Value(string text) => padded ? " " + text + " " : text;
            switch (field)
            {
                case "global-search": filters.SearchText = Value("Dune"); break;
                case "global-keyword": filters.KeywordText = Value("Desert"); break;
                case "global-source": filters.NetworkText = Value("Cinema"); break;
                case "movie-search": filters.MovieFilters.SearchText = Value("Dune"); break;
                case "movie-keyword": filters.MovieFilters.KeywordText = Value("Desert"); break;
                case "movie-source": filters.MovieFilters.SourceText = Value("Cinema"); break;
            }
            return filters;
        }
        var clean = Filters(false);
        var padded = Filters(true);
        Assert.Equal(PremiereDiscoveryCriteria.FromFilters(clean).CacheKey(), PremiereDiscoveryCriteria.FromFilters(padded).CacheKey());
        Assert.Equal(new[] { 1 }, PremiereFilter.Apply([Item(1)], clean).Select(item => item.TmdbId));
        Assert.Equal(new[] { 1 }, PremiereFilter.Apply([Item(1)], padded).Select(item => item.TmdbId));
    }

    private static PremiereItem Item(int id) => new()
    {
        CanonicalId = "movie:" + id, TmdbId = id, MediaType = PremiereMediaType.Movie,
        Type = PremiereItemType.MovieFirstRelease, Title = "Dune", PremiereDate = new(2026, 11, 2),
        OriginalLanguage = "en", SourceNames = ["Cinema"], Keywords = ["Desert"]
    };
}

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedOptionalDenialAndUndeferredFailureFinishesPartialWithoutAutomaticDeadline(bool hasRows)
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var other = new DegradedThrowingProvider("Other source", new ExternalApiException("Fixture outage"));
        var final = await DegradedFinalAsync(CreateService(hasRows ? ItemCacheMovie(date) : new FakeTmdbClient(),
            omdb: ItemCacheOmdb("7.1"), calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(new DegradedTraktClient()), other]), date);
        Assert.True(final.HasSourceErrors);
        Assert.False(final.IsReusableDegraded);
        Assert.False(final.IsDegradedResult);
        Assert.Null(final.RetryAfterUtc);
        Assert.Equal("trakt", Assert.Single(final.UnavailableSources).Provider);
        Assert.Equal(hasRows ? 1 : 0, final.Items.Count);
        Assert.Contains("Other source", final.FailedSourceNames);
        Assert.False((await files.ReadAsync(date)).CanReuseDegraded(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task MixedFailuresInsideOneOptionalSourceDoNotTurnItsDenialIntoInfiniteRetry()
    {
        var date = new DateOnly(2026, 11, 2);
        var error = new AggregateException(new ProviderRequestException("trakt",
            new("authentication_denied", 403, DateTimeOffset.UtcNow.AddMinutes(15))),
            new HttpRequestException("Fixture branch failure", null, System.Net.HttpStatusCode.InternalServerError));
        var final = await DegradedFinalAsync(CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            discoveryProviders: [new DegradedThrowingProvider("Trakt", error)]), date);
        Assert.True(final.HasSourceErrors);
        Assert.False(final.IsDegradedResult);
        Assert.False(final.IsReusableDegraded);
        Assert.Null(final.RetryAfterUtc);
        Assert.Equal("trakt", Assert.Single(final.UnavailableSources).Provider);
    }

    [Fact]
    public async Task MixedQuotaFailureRetainsItsRealRecoveryDeadline()
    {
        var date = new DateOnly(2026, 11, 2);
        var quota = new DegradedThrowingProvider("Other source", new ProviderRequestException("other",
            new("rate_limited", 429, DateTimeOffset.UtcNow.AddMinutes(1))));
        var final = await DegradedFinalAsync(CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            discoveryProviders: [new TraktDiscoveryProvider(new DegradedTraktClient()), quota]), date);
        Assert.True(final.HasSourceErrors);
        Assert.False(final.IsDegradedResult);
        Assert.False(final.IsReusableDegraded);
        Assert.True(final.RetryAfterUtc > DateTimeOffset.UtcNow);
        Assert.Equal(2, final.FailedSourceNames.Count);
    }
}
