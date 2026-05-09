using Bunit;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using PremiereCalendar.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.ComponentTests;

public sealed class CalendarPageTests : BunitContext
{
    private readonly FakeAdjacentWeekPrefetcher _prefetcher = new();

    public CalendarPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IAdjacentWeekPrefetcher>(_prefetcher);
        Services.AddSingleton<CalendarLoadCoordinator>();
        Services.AddSingleton<ICalendarFilterUsageStore, FakeCalendarFilterUsageStore>();
        Services.AddSingleton<IFilterCatalogService, FakeFilterCatalogService>();
        Services.AddSingleton<IIntegrationSettingsStore, FakeIntegrationSettingsStore>();
        Services.AddSingleton<IArrIntegrationService, FakeArrIntegrationService>();
        Services.Configure<CalendarLoadOptions>(_ => { });
    }

    [Fact]
    public void CalendarPage_SearchTextFiltersVisibleCards()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/movies?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='premiere-card']")));

        component.Find("button[title='Open filters']").Click();
        component.Find("input[aria-label='Movie filters keywords']").Input("south");

        Assert.Single(component.FindAll("[data-testid='premiere-card']"));
        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() =>
        {
            var cards = component.FindAll("[data-testid='premiere-card']");
            var card = Assert.Single(cards);
            Assert.Contains("South Point", card.TextContent);
        });
    }

    [Fact]
    public void CalendarPage_CloseFiltersDiscardsDraftChanges()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='premiere-card']")));

        component.Find("button[title='Open filters']").Click();
        component.Find("input[aria-label='Movie filters keywords']").Input("south");
        component.Find("button[title='Close filters']").Click();

        Assert.Single(component.FindAll("[data-testid='premiere-card']"));
        Assert.Empty(component.FindAll("[data-testid='filter-pane']"));
    }

    [Fact]
    public void CalendarPage_DraftFilterChangesDoNotReloadOrFilterUntilSave()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Single(service.Calls);
            var card = Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Contains("North Star", card.TextContent);
        });

        component.Find("button[title='Open filters']").Click();
        component.Find("input[aria-label='Series filters keywords']").Input("missing");

        Assert.Single(service.Calls);
        Assert.Single(component.FindAll("[data-testid='premiere-card']"));

        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.True(service.Calls.Count >= 2);
            Assert.Empty(component.FindAll("[data-testid='premiere-card']"));
        });
    }

    [Fact]
    public void CalendarPage_NextWeekLoadsNextDateWindow()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls));

        var firstStart = service.Calls[0].Start;
        component.Find("button[title='Next week']").Click();

        component.WaitForAssertion(() => Assert.Equal(2, service.Calls.Count));
        Assert.Equal(firstStart.AddDays(7), service.Calls[1].Start);
        Assert.Equal(firstStart.AddDays(13), service.Calls[1].End);
    }

    [Fact]
    public void CalendarPage_RefreshButtonForcesFreshLoad()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls));

        component.Find("button[title='Refresh premieres']").Click();

        component.WaitForAssertion(() => Assert.True(service.Calls.Count >= 2));
        Assert.True(service.Calls[1].ForceRefresh);
        component.WaitForAssertion(() =>
        {
            Assert.Contains("refresh=true", component.Markup);
            Assert.DoesNotContain("_imageCacheVersion", component.Markup);
            Assert.DoesNotContain("ImageCacheVersion", component.Markup);
        });
    }

    [Fact]
    public void CalendarPage_RefreshButtonRemainsClickableWhileLoadIsActive()
    {
        Services.Configure<CalendarLoadOptions>(options => options.ForegroundLoadBudgetSeconds = 5);
        var service = new FakePremiereService
        {
            ReportPartialProgress = true,
            DelayAfterPartialProgress = TimeSpan.FromSeconds(5),
            SuppressFinalProgress = true
        };
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var refresh = component.Find("button[title='Refresh premieres']");
            Assert.Null(refresh.GetAttribute("disabled"));
            Assert.Contains("Updating results", component.Markup);
        });
    }

    [Fact]
    public void CalendarPage_PrefetchesAdjacentWeeksAfterVisibleWeekLoads()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Single(service.Calls);
            Assert.Contains(_prefetcher.WeekStarts, week => week == service.Calls[0].Start);
        });
    }

    [Fact]
    public void CalendarPage_PrefetchesMovieFiltersAfterForegroundLoadCompletes()
    {
        var coordinator = new CalendarLoadCoordinator();
        var prefetcher = new ForegroundAwareAdjacentWeekPrefetcher(coordinator);
        var service = new FakePremiereService();
        Services.AddSingleton(coordinator);
        Services.AddSingleton<IAdjacentWeekPrefetcher>(prefetcher);
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/movies?week=2026-05-04&movieLang=en,nl&movieRuntimeMin=45");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var call = Assert.Single(prefetcher.Calls);
            Assert.False(call.WasForegroundActive);
            Assert.NotNull(call.Filters);
            Assert.False(call.Filters!.ShowSeries);
            Assert.True(call.Filters.ShowMovies);
            Assert.Equal(["en", "nl"], call.Filters.MovieFilters.OriginalLanguages);
            Assert.Equal(45, call.Filters.MovieFilters.RuntimeMinMinutes);
        });
    }

    [Fact]
    public void CalendarPage_FilterCatalogFailureDoesNotBlockCalendarLoad()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.AddSingleton<IFilterCatalogService>(new ThrowingFilterCatalogService());

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Single(service.Calls);
            Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Empty(component.FindAll("[data-testid='error']"));
        });
    }

    [Fact]
    public void CalendarPage_ReadsFiltersFromQueryStringAndKeepsUrlShareable()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(
            "/?week=2026-05-04&media=movies&sort=score&dir=desc&score=tmdb&min=0.0&max=10.0&unknown=1&minVotes=0&lang=both&origin=all&runtimeMin=0&runtimeMax=360&sources=Apple%20TV&q=south");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var card = Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Contains("South Point", card.TextContent);
        });

        Assert.Contains("media=movies", navigation.Uri);
        Assert.Contains("sources=", navigation.Uri);
        Assert.Contains("q=south", navigation.Uri);
    }

    [Fact]
    public void SeriesPage_LocksCalendarToSeriesAndHidesMediaFilter()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var card = Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Contains("North Star", card.TextContent);
        });
        Assert.Contains("Series Calendar", component.Markup);
        Assert.Empty(component.FindAll("[data-testid='media-filter']"));
    }

    [Fact]
    public void CalendarPage_RouteChangesUpdateVisibleMedia()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);

        var navigation = Services.GetRequiredService<NavigationManager>();
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='premiere-card']")));

        navigation.NavigateTo("/movies");

        component.WaitForAssertion(() =>
        {
            var card = Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Contains("South Point", card.TextContent);
        });

        navigation.NavigateTo("/series");

        component.WaitForAssertion(() =>
        {
            var card = Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Contains("North Star", card.TextContent);
        });
    }

    [Fact]
    public void MoviesPage_LocksCalendarToMoviesAndUsesMovieSourceFilterCopy()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/movies?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var card = Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Contains("South Point", card.TextContent);
        });

        component.Find("button[title='Open filters']").Click();

        Assert.Contains("Movie Calendar", component.Markup);
        Assert.NotEmpty(component.FindAll("[data-testid='movie-where-to-watch-filter']"));
        Assert.Empty(component.FindAll("input[aria-label='Streaming provider']"));
        Assert.Empty(component.FindAll("[data-testid='media-filter']"));
    }


    [Fact]
    public void CalendarPage_SourceDropdownFiltersVisibleCardsAfterSave()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='premiere-card']")));

        component.Find("button[title='Open filters']").Click();
        component.Find("input[aria-label='Toggle Series filters source HBO Max']").Change(true);
        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() =>
        {
            var card = Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Contains("North Star", card.TextContent);
        });
    }

    [Fact]
    public void CalendarPage_SourceDropdownIncludesLegacyNetworkMetadata()
    {
        var service = new FakePremiereService
        {
            Items =
            [
                new PremiereItem
                {
                    CanonicalId = "tv:10",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 10,
                    Title = "Legacy Network Show",
                    PremiereDate = new DateOnly(2026, 4, 27),
                    OriginalLanguage = "en",
                    OriginCountries = ["US"],
                    SourceNames = [],
                    NetworkName = "Legacy Network",
                    TmdbScore = 6.8,
                    TmdbVoteCount = 5
                }
            ]
        };
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/?week=2026-04-27");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='premiere-card']")));

        component.Find("button[title='Open filters']").Click();
        component.Find("input[aria-label='Toggle Series filters source Legacy Network']").Change(true);
        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() =>
        {
            var card = Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Contains("Legacy Network Show", card.TextContent);
        });
    }

    [Fact]
    public void CalendarPage_ClearFiltersResetsCurrentPageDraft()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/?week=2026-05-04&media=series&seriesQ=north");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var card = Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Contains("North Star", card.TextContent);
        });

        component.Find("button[title='Open filters']").Click();
        component.Find("button[title='Clear current page filters']").Click();
        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='premiere-card']")));
    }

    [Fact]
    public void CalendarPage_ClearMediaFilterSetOnlyClearsThatMediaGroup()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/?week=2026-05-04&media=all&seriesQ=south");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var card = Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Contains("South Point", card.TextContent);
        });

        component.Find("button[title='Open filters']").Click();
        component.Find("button[title='Clear Series filters']").Click();
        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='premiere-card']")));
    }

    [Fact]
    public void CalendarPage_LanguageDropdownAllowsMultipleSelections()
    {
        var service = new FakePremiereService
        {
            Items =
            [
                new PremiereItem
                {
                    CanonicalId = "tv:en",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 101,
                    Title = "English Launch",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    OriginalLanguage = "en"
                },
                new PremiereItem
                {
                    CanonicalId = "tv:nl",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 102,
                    Title = "Dutch Launch",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    OriginalLanguage = "nl"
                },
                new PremiereItem
                {
                    CanonicalId = "tv:fr",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 103,
                    Title = "French Launch",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    OriginalLanguage = "fr"
                }
            ]
        };
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Equal(3, component.FindAll("[data-testid='premiere-card']").Count));

        component.Find("button[title='Open filters']").Click();
        component.Find("input[aria-label='Toggle Series filters language English (EN)']").Change(true);
        component.Find("input[aria-label='Toggle Series filters language Dutch (NL)']").Change(true);
        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() =>
        {
            var text = component.Markup;
            Assert.Contains("English Launch", text);
            Assert.Contains("Dutch Launch", text);
            Assert.DoesNotContain("French Launch", text);
        });

        var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        Assert.Equal("en,nl", query["seriesLang"].ToString());
    }

    [Fact]
    public void CalendarPage_SeriesDateModeRadioUsesBrowserChangeValue()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='premiere-card']")));

        component.Find("button[title='Open filters']").Click();
        component.Find("input[aria-label='Use New series only']").Change("on");

        component.WaitForAssertion(() =>
        {
            var panel = component.Find("[data-testid='series-date-mode-filter']");
            Assert.Equal("New series only", panel.QuerySelector("summary strong")?.TextContent.Trim());
        });

        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() =>
        {
            var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
            Assert.Equal("new", query["seriesScope"].ToString());
        });
    }

    [Fact]
    public void CalendarPage_LanguageDropdownAllowsDeselectingOneSelectedLanguage()
    {
        var service = new FakePremiereService
        {
            Items =
            [
                new PremiereItem
                {
                    CanonicalId = "tv:en",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 101,
                    Title = "English Launch",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    OriginalLanguage = "en"
                },
                new PremiereItem
                {
                    CanonicalId = "tv:nl",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 102,
                    Title = "Dutch Launch",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    OriginalLanguage = "nl"
                },
                new PremiereItem
                {
                    CanonicalId = "tv:fr",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 103,
                    Title = "French Launch",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    OriginalLanguage = "fr"
                }
            ]
        };
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04&seriesLang=en,nl");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var text = component.Markup;
            Assert.Contains("English Launch", text);
            Assert.Contains("Dutch Launch", text);
            Assert.DoesNotContain("French Launch", text);
        });

        component.Find("button[title='Open filters']").Click();
        component.Find("input[aria-label='Toggle Series filters language English (EN)']").Change(false);
        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() =>
        {
            var text = component.Markup;
            Assert.DoesNotContain("English Launch", text);
            Assert.Contains("Dutch Launch", text);
            Assert.DoesNotContain("French Launch", text);
        });

        var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        Assert.Equal("nl", query["seriesLang"].ToString());
    }

    [Fact]
    public void CalendarPage_AllViewMirrorsSingleMediaLanguageQueryToBothVisibleMediaTypes()
    {
        var service = new FakePremiereService
        {
            Items =
            [
                new PremiereItem
                {
                    CanonicalId = "tv:en",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 101,
                    Title = "English Series",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    OriginalLanguage = "en"
                },
                new PremiereItem
                {
                    CanonicalId = "movie:nl",
                    Type = PremiereItemType.MovieFirstRelease,
                    MediaType = PremiereMediaType.Movie,
                    TmdbId = 201,
                    Title = "Dutch Movie",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    OriginalLanguage = "nl"
                },
                new PremiereItem
                {
                    CanonicalId = "movie:de",
                    Type = PremiereItemType.MovieFirstRelease,
                    MediaType = PremiereMediaType.Movie,
                    TmdbId = 202,
                    Title = "German Movie",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    OriginalLanguage = "de"
                },
                new PremiereItem
                {
                    CanonicalId = "tv:fr",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 102,
                    Title = "French Series",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    OriginalLanguage = "fr"
                }
            ]
        };
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/?week=2026-05-04&seriesLang=en,nl&seriesScope=new");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var text = component.Markup;
            Assert.Contains("English Series", text);
            Assert.Contains("Dutch Movie", text);
            Assert.DoesNotContain("German Movie", text);
            Assert.DoesNotContain("French Series", text);
        });
    }

    [Fact]
    public void CalendarPage_OriginCountryDropdownWritesQueryParameter()
    {
        var service = new FakePremiereService
        {
            Items =
            [
                new PremiereItem
                {
                    CanonicalId = "tv:be",
                    Type = PremiereItemType.SeriesEpisode,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 201,
                    Title = "Belgian Episode",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    OriginalLanguage = "nl",
                    OriginCountries = ["BE"]
                },
                new PremiereItem
                {
                    CanonicalId = "tv:us",
                    Type = PremiereItemType.SeriesEpisode,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 202,
                    Title = "American Episode",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    OriginalLanguage = "en",
                    OriginCountries = ["US"]
                }
            ]
        };
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Equal(2, component.FindAll("[data-testid='premiere-card']").Count));

        component.Find("button[title='Open filters']").Click();
        component.Find("input[aria-label='Toggle Series filters origin country Belgium (BE)']").Change(true);
        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() =>
        {
            var text = component.Markup;
            Assert.Contains("Belgian Episode", text);
            Assert.DoesNotContain("American Episode", text);
        });

        var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        Assert.Equal("BE", query["seriesOrigins"].ToString());
    }

    [Fact]
    public void CalendarPage_RoundTripsVisibleFiltersThroughQueryParameters()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var query = QueryHelpers.AddQueryString(
            "/",
            new Dictionary<string, string?>
            {
                ["week"] = "2026-05-04",
                ["media"] = "all",
                ["sort"] = "score",
                ["dir"] = "desc",
                ["min"] = "4.2",
                ["max"] = "9.1",
                ["minVotes"] = "25",
                ["seriesScope"] = "new",
                ["seriesLang"] = "en,nl",
                ["seriesOrigins"] = "BE,US",
                ["seriesGenres"] = "18",
                ["seriesSources"] = "HBO Max|VTM",
                ["seriesWatchRegion"] = "BE",
                ["seriesSourceText"] = "CBS",
                ["seriesAvailabilities"] = "flatrate,free",
                ["seriesStatuses"] = "Returning Series|Pilot",
                ["seriesTypes"] = "Scripted|Reality",
                ["seriesRuntimeMin"] = "20",
                ["seriesRuntimeMax"] = "90",
                ["seriesKeywords"] = "crime",
                ["seriesQ"] = "north",
                ["movieLang"] = "fr,nl",
                ["movieOrigins"] = "AU",
                ["movieGenres"] = "28",
                ["movieSources"] = "Apple TV",
                ["movieWatchRegion"] = "AU",
                ["movieSourceText"] = "Netflix",
                ["movieAvailabilities"] = "buy,rent",
                ["movieReleaseTypes"] = "3,4",
                ["movieCertifications"] = "US:PG-13",
                ["movieCertificationCountry"] = "US",
                ["movieRuntimeMin"] = "80",
                ["movieRuntimeMax"] = "180",
                ["movieKeywords"] = "south",
                ["movieQ"] = "south"
            });
        navigation.NavigateTo(query);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        component.Find("button[title='Open filters']").Click();
        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() => Assert.True(service.Calls.Count >= 2));

        var roundTrip = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        Assert.Equal("2026-05-04", roundTrip["week"].ToString());
        Assert.False(roundTrip.ContainsKey("media"));
        Assert.Equal("score", roundTrip["sort"].ToString());
        Assert.Equal("desc", roundTrip["dir"].ToString());
        Assert.Equal("4.2", roundTrip["min"].ToString());
        Assert.Equal("9.1", roundTrip["max"].ToString());
        Assert.Equal("25", roundTrip["minVotes"].ToString());
        Assert.Equal("new", roundTrip["seriesScope"].ToString());
        Assert.Equal("en,nl", roundTrip["seriesLang"].ToString());
        Assert.Equal("BE,US", roundTrip["seriesOrigins"].ToString());
        Assert.Equal("18", roundTrip["seriesGenres"].ToString());
        AssertDelimitedValues(roundTrip["seriesSources"].ToString(), '|', "HBO Max", "VTM");
        Assert.Equal("BE", roundTrip["seriesWatchRegion"].ToString());
        Assert.Equal("CBS", roundTrip["seriesSourceText"].ToString());
        AssertDelimitedValues(roundTrip["seriesAvailabilities"].ToString(), ',', "flatrate", "free");
        AssertDelimitedValues(roundTrip["seriesStatuses"].ToString(), '|', "Pilot", "Returning Series");
        AssertDelimitedValues(roundTrip["seriesTypes"].ToString(), '|', "Reality", "Scripted");
        Assert.Equal("20", roundTrip["seriesRuntimeMin"].ToString());
        Assert.Equal("90", roundTrip["seriesRuntimeMax"].ToString());
        Assert.Equal("crime", roundTrip["seriesKeywords"].ToString());
        Assert.Equal("north", roundTrip["seriesQ"].ToString());
        Assert.Equal("fr,nl", roundTrip["movieLang"].ToString());
        Assert.Equal("AU", roundTrip["movieOrigins"].ToString());
        Assert.Equal("28", roundTrip["movieGenres"].ToString());
        Assert.Equal("Apple TV", roundTrip["movieSources"].ToString());
        Assert.Equal("AU", roundTrip["movieWatchRegion"].ToString());
        Assert.Equal("Netflix", roundTrip["movieSourceText"].ToString());
        AssertDelimitedValues(roundTrip["movieAvailabilities"].ToString(), ',', "buy", "rent");
        Assert.Equal("3,4", roundTrip["movieReleaseTypes"].ToString());
        Assert.Equal("US:PG-13", roundTrip["movieCertifications"].ToString());
        Assert.Equal("US", roundTrip["movieCertificationCountry"].ToString());
        Assert.Equal("80", roundTrip["movieRuntimeMin"].ToString());
        Assert.Equal("180", roundTrip["movieRuntimeMax"].ToString());
        Assert.Equal("south", roundTrip["movieKeywords"].ToString());
        Assert.Equal("south", roundTrip["movieQ"].ToString());
    }

    [Fact]
    public void CalendarPage_SaveCanonicalizesUrlByOmittingDefaultFilterParameters()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(
            "/series?week=2026-04-20&sort=date&dir=asc&min=0.0&max=10.0&minVotes=0&lang=both&origin=all&runtimeMin=0&runtimeMax=360");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        component.Find("button[title='Open filters']").Click();
        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() => Assert.True(service.Calls.Count >= 2));

        var uri = new Uri(navigation.Uri);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal("/series", uri.AbsolutePath);
        Assert.Equal("2026-04-20", query["week"].ToString());
        Assert.DoesNotContain("sort=", navigation.Uri);
        Assert.DoesNotContain("dir=", navigation.Uri);
        Assert.DoesNotContain("min=", navigation.Uri);
        Assert.DoesNotContain("max=", navigation.Uri);
        Assert.DoesNotContain("minVotes=", navigation.Uri);
        Assert.DoesNotContain("lang=", navigation.Uri);
        Assert.DoesNotContain("origin=", navigation.Uri);
        Assert.DoesNotContain("runtimeMin=", navigation.Uri);
        Assert.DoesNotContain("runtimeMax=", navigation.Uri);
    }

    [Fact]
    public void CalendarPage_ClearFiltersRemovesSeriesScopeFromUrl()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(
            "/series?week=2026-04-20&sort=date&dir=asc&min=0.0&max=10.0&minVotes=0&lang=both&origin=all&runtimeMin=0&runtimeMax=360&seriesScope=new");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        component.Find("button[title='Open filters']").Click();
        component.Find("button[title='Clear current page filters']").Click();
        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() => Assert.True(service.Calls.Count >= 2));

        var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        Assert.Equal("2026-04-20", query["week"].ToString());
        Assert.DoesNotContain("seriesScope=", navigation.Uri);
        Assert.DoesNotContain("sort=", navigation.Uri);
        Assert.DoesNotContain("dir=", navigation.Uri);
        Assert.DoesNotContain("lang=", navigation.Uri);
    }

    [Fact]
    public void CalendarPage_ShowsIncrementalQueryResults()
    {
        var service = new FakePremiereService
        {
            ReportPartialProgress = true
        };
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var progress = component.Find("[data-testid='query-progress']");
            Assert.Contains("Fake source one", progress.TextContent);
            Assert.Contains("Fake source two", progress.TextContent);
            Assert.Contains("Complete", progress.TextContent);
            Assert.Single(component.FindAll("[data-testid='premiere-card']"));
        });
    }

    [Fact]
    public void CalendarPage_RendersUnverifiedCardsBelowVerifiedCards()
    {
        var service = new FakePremiereService
        {
            Items =
            [
                new PremiereItem
                {
                    CanonicalId = "unverified:movie:watchmode-1",
                    Type = PremiereItemType.MovieFirstRelease,
                    MediaType = PremiereMediaType.Movie,
                    TmdbId = 0,
                    Title = "External Hint",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    VerificationState = PremiereVerificationState.Unverified,
                    VerificationNote = "Could not match to TMDb yet",
                    SourceNames = ["Watchmode"]
                },
                new PremiereItem
                {
                    CanonicalId = "movie:20",
                    Type = PremiereItemType.MovieFirstRelease,
                    MediaType = PremiereMediaType.Movie,
                    TmdbId = 20,
                    Title = "Verified Movie",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    OriginalLanguage = "en"
                }
            ]
        };
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/movies?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var cards = component.FindAll("[data-testid='premiere-card']");
            Assert.Equal(2, cards.Count);
            Assert.Contains("Verified Movie", cards[0].TextContent);
            Assert.Contains("External Hint", cards[1].TextContent);
            Assert.Contains("Unverified from external sources", component.Markup);
        });
    }

    [Fact]
    public void PremiereCard_UnverifiedCardShowsProviderDateAndHidesArrButton()
    {
        var item = new PremiereItem
        {
            CanonicalId = "unverified:movie:watchmode-1",
            Type = PremiereItemType.MovieFirstRelease,
            MediaType = PremiereMediaType.Movie,
            TmdbId = 0,
            Title = "External Hint",
            PremiereDate = new DateOnly(2026, 5, 4),
            VerificationState = PremiereVerificationState.Unverified,
            VerificationNote = "Could not match to TMDb yet",
            SourceNames = ["Watchmode"],
            ExternalUrl = "https://example.test/title"
        };
        var settings = new IntegrationSettings
        {
            Radarr = new RadarrIntegrationSettings { Enabled = true }
        };

        var component = Render<PremiereCalendar.Components.Shared.PremiereCard>(parameters => parameters
            .Add(card => card.Item, item)
            .Add(card => card.IntegrationSettings, settings)
            .Add(card => card.OnAddToArr, EventCallback.Factory.Create<PremiereItem>(this, _ => { })));

        Assert.Contains("Unverified", component.Markup);
        Assert.Contains("Provider date", component.Markup);
        Assert.Contains("Could not match to TMDb yet", component.Markup);
        Assert.Contains("External source", component.Markup);
        Assert.Empty(component.FindAll(".arr-add-button"));
    }

    [Fact]
    public void CalendarPage_QueryProgressShowsUnverifiedDiagnosticCounts()
    {
        var service = new FakePremiereService
        {
            ReportPartialProgress = true,
            ReportProgressDetails = true,
            ReportUnverifiedProgress = true,
            SuppressFinalProgress = true
        };
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var progress = component.Find("[data-testid='query-progress']");
            Assert.Contains("1 unverified", progress.TextContent);
        });
    }

    [Fact]
    public void CalendarPage_ShowsPerSourceProgressBarsAndDetails()
    {
        var service = new FakePremiereService
        {
            ReportPartialProgress = true,
            ReportProgressDetails = true,
            SuppressFinalProgress = true
        };
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var progress = component.Find("[data-testid='query-progress']");
            Assert.Contains("pages 1-2 of 4", progress.TextContent);
            Assert.Contains("resolved 1 of 2 candidates", progress.TextContent);
            var bars = component.FindAll("[data-testid='query-progress-bar']");
            Assert.True(bars.Count >= 2);
            Assert.Contains(bars, bar => bar.GetAttribute("aria-valuenow") == "50");
        });
    }

    [Fact]
    public void CalendarPage_FinalProgressDoesNotLeaveSourceCardsPartiallyLoaded()
    {
        var service = new FakePremiereService
        {
            ReportPartialProgress = true,
            ReportProgressDetails = true
        };
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var progress = component.Find("[data-testid='query-progress']");
            Assert.Contains("Complete", progress.TextContent);
            Assert.Contains("Done", progress.TextContent);
            Assert.DoesNotContain("pages 1-2 of 4", progress.TextContent);
            Assert.DoesNotContain("resolved 1 of 2 candidates", progress.TextContent);
            Assert.All(
                component.FindAll("[data-testid='query-progress-bar']"),
                bar => Assert.Equal("100", bar.GetAttribute("aria-valuenow")));
        });
    }

    [Fact]
    public void CalendarPage_ForegroundBudgetStopsStuckSourceLoad()
    {
        Services.Configure<CalendarLoadOptions>(options => options.ForegroundLoadBudgetSeconds = 1);
        var service = new FakePremiereService
        {
            ReportPartialProgress = true,
            DelayAfterPartialProgress = TimeSpan.FromSeconds(5),
            SuppressFinalProgress = true
        };
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var progress = component.Find("[data-testid='query-progress']");
            Assert.Contains("Load budget", progress.TextContent);
            Assert.Contains("Stopped after 1 s foreground budget", progress.TextContent);
            Assert.DoesNotContain("Loading sources", progress.TextContent);
            Assert.DoesNotContain("Updating results", component.Markup);
            Assert.Single(component.FindAll("[data-testid='premiere-card']"));
        }, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void CalendarPage_GroupsDaySourceProgressByProvider()
    {
        var service = new FakePremiereService
        {
            ReportDaySourceProgress = true
        };
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var progress = component.Find("[data-testid='query-progress']");
            Assert.Contains("TMDb series", progress.TextContent);
            Assert.Contains("2 day batches", progress.TextContent);
            Assert.Single(progress.QuerySelectorAll("button"), button => button.TextContent.Contains("TMDb series"));
            Assert.DoesNotContain("TMDb series Mon 04 May", progress.TextContent);
            Assert.DoesNotContain("TMDb series Tue 05 May", progress.TextContent);
        });
    }

    [Fact]
    public void CalendarPage_DaySourceGroupDoesNotLookCompleteWhileWeekLoadContinues()
    {
        var service = new FakePremiereService
        {
            ReportCompletedDaySourceProgress = true,
            DelayBetweenDaySourceProgress = TimeSpan.FromSeconds(5),
            SuppressFinalProgress = true
        };
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var progress = component.Find("[data-testid='query-progress']");
            Assert.Contains("TMDb series", progress.TextContent);
            Assert.Contains("1 day batch", progress.TextContent);
            Assert.DoesNotContain("Done", progress.TextContent);

            var progressButton = Assert.Single(progress.QuerySelectorAll("button"), button => button.TextContent.Contains("TMDb series"));
            Assert.DoesNotContain("final", progressButton.ClassName, StringComparison.OrdinalIgnoreCase);
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void CalendarPage_RefreshPassesSelectedDayAsPriorityDate()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        component.Find("button[data-day-target='premiere-day-20260505']").Click();
        component.Find("button[title='Refresh premieres']").Click();

        component.WaitForAssertion(() => Assert.True(service.Calls.Count >= 2));
        Assert.Equal(new DateOnly(2026, 5, 5), service.Calls.Last().PriorityDate);
    }

    private static void AssertDelimitedValues(string actual, char separator, params string[] expected)
    {
        Assert.Equal(
            expected.Order(StringComparer.OrdinalIgnoreCase),
            actual.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Order(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class FakePremiereService : IPremiereService
    {
        public List<(DateOnly Start, DateOnly End, bool ForceRefresh, DateOnly? PriorityDate)> Calls { get; } = [];

        public IReadOnlyList<PremiereItem>? Items { get; init; }

        public bool ReportPartialProgress { get; init; }

        public bool ReportProgressDetails { get; init; }

        public bool ReportDaySourceProgress { get; init; }

        public bool ReportCompletedDaySourceProgress { get; init; }

        public bool ReportUnverifiedProgress { get; init; }

        public bool SuppressFinalProgress { get; init; }

        public TimeSpan DelayAfterPartialProgress { get; init; }

        public TimeSpan DelayBetweenDaySourceProgress { get; init; }

        public Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false,
            IProgress<PremiereLoadProgress>? progress = null,
            CalendarFilters? filters = null)
        {
            return GetPremieresCoreAsync(start, end, cancellationToken, forceRefresh, progress, filters);
        }

        public async IAsyncEnumerable<PremiereLoadProgress> StreamPremieresAsync(
            DateOnly start,
            DateOnly end,
            bool forceRefresh = false,
            CalendarFilters? filters = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add((start, end, forceRefresh, filters?.PriorityDate));
            var items = BuildItems(start);

            if (ReportPartialProgress)
            {
                var firstItems = items.Take(1).ToArray();
                yield return new PremiereLoadProgress(
                    "Fake source one",
                    firstItems.Length,
                    firstItems.Length,
                    firstItems,
                    CompletedWork: ReportProgressDetails ? 2 : null,
                    TotalWork: ReportProgressDetails ? 4 : null,
                    ProgressText: ReportProgressDetails ? "pages 1-2 of 4" : null);
                yield return new PremiereLoadProgress(
                    "Fake source two",
                    items.Count - firstItems.Length,
                    items.Count,
                    items,
                    CompletedWork: ReportProgressDetails ? 1 : null,
                    TotalWork: ReportProgressDetails ? 2 : null,
                    ProgressText: ReportProgressDetails ? "resolved 1 of 2 candidates" : null)
                {
                    UnmappedCount = ReportUnverifiedProgress ? 1 : null
                };

                if (DelayAfterPartialProgress > TimeSpan.Zero)
                {
                    await Task.Delay(DelayAfterPartialProgress, cancellationToken);
                }
            }

            if (ReportDaySourceProgress)
            {
                var mondayItems = items.Take(1).ToArray();
                var tuesdayItems = items.Skip(1).Take(1).ToArray();
                yield return new PremiereLoadProgress(
                    "TMDb series Mon 04 May",
                    mondayItems.Length,
                    mondayItems.Length,
                    mondayItems,
                    CompletedWork: 10,
                    TotalWork: 20,
                    ProgressText: "pages 1-1 of 1 · processed 10 of 20 rows",
                    ElapsedMilliseconds: 1200)
                {
                    SourceItems = mondayItems
                };
                yield return new PremiereLoadProgress(
                    "TMDb series Tue 05 May",
                    tuesdayItems.Length,
                    items.Count,
                    items,
                    CompletedWork: 10,
                    TotalWork: 20,
                    ProgressText: "pages 1-1 of 1 · processed 10 of 20 rows",
                    ElapsedMilliseconds: 1400)
                {
                    SourceItems = tuesdayItems
                };
            }

            if (ReportCompletedDaySourceProgress)
            {
                var mondayItems = items.Take(1).ToArray();
                var tuesdayItems = items.Skip(1).Take(1).ToArray();
                yield return new PremiereLoadProgress(
                    "TMDb series Mon 04 May",
                    mondayItems.Length,
                    mondayItems.Length,
                    mondayItems,
                    CompletedWork: 20,
                    TotalWork: 20,
                    ProgressText: "Done - pages 1-1 of 1 · processed 20 of 20 rows",
                    ElapsedMilliseconds: 1200)
                {
                    Phase = "complete",
                    SourceItems = mondayItems
                };

                if (DelayBetweenDaySourceProgress > TimeSpan.Zero)
                {
                    await Task.Delay(DelayBetweenDaySourceProgress, cancellationToken);
                }

                yield return new PremiereLoadProgress(
                    "TMDb series Tue 05 May",
                    tuesdayItems.Length,
                    items.Count,
                    items,
                    CompletedWork: 8,
                    TotalWork: 20,
                    ProgressText: "pages 1-1 of 1 · processed 8 of 20 rows",
                    ElapsedMilliseconds: 1400)
                {
                    SourceItems = tuesdayItems
                };
            }

            if (!SuppressFinalProgress)
            {
                yield return new PremiereLoadProgress("Complete", 0, items.Count, items, IsFinal: true);
            }

            await Task.CompletedTask;
        }

        private async Task<IReadOnlyList<PremiereItem>> GetPremieresCoreAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh,
            IProgress<PremiereLoadProgress>? progress,
            CalendarFilters? filters)
        {
            IReadOnlyList<PremiereItem> items = [];
            await foreach (var update in StreamPremieresAsync(start, end, forceRefresh, filters, cancellationToken))
            {
                progress?.Report(update);
                items = update.Items;
            }

            return items;
        }

        private IReadOnlyList<PremiereItem> BuildItems(DateOnly start)
        {
            return Items ??
            [
                new PremiereItem
                {
                    CanonicalId = "tv:1",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 1,
                    Title = "North Star",
                    PremiereDate = start,
                    OriginalLanguage = "en",
                    OriginCountries = ["US"],
                    SourceNames = ["HBO Max"],
                    Genres = ["Drama"],
                    GenreIds = [18],
                    NetworkName = "HBO Max",
                    RuntimeMinutes = 55,
                    TmdbScore = 7.5,
                    TmdbVoteCount = 12,
                    Keywords = ["north"],
                    PosterUrl = "https://image.tmdb.org/t/p/w185/north.jpg"
                },
                new PremiereItem
                {
                    CanonicalId = "movie:2",
                    Type = PremiereItemType.MovieFirstRelease,
                    MediaType = PremiereMediaType.Movie,
                    TmdbId = 2,
                    Title = "South Point",
                    PremiereDate = start.AddDays(1),
                    OriginalLanguage = "en",
                    OriginCountries = ["AU"],
                    SourceNames = ["Apple TV"],
                    Genres = ["Action"],
                    GenreIds = [28],
                    RuntimeMinutes = 102,
                    TmdbScore = 8.1,
                    TmdbVoteCount = 44,
                    Keywords = ["south"],
                    PosterUrl = "https://image.tmdb.org/t/p/w185/south.jpg"
                }
            ];
        }
    }

    private sealed class FakeAdjacentWeekPrefetcher : IAdjacentWeekPrefetcher
    {
        public List<DateOnly> WeekStarts { get; } = [];

        public void PrefetchAdjacentWeeks(DateOnly weekStart, CalendarFilters? filters = null)
        {
            WeekStarts.Add(weekStart);
        }
    }

    private sealed class ForegroundAwareAdjacentWeekPrefetcher(CalendarLoadCoordinator coordinator) : IAdjacentWeekPrefetcher
    {
        public List<PrefetchCall> Calls { get; } = [];

        public void PrefetchAdjacentWeeks(DateOnly weekStart, CalendarFilters? filters = null)
        {
            Calls.Add(new PrefetchCall(
                weekStart,
                filters is null ? null : CalendarFilterState.Clone(filters),
                coordinator.HasActiveForegroundLoad));
        }
    }

    private sealed record PrefetchCall(
        DateOnly WeekStart,
        CalendarFilters? Filters,
        bool WasForegroundActive);

    private sealed class FakeFilterCatalogService : IFilterCatalogService
    {
        public Task<FilterCatalog> GetCatalogAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult(new FilterCatalog
            {
                SeriesGenres = [new("18", "Drama"), new("9648", "Mystery")],
                MovieGenres = [new("28", "Action"), new("35", "Comedy")],
                SeriesProviders = [new("HBO Max", "HBO Max")],
                MovieProviders = [new("Apple TV", "Apple TV")],
                Languages = [new("en", "English (EN)"), new("nl", "Dutch (NL)"), new("fr", "French (FR)")],
                Countries = [new("BE", "Belgium (BE)"), new("US", "United States of America (US)"), new("AU", "Australia (AU)")],
                MovieCertifications = [new("US:PG-13", "PG-13")]
            });
        }
    }

    private sealed class FakeCalendarFilterUsageStore : ICalendarFilterUsageStore
    {
        public Task RecordUseAsync(
            CalendarPageMode pageMode,
            CalendarFilters filters,
            int itemCount,
            DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<CalendarFilterUsageProfile?> GetProfileAsync(string profileKey, CancellationToken cancellationToken)
        {
            return Task.FromResult<CalendarFilterUsageProfile?>(null);
        }

        public Task<IReadOnlyList<CalendarFilterUsageProfile>> GetTopProfilesAsync(
            int count,
            DateTimeOffset nowUtc,
            TimeSpan retention,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<CalendarFilterUsageProfile>>([]);
        }

        public Task MarkWarmedAsync(
            string profileKey,
            CalendarPageMode pageMode,
            CalendarFilters filters,
            bool isDefault,
            int itemCount,
            DateTimeOffset warmedAtUtc,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task MarkWarmFailedAsync(
            string profileKey,
            CalendarPageMode pageMode,
            CalendarFilters filters,
            bool isDefault,
            string failure,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<int> CleanupAsync(
            DateTimeOffset cutoffUtc,
            IReadOnlySet<string> retainedProfileKeys,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }
    }

    private sealed class ThrowingFilterCatalogService : IFilterCatalogService
    {
        public Task<FilterCatalog> GetCatalogAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            throw new TimeoutException("Catalog timeout.");
        }
    }

    private sealed class FakeIntegrationSettingsStore : IIntegrationSettingsStore
    {
        public IntegrationSettings Settings { get; private set; } = new();

        public Task<IntegrationSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Settings);
        }

        public Task SaveAsync(IntegrationSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeArrIntegrationService : IArrIntegrationService
    {
        public Task<ArrAddResult> AddAsync(PremiereItem item, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ArrAddResult(true, false, ArrIntegrationTarget.Radarr, item.Title, "Added."));
        }

        public Task<ArrConnectionOptions> GetSonarrOptionsAsync(
            SonarrIntegrationSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ArrConnectionOptions([], []));
        }

        public Task<ArrConnectionOptions> GetRadarrOptionsAsync(
            RadarrIntegrationSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ArrConnectionOptions([], []));
        }
    }
}
