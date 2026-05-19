using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PremiereCalendar.Components.Shared;
using PremiereCalendar.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.ComponentTests;

public sealed class CalendarPageTests : BunitContext
{
    private readonly FakeAdjacentWeekPrefetcher _prefetcher = new();
    private readonly FakeViewSyncService _viewSyncService = new();
    private readonly FakeCalendarCacheMaintenance _cacheMaintenance = new();
    private readonly InMemoryAppStateStore _appStateStore = new();

    public CalendarPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string>("premiereViewSync.getOrCreateDeviceId").SetResult("device-a");
        Services.AddLogging();
        Services.AddSingleton<IAdjacentWeekPrefetcher>(_prefetcher);
        Services.AddSingleton<CalendarLoadCoordinator>();
        Services.AddSingleton<ICalendarFilterUsageStore, FakeCalendarFilterUsageStore>();
        Services.AddSingleton<IFilterCatalogService, FakeFilterCatalogService>();
        Services.AddSingleton<IIntegrationSettingsStore>(new FakeIntegrationSettingsStore
        {
            Settings = new IntegrationSettings
            {
                Sources = new SourceIntegrationSettings
                {
                    Tmdb = new TmdbSourceSettings { BearerToken = "tmdb-token" }
                }
            }
        });
        Services.AddSingleton<IArrIntegrationService, FakeArrIntegrationService>();
        Services.AddSingleton<IViewSyncService>(_viewSyncService);
        Services.AddSingleton<ICalendarCacheMaintenance>(_cacheMaintenance);
        Services.AddSingleton<IAppStateStore>(_appStateStore);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<CalendarPresetService>();
        Services.AddSingleton<CalendarVisitChangeService>();
        Services.Configure<CalendarLoadOptions>(_ => { });
    }

    private static void ExpandQueryProgress(IRenderedComponent<PremiereCalendar.Components.Pages.Calendar> component)
    {
        component.WaitForAssertion(() =>
            Assert.Single(component.FindAll("[data-testid='query-progress-toggle']")));

        var toggle = component.Find("[data-testid='query-progress-toggle']");
        if (toggle.GetAttribute("aria-expanded") != "true")
        {
            toggle.Click();
        }
    }

    private static int DirectChildIndex(IElement parent, Func<IElement, bool> predicate)
    {
        for (var index = 0; index < parent.Children.Length; index++)
        {
            if (predicate(parent.Children[index]))
            {
                return index;
            }
        }

        return -1;
    }

    [Fact]
    public void CalendarPage_UsesCompactCommandBarBeforeCalendarBoard()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-11&seriesScope=new");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
            Assert.NotEmpty(component.FindAll("[data-testid='premiere-card']")));

        var commandBar = component.Find("[data-testid='calendar-command-bar']");
        Assert.Contains("Series", commandBar.TextContent);
        Assert.Contains("11 May", commandBar.TextContent);
        Assert.Contains("17 May", commandBar.TextContent);
        Assert.Contains("New only", commandBar.TextContent);
        Assert.Empty(component.FindAll(".calendar-header"));
        Assert.Empty(component.FindAll(".toolbar"));

        var shell = component.Find(".calendar-shell");
        var commandIndex = DirectChildIndex(shell, element =>
            element.GetAttribute("data-testid") == "calendar-command-bar");
        var boardIndex = DirectChildIndex(shell, element =>
            element.ClassList.Contains("calendar-week-board"));

        Assert.True(commandIndex >= 0);
        Assert.True(boardIndex >= 0);
        Assert.True(commandIndex < boardIndex);
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
    public void CalendarPage_HidesNoOpSourceProgressAfterSimpleLoad()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Empty(component.FindAll("[data-testid='query-progress']"));
        });
    }

    [Fact]
    public void CalendarPage_HidesZeroResultSourceProgressWhenCollapsed()
    {
        var service = new FakePremiereService
        {
            Items = [],
            ReportPartialProgress = true
        };
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Empty(component.FindAll("[data-testid='loading']"));
            Assert.Empty(component.FindAll("[data-testid='premiere-card']"));
            Assert.Empty(component.FindAll("[data-testid='query-progress']"));
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

        component.Find("button[title='Refresh sources from providers']").Click();

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
            var refresh = component.Find("button[title='Refresh sources from providers']");
            Assert.Null(refresh.GetAttribute("disabled"));
            Assert.Contains("Updating", component.Find("[data-testid='refreshing']").TextContent);
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
    public void CalendarPage_FilterCatalogFailureLogsWarning()
    {
        var loggerProvider = new CollectingLoggerProvider();
        var service = new FakePremiereService();
        Services.AddLogging(builder => builder.AddProvider(loggerProvider));
        Services.AddSingleton<IPremiereService>(service);
        Services.AddSingleton<IFilterCatalogService>(new ThrowingFilterCatalogService());

        Render<PremiereCalendar.Components.Pages.Calendar>();

        Assert.Contains(loggerProvider.Entries, entry =>
            string.Equals(entry.Category, "PremiereCalendar.Components.Pages.Calendar", StringComparison.Ordinal)
            && entry.Level == LogLevel.Warning
            && entry.Message.Contains("Filter catalog load failed", StringComparison.Ordinal));
    }

    [Fact]
    public void CalendarPage_MissingTmdbSettingsNavigatesToSettingsBeforeLoading()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.AddSingleton<IIntegrationSettingsStore>(new FakeIntegrationSettingsStore
        {
            Settings = new IntegrationSettings()
        });
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var redirected = new Uri(navigation.Uri);
            var query = QueryHelpers.ParseQuery(redirected.Query);
            Assert.Equal("/settings", redirected.AbsolutePath);
            Assert.Equal("tmdb", query["reason"].ToString());
            Assert.Equal("/series?week=2026-05-04", query["returnUrl"].ToString());
            Assert.Empty(service.Calls);
        });
    }

    [Fact]
    public void CalendarPage_SettingsLoadFailureLogsErrorWithoutRedirectingToSetup()
    {
        var loggerProvider = new CollectingLoggerProvider();
        var service = new FakePremiereService();
        Services.AddLogging(builder => builder.AddProvider(loggerProvider));
        Services.AddSingleton<IPremiereService>(service);
        Services.AddSingleton<IIntegrationSettingsStore>(new ThrowingIntegrationSettingsStore());
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.EndsWith("/series?week=2026-05-04", new Uri(navigation.Uri).PathAndQuery);
            Assert.Empty(service.Calls);
            Assert.Contains("Settings could not be loaded", component.Markup);
            Assert.Contains(loggerProvider.Entries, entry =>
                string.Equals(entry.Category, "PremiereCalendar.Components.Pages.Calendar", StringComparison.Ordinal)
                && entry.Level == LogLevel.Error
                && entry.Message.Contains("Integration settings load failed", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void CalendarPage_SettingsLoadFailureCanRetryWithoutRedirectingToSetup()
    {
        var service = new FakePremiereService();
        var store = new TransientFailingIntegrationSettingsStore
        {
            Settings = new IntegrationSettings
            {
                Sources = new SourceIntegrationSettings
                {
                    Tmdb = new TmdbSourceSettings { BearerToken = "tmdb-token" }
                }
            }
        };
        Services.AddSingleton<IPremiereService>(service);
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("Settings could not be loaded", component.Markup);
            Assert.Empty(service.Calls);
        });

        component.Find("button[title='Refresh sources from providers']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.EndsWith("/series?week=2026-05-04", new Uri(navigation.Uri).PathAndQuery);
            Assert.Single(service.Calls);
            Assert.DoesNotContain("/settings?reason=tmdb", navigation.Uri);
            Assert.DoesNotContain("Settings could not be loaded", component.Markup);
        });
        Assert.Equal(2, store.GetCallCount);
    }

    [Fact]
    public void CalendarPage_SameUrlViewSyncStateStillLoadsInitialCalendar()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        _viewSyncService.SetGroupState(new ViewSyncGroupState(
            "group-a",
            "series",
            "/series",
            4,
            DateTimeOffset.Parse("2026-05-10T10:00:00Z"),
            "device-b",
            "Kitchen tablet"));
        navigation.NavigateTo("/series");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Single(service.Calls);
            Assert.Single(component.FindAll("[data-testid='calendar-week']"));
        });
    }

    [Fact]
    public void CalendarPage_DefaultOnlyStoredFiltersDoNotBlockInitialLoad()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        JSInterop.Setup<string?>(
                "premiereFilterStorage.get",
                "premiere-calendar:filters:v2:series")
            .SetResult("sort=date&dir=asc&score=tmdb&min=0.0&max=10.0&minVotes=0&lang=both&origin=all&runtimeMin=0&runtimeMax=360&seriesScope=episodes");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Single(service.Calls);
            Assert.DoesNotContain("sort=date", navigation.Uri);
            Assert.Single(component.FindAll("[data-testid='calendar-week']"));
        });
    }

    [Fact]
    public void CalendarPage_ViewSyncOverviewFailureDoesNotBreakCalendarLoad()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        _viewSyncService.GetOverviewException = new ArgumentOutOfRangeException("value", "View-sync schema initialization failed.");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Single(service.Calls);
            Assert.Single(component.FindAll("[data-testid='calendar-week']"));
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
    public void CalendarPage_DayTabsOnlyReferenceMountedPanel()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var tabs = component.FindAll("button[role='tab']");
            Assert.Equal(7, tabs.Count);

            var selected = Assert.Single(tabs, tab => tab.GetAttribute("aria-selected") == "true");
            var controlledPanel = selected.GetAttribute("aria-controls");
            Assert.False(string.IsNullOrWhiteSpace(controlledPanel));
            Assert.Single(component.FindAll($"#{controlledPanel}"));

            Assert.All(
                tabs.Where(tab => tab.GetAttribute("aria-selected") != "true"),
                tab => Assert.Null(tab.GetAttribute("aria-controls")));
        });
    }

    [Fact]
    public void CalendarPage_SortOnlyRouteChangeReordersVisibleCardsWithoutReloading()
    {
        var weekStart = new DateOnly(2026, 5, 4);
        var service = new FakePremiereService
        {
            Items =
            [
                new PremiereItem
                {
                    CanonicalId = "series:zulu",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 10,
                    Title = "Zulu",
                    PremiereDate = weekStart,
                    OriginalLanguage = "en",
                    TmdbScore = 9.2,
                    TmdbVoteCount = 300
                },
                new PremiereItem
                {
                    CanonicalId = "series:alpha",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 11,
                    Title = "Alpha",
                    PremiereDate = weekStart,
                    OriginalLanguage = "en",
                    TmdbScore = 5.1,
                    TmdbVoteCount = 30
                }
            ]
        };
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04&sort=title");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var cards = component.FindAll("[data-testid='premiere-card']");
            Assert.Equal(2, cards.Count);
            Assert.Contains("Alpha", cards[0].TextContent);
            Assert.Contains("Zulu", cards[1].TextContent);
            Assert.Single(service.Calls);
        });

        navigation.NavigateTo("/series?week=2026-05-04&sort=score&dir=desc");

        component.WaitForAssertion(() =>
        {
            var cards = component.FindAll("[data-testid='premiere-card']");
            Assert.Equal(2, cards.Count);
            Assert.Contains("Zulu", cards[0].TextContent);
            Assert.Contains("Alpha", cards[1].TextContent);
            Assert.Single(service.Calls);
        });
    }

    [Fact]
    public async Task CalendarPage_SaveSortOnlyFilterChangeReordersExistingResultsWithoutReloading()
    {
        var weekStart = new DateOnly(2026, 5, 4);
        var service = new FakePremiereService
        {
            Items =
            [
                new PremiereItem
                {
                    CanonicalId = "series:zulu",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 10,
                    Title = "Zulu",
                    PremiereDate = weekStart,
                    OriginalLanguage = "en",
                    TmdbScore = 9.2,
                    TmdbVoteCount = 300
                },
                new PremiereItem
                {
                    CanonicalId = "series:alpha",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 11,
                    Title = "Alpha",
                    PremiereDate = weekStart,
                    OriginalLanguage = "en",
                    TmdbScore = 5.1,
                    TmdbVoteCount = 30
                }
            ]
        };
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        component.Find("button[title='Open filters']").Click();
        component.Find("select[aria-label='Sort results by']").Change("Title");
        component.Find("button[title='Save filters']").Click();

        component.WaitForAssertion(() =>
        {
            var cards = component.FindAll("[data-testid='premiere-card']");
            Assert.Equal(2, cards.Count);
            Assert.Contains("Alpha", cards[0].TextContent);
            Assert.Contains("Zulu", cards[1].TextContent);
            Assert.Contains("sort=title", navigation.Uri);
        });
        await Task.Delay(200);
        Assert.Single(service.Calls);
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
    public async Task CalendarPage_RoundTripsVisibleFiltersThroughQueryParameters()
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
        await Task.Delay(200);
        Assert.Single(service.Calls);
    }

    [Fact]
    public void CalendarPage_InvalidMovieReleaseTypeQueryDoesNotBreakFilterPane()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/movies?week=2026-05-04&movieReleaseTypes=99");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        component.Find("button[title='Open filters']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Single(component.FindAll("[data-testid='filter-pane']"));
            Assert.Contains("All releases", component.Markup);
        });
    }

    [Fact]
    public void CalendarPage_FilterButtonShowsActiveFilterCountWithoutHeaderCriteria()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/?week=2026-05-04&runtimeMin=80&runtimeMax=150&seriesWatchRegion=BE&movieCertifications=US%3APG-13&movieCertificationCountry=US");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Empty(component.FindAll("[data-testid='active-filter-strip']"));
            Assert.Empty(component.FindAll("button[aria-label='Clear active filters']"));
            var filterButton = component.Find("button[title='Open filters']");
            Assert.Equal("3", component.Find("[data-testid='active-filter-count']").TextContent.Trim());
            Assert.Equal("Open filters, 3 active filters", filterButton.GetAttribute("aria-label"));
            var heading = component.Find("[data-testid='calendar-focus-target']");
            Assert.Equal("-1", heading.GetAttribute("tabindex"));
        });

        component.Find("button[title='Open filters']").Click();

        component.WaitForAssertion(() =>
        {
            var pane = component.Find("[data-testid='filter-pane']");
            Assert.Contains("Runtime from", pane.TextContent);
            Assert.Contains("Runtime to", pane.TextContent);
            Assert.Contains("Clear filters", pane.TextContent);
        });
    }

    [Fact]
    public void CalendarPage_FilterButtonCountGroupsSupportingFieldsWithVisibleFilterControls()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/movies?week=2026-05-04&movieSources=Netflix&movieWatchRegion=BE&movieCertifications=US%3APG-13&movieCertificationCountry=US");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Equal("2", component.Find("[data-testid='active-filter-count']").TextContent.Trim());
        });
    }

    [Fact]
    public void CalendarPage_FilterButtonCountDoesNotDoubleCountMirroredLegacyGlobalFilters()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04&q=north&runtimeMin=80&runtimeMax=150");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Equal("2", component.Find("[data-testid='active-filter-count']").TextContent.Trim());
        });
    }

    [Fact]
    public void CalendarPage_FilterButtonCountIgnoresSortAndScoreSourceSelection()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04&sort=title&dir=desc&score=imdb");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Empty(component.FindAll("[data-testid='active-filter-count']"));
        });
    }

    [Fact]
    public void CalendarPage_FilterButtonCountIncludesSeriesScope()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-11&day=2026-05-17&seriesScope=new&seriesLang=en,nl");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Equal("2", component.Find("[data-testid='active-filter-count']").TextContent.Trim());
        });
    }

    [Fact]
    public void CalendarPage_FilterButtonCountIgnoresUnsupportedMovieScopeQuery()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/movies?week=2026-05-11&movieScope=new&movieLang=en");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Equal("1", component.Find("[data-testid='active-filter-count']").TextContent.Trim());
        });
    }

    [Fact]
    public void CalendarPage_FilterButtonCountIgnoresUnsupportedSeriesMovieFilterQuery()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-11&seriesLang=en&seriesReleaseTypes=3&seriesCertifications=US%3APG-13&seriesCertificationCountry=US");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Equal("1", component.Find("[data-testid='active-filter-count']").TextContent.Trim());
        });
    }

    [Fact]
    public void CalendarPage_FilterButtonCountIgnoresUnsupportedMovieSeriesFilterQuery()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/movies?week=2026-05-11&movieLang=en&movieStatuses=Returning%20Series&movieTypes=Scripted");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Equal("1", component.Find("[data-testid='active-filter-count']").TextContent.Trim());
        });
    }

    [Fact]
    public void CalendarPage_FilterButtonCountIncludesGlobalAndMediaFilters()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04&q=north&seriesWatchRegion=BE");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Empty(component.FindAll("[data-testid='active-filter-strip']"));
            Assert.Equal("2", component.Find("[data-testid='active-filter-count']").TextContent.Trim());
        });
    }

    [Fact]
    public void CalendarPage_FilterButtonCountTreatsPluralCriteriaAsSingleFilters()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/?week=2026-05-04&seriesOrigins=BE,US&seriesAvailabilities=flatrate,free&seriesStatuses=Returning%20Series%7CEnded");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Empty(component.FindAll("[data-testid='active-filter-strip']"));
            Assert.Equal("3", component.Find("[data-testid='active-filter-count']").TextContent.Trim());
        });
    }

    [Fact]
    public void CalendarPage_FilterDialogUsesSelectedScoreCopy()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls));

        component.Find("button[title='Open filters']").Click();

        component.WaitForAssertion(() =>
        {
            var pane = component.Find("[data-testid='filter-pane']");
            Assert.Equal("filter-pane-summary", pane.GetAttribute("aria-describedby"));
            Assert.Equal("status", component.Find("#filter-pane-summary").GetAttribute("role"));
            Assert.Equal("polite", component.Find("#filter-pane-summary").GetAttribute("aria-live"));
            Assert.Contains("Selected score", pane.TextContent);
            Assert.Contains("Selected-source votes", pane.TextContent);
            Assert.Contains("Vote count", pane.TextContent);
            Assert.DoesNotContain("TMDb user score", pane.TextContent);
            Assert.DoesNotContain("TMDb votes", pane.TextContent);
        });
    }

    [Fact]
    public async Task CalendarPage_SaveCanonicalizesUrlByOmittingDefaultFilterParameters()
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
        await Task.Delay(200);
        Assert.Single(service.Calls);
    }

    [Fact]
    public void CalendarPage_PlainAllUrlDoesNotRestoreSeriesOrMovieSavedFilters()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        JSInterop.Setup<string?>(
                "premiereFilterStorage.get",
                "premiere-calendar:filters:v2:all")
            .SetResult(null);
        JSInterop.Setup<string?>(
                "premiereFilterStorage.get",
                "premiere-calendar:filters:v2:series")
            .SetResult("week=2026-05-04&seriesLang=nl&seriesScope=new");
        JSInterop.Setup<string?>(
                "premiereFilterStorage.get",
                "premiere-calendar:filters:v2:movies")
            .SetResult("week=2026-05-04&movieLang=en&movieRuntimeMin=45");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Single(service.Calls);
            Assert.DoesNotContain("seriesLang=", navigation.Uri);
            Assert.DoesNotContain("movieLang=", navigation.Uri);
            Assert.DoesNotContain("movieRuntimeMin=", navigation.Uri);
        });
    }

    [Fact]
    public void CalendarPage_ShowsRouteScopedCacheFreshness()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        _cacheMaintenance.DefaultMetadata = new CalendarCacheMetadata(
            DateTimeOffset.Parse("2026-05-10T08:15:00Z"),
            ItemCount: 12,
            SchemaVersion: 3,
            CalendarCacheCompleteness.Complete);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            var freshness = component.Find("[data-testid='data-freshness-card']");
            Assert.Contains("Series cache", freshness.TextContent);
            Assert.Contains("10 May", freshness.TextContent);
            Assert.DoesNotContain("Movies cache", freshness.TextContent);
        });
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
            Assert.Contains("2 total", progress.TextContent);
            Assert.Empty(component.FindAll("[data-testid='query-progress-details']"));
            Assert.Null(component.Find("[data-testid='query-progress-toggle']").GetAttribute("aria-controls"));
            Assert.Single(component.FindAll("[data-testid='premiere-card']"));
        });

        ExpandQueryProgress(component);

        var expandedProgress = component.Find("[data-testid='query-progress']");
        Assert.Contains("Fake source one", expandedProgress.TextContent);
        Assert.Contains("Fake source two", expandedProgress.TextContent);
        Assert.Contains("Complete", expandedProgress.TextContent);
        Assert.Single(component.FindAll("[data-testid='query-progress-details']"));
        Assert.Equal(
            "query-progress-details",
            component.Find("[data-testid='query-progress-toggle']").GetAttribute("aria-controls"));
    }

    [Fact]
    public void CalendarPage_CollapsedQueryProgressShowsActiveSourceAndClearAction()
    {
        var service = new FakePremiereService
        {
            ReportPartialProgress = true
        };
        Services.AddSingleton<IPremiereService>(service);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        ExpandQueryProgress(component);

        component.WaitForAssertion(() => Assert.NotEmpty(component.FindAll(".query-progress-entry")));
        component.FindAll(".query-progress-entry")[0].Click();

        component.WaitForAssertion(() =>
        {
            var progress = component.Find("[data-testid='query-progress']");
            Assert.Contains("Fake source one", progress.TextContent);
            Assert.NotEmpty(progress.QuerySelectorAll("button[aria-label='Clear loaded-source filter']"));
            Assert.NotEmpty(progress.QuerySelectorAll("button[data-focus-restore='calendar-heading']"));
        });

        component.Find("[data-testid='query-progress-toggle']").Click();

        component.WaitForAssertion(() =>
        {
            var progress = component.Find("[data-testid='query-progress']");
            Assert.Empty(component.FindAll("[data-testid='query-progress-details']"));
            Assert.Contains("Fake source one", progress.TextContent);
            Assert.NotEmpty(progress.QuerySelectorAll("button[aria-label='Clear loaded-source filter']"));
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

        ExpandQueryProgress(component);

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

        ExpandQueryProgress(component);

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

        ExpandQueryProgress(component);

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

        ExpandQueryProgress(component);

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

        ExpandQueryProgress(component);

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

        ExpandQueryProgress(component);

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
        component.Find("button[title='Refresh sources from providers']").Click();

        component.WaitForAssertion(() => Assert.True(service.Calls.Count >= 2));
        Assert.Equal(new DateOnly(2026, 5, 5), service.Calls.Last().PriorityDate);
    }

    [Fact]
    public void CalendarPage_OffersUpdateAndRefreshSourceModes()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        var commandBar = component.Find("[data-testid='calendar-command-bar']");
        Assert.Contains("Update", commandBar.TextContent);
        Assert.Contains("Refresh sources", commandBar.TextContent);
        Assert.Contains("Actions", commandBar.TextContent);
        Assert.DoesNotContain("Quick", commandBar.TextContent);
        Assert.DoesNotContain("Full", commandBar.TextContent);
        Assert.DoesNotContain("Command", commandBar.TextContent);

        component.Find("button[title='Update visible week']").Click();
        component.WaitForAssertion(() => Assert.True(service.Calls.Count >= 2));
        Assert.False(service.Calls.Last().ForceRefresh);

        component.Find("button[title='Refresh sources from providers']").Click();
        component.WaitForAssertion(() => Assert.True(service.Calls.Count >= 3));
        Assert.True(service.Calls.Last().ForceRefresh);
    }

    [Fact]
    public void CalendarPage_SavesAndAppliesFilterPresets()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/movies?week=2026-05-04&movieWatchRegion=be");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        Assert.Empty(component.FindAll(".preset-command-row"));
        component.Find("button[title='Open actions (Ctrl+K)']").Click();
        component.Find("input[aria-label='Preset name']").Input("Belgian movies");
        component.Find("button[title='Save current filters as preset']").Click();

        component.WaitForAssertion(() =>
            Assert.Contains("Belgian movies", component.Find("select[aria-label='Saved filter presets']").TextContent));
    }

    [Fact]
    public void CalendarPage_ShowsCommandPalette()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        component.Find("button[title='Open actions (Ctrl+K)']").Click();

        var palette = component.Find("[data-testid='command-palette']");
        Assert.Single(component.FindAll("[data-command-palette-toggle]"));
        Assert.Single(component.FindAll("[data-command-palette-panel]"));
        Assert.Contains("Filters", palette.TextContent);
        Assert.Contains("Settings", palette.TextContent);
        Assert.Contains("Update visible week", palette.TextContent);
        Assert.Contains("Saved filter presets", palette.TextContent);
    }

    [Fact]
    public void CalendarPage_LoadsSelectedDayFromDayQuery()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04&day=2026-05-06");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        Assert.Contains("active", component.Find("button[data-day-target='premiere-day-20260506']").ClassName);
        Assert.Equal(new DateOnly(2026, 5, 6), service.Calls.Last().PriorityDate);
    }

    [Fact]
    public void CalendarPage_ClickingDayUpdatesDayQueryWithoutReloading()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        component.Find("button[data-day-target='premiere-day-20260505']").Click();

        component.WaitForAssertion(() =>
        {
            var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
            Assert.Equal("2026-05-04", query["week"].ToString());
            Assert.Equal("2026-05-05", query["day"].ToString());
            Assert.Single(service.Calls);
        });
    }

    [Fact]
    public void CalendarPage_ClickingDayPublishesViewSyncUrl()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        _viewSyncService.PublishedUrls.Clear();
        component.Find("button[data-day-target='premiere-day-20260505']").Click();

        component.WaitForAssertion(() =>
            Assert.Contains("/series?week=2026-05-04&day=2026-05-05", _viewSyncService.PublishedUrls));
    }

    [Fact]
    public void CalendarPage_RemoteViewSyncDayChangeInSameWeekUpdatesSelectedDay()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04&day=2026-05-05");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls));

        component.InvokeAsync(() => _viewSyncService.RaiseStateChanged(new ViewSyncGroupState(
            "group-a",
            "series",
            "/series?week=2026-05-04&day=2026-05-06",
            10,
            DateTimeOffset.Parse("2026-05-10T10:01:00Z"),
            "device-b",
            "Tablet")));

        component.WaitForAssertion(() =>
        {
            Assert.Contains("day=2026-05-06", navigation.Uri);
            Assert.Contains("active", component.Find("button[data-day-target='premiere-day-20260506']").ClassName);
            Assert.DoesNotContain("active", component.Find("button[data-day-target='premiere-day-20260505']").ClassName);
            Assert.Single(service.Calls);
        });
    }

    [Fact]
    public void CalendarPage_RemovingDayQueryClearsSameWeekSelectedDay()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04&day=2026-05-06");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls));
        Assert.Contains("active", component.Find("button[data-day-target='premiere-day-20260506']").ClassName);

        navigation.NavigateTo("/series?week=2026-05-04");

        component.WaitForAssertion(() =>
        {
            Assert.Contains("active", component.Find("button[data-day-target='premiere-day-20260504']").ClassName);
            Assert.DoesNotContain("active", component.Find("button[data-day-target='premiere-day-20260506']").ClassName);
            Assert.Single(service.Calls);
        });
    }

    [Fact]
    public async Task CalendarPage_ScrollAdjacentDayAcrossWeekBoundaryLoadsAdjacentWeek()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        component.Find("button[data-day-target='premiere-day-20260510']").Click();

        var week = component.FindComponent<CalendarWeek>();
        await week.InvokeAsync(() => week.Instance.SelectAdjacentDayByScrollAsync(1));

        component.WaitForAssertion(() => Assert.True(service.Calls.Count >= 2));
        Assert.Equal(new DateOnly(2026, 5, 11), service.Calls.Last().Start);
        Assert.Equal(new DateOnly(2026, 5, 11), service.Calls.Last().PriorityDate);
        var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        Assert.Equal("2026-05-11", query["week"].ToString());
        Assert.Equal("2026-05-11", query["day"].ToString());
    }

    [Fact]
    public void CalendarPage_ExplicitCalendarUrlPublishesRelativeViewSyncUrl()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04&day=2026-05-06&seriesLang=en,nl");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Contains(
                "/series?week=2026-05-04&day=2026-05-06&seriesLang=en,nl",
                _viewSyncService.PublishedUrls);
        });
    }

    [Fact]
    public void CalendarPage_PlainCalendarUrlAppliesLatestSyncedUrlBeforeLoading()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        _viewSyncService.SetGroupState(new ViewSyncGroupState(
            "group-a",
            "movies",
            "/movies?week=2026-04-27&day=2026-04-28&movieRuntimeMin=45",
            4,
            DateTimeOffset.Parse("2026-05-10T10:00:00Z"),
            "device-b",
            "Living room"));
        navigation.NavigateTo("/movies");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("week=2026-04-27", navigation.Uri);
            Assert.Contains("day=2026-04-28", navigation.Uri);
            Assert.Equal(new DateOnly(2026, 4, 27), service.Calls.Last().Start);
            Assert.Equal(new DateOnly(2026, 4, 28), service.Calls.Last().PriorityDate);
        });
    }

    [Fact]
    public void CalendarPage_PlainCalendarUrlPrefersOwnGroupStateOverLocalSavedFilters()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        JSInterop.Setup<string?>(
                "premiereFilterStorage.get",
                "premiere-calendar:filters:v2:series")
            .SetResult("seriesLang=nl");
        var navigation = Services.GetRequiredService<NavigationManager>();
        _viewSyncService.SetGroupState(new ViewSyncGroupState(
            "group-a",
            "series",
            "/series?week=2026-05-18&day=2026-05-19&seriesLang=en",
            4,
            DateTimeOffset.Parse("2026-05-10T10:00:00Z"),
            "device-a",
            "Office PC"));
        navigation.NavigateTo("/series");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("week=2026-05-18", navigation.Uri);
            Assert.Contains("day=2026-05-19", navigation.Uri);
            Assert.Contains("seriesLang=en", navigation.Uri);
            Assert.DoesNotContain("seriesLang=nl", navigation.Uri);
        });
    }

    [Fact]
    public void CalendarPage_NavigationOnlyUrlPrefersGroupStateOverLocalSavedFilters()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        JSInterop.Setup<string?>(
                "premiereFilterStorage.get",
                "premiere-calendar:filters:v2:series")
            .SetResult("seriesLang=nl");
        var navigation = Services.GetRequiredService<NavigationManager>();
        _viewSyncService.SetGroupState(new ViewSyncGroupState(
            "group-a",
            "series",
            "/series?week=2026-05-18&day=2026-05-19&seriesLang=en",
            4,
            DateTimeOffset.Parse("2026-05-10T10:00:00Z"),
            "device-b",
            "Office PC"));
        navigation.NavigateTo("/series?week=2026-05-04&day=2026-05-05");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("week=2026-05-18", navigation.Uri);
            Assert.Contains("day=2026-05-19", navigation.Uri);
            Assert.Contains("seriesLang=en", navigation.Uri);
            Assert.DoesNotContain("seriesLang=nl", navigation.Uri);
        });
    }

    [Fact]
    public void CalendarPage_DateOnlyViewSyncUrlDoesNotMergeLocalSavedFilters()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        JSInterop.Setup<string?>(
                "premiereFilterStorage.get",
                "premiere-calendar:filters:v2:series")
            .SetResult("seriesLang=nl");
        var navigation = Services.GetRequiredService<NavigationManager>();
        _viewSyncService.SetGroupState(new ViewSyncGroupState(
            "group-a",
            "series",
            "/series?week=2026-05-18&day=2026-05-19",
            4,
            DateTimeOffset.Parse("2026-05-10T10:00:00Z"),
            "device-b",
            "Office PC"));
        navigation.NavigateTo("/series");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("week=2026-05-18", navigation.Uri);
            Assert.Contains("day=2026-05-19", navigation.Uri);
            Assert.DoesNotContain("seriesLang=nl", navigation.Uri);
        });
    }

    [Fact]
    public void CalendarPage_PlainCalendarUrlFallsBackToLocalSavedFiltersWhenGroupRouteIsMissing()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        JSInterop.Setup<string?>(
                "premiereFilterStorage.get",
                "premiere-calendar:filters:v2:series")
            .SetResult("week=2026-05-04&day=2026-05-05&seriesLang=nl");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("seriesLang=nl", navigation.Uri);
        });
    }

    [Fact]
    public void CalendarPage_PlainSeriesUrlAppliesLatestSeriesViewInsteadOfLatestMovieView()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        _viewSyncService.SetGroupState(new ViewSyncGroupState(
            "group-a",
            "movies",
            "/movies?week=2026-04-27&day=2026-04-28",
            5,
            DateTimeOffset.Parse("2026-05-10T10:00:00Z"),
            "device-b",
            "Living room"));
        _viewSyncService.SetGroupState(new ViewSyncGroupState(
            "group-a",
            "series",
            "/series?week=2026-05-11&day=2026-05-12",
            2,
            DateTimeOffset.Parse("2026-05-10T09:00:00Z"),
            "device-b",
            "Living room"));
        navigation.NavigateTo("/series");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("/series?", navigation.Uri);
            Assert.Contains("week=2026-05-11", navigation.Uri);
            Assert.Contains("day=2026-05-12", navigation.Uri);
            Assert.Equal(new DateOnly(2026, 5, 11), service.Calls.Last().Start);
        });
    }

    [Fact]
    public void CalendarPage_RemoteViewSyncEventNavigatesWithoutRepublishing()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04&day=2026-05-05");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls));
        _viewSyncService.PublishedUrls.Clear();

        component.InvokeAsync(() => _viewSyncService.RaiseStateChanged(new ViewSyncGroupState(
            "group-a",
            "series",
            "/series?week=2026-05-11&day=2026-05-12",
            10,
            DateTimeOffset.Parse("2026-05-10T10:01:00Z"),
            "device-b",
            "Tablet")));

        component.WaitForAssertion(() =>
        {
            Assert.Contains("week=2026-05-11", navigation.Uri);
            Assert.Contains("day=2026-05-12", navigation.Uri);
            Assert.DoesNotContain("/series?week=2026-05-11&day=2026-05-12", _viewSyncService.PublishedUrls);
        });
    }

    [Fact]
    public void CalendarPage_ClosingFiltersLeavesQueuedViewSyncForExplicitChoice()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04&day=2026-05-05");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls));

        component.Find("button[title='Open filters']").Click();
        component.InvokeAsync(() => _viewSyncService.RaiseStateChanged(new ViewSyncGroupState(
            "group-a",
            "series",
            "/series?week=2026-05-11&day=2026-05-12",
            10,
            DateTimeOffset.Parse("2026-05-10T10:01:00Z"),
            "device-b",
            "Tablet")));

        component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='filter-pane']")));
        component.Find("button[title='Cancel filter changes']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("week=2026-05-04", navigation.Uri);
            Assert.Contains("day=2026-05-05", navigation.Uri);
            Assert.Single(component.FindAll("[data-testid='view-sync-queued']"));
            Assert.DoesNotContain("week=2026-05-11", navigation.Uri);
        });
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

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            return new CollectingLogger(categoryName, Entries);
        }

        public void Dispose()
        {
        }

        private sealed class CollectingLogger(string categoryName, List<LogEntry> entries) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull
            {
                return NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                entries.Add(new LogEntry(categoryName, logLevel, formatter(state, exception), exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class FakeCalendarCacheMaintenance : ICalendarCacheMaintenance
    {
        public CalendarCacheMetadata? DefaultMetadata { get; set; }

        public List<(DateOnly Start, DateOnly End, string CacheKey)> MetadataCalls { get; } = [];

        public Task<CalendarCacheMetadata?> GetWeekMetadataAsync(
            DateOnly start,
            DateOnly end,
            string cacheKey,
            CancellationToken cancellationToken)
        {
            MetadataCalls.Add((start, end, cacheKey));
            return Task.FromResult(DefaultMetadata);
        }

        public Task<int> CleanupAsync(
            DateTimeOffset nowUtc,
            TimeSpan retention,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }
    }

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

    private sealed class FakeViewSyncService : IViewSyncService
    {
        public event EventHandler<ViewSyncStateChangedEventArgs>? StateChanged;

        public List<string> PublishedUrls { get; } = [];

        public ViewSyncGroupState? GroupState { get; set; }

        public Exception? GetOverviewException { get; set; }

        private readonly Dictionary<string, ViewSyncGroupState> _statesByRoute = new(StringComparer.OrdinalIgnoreCase);

        public Task<ViewSyncOverview> GetOverviewAsync(string deviceId, CancellationToken cancellationToken)
        {
            if (GetOverviewException is not null)
            {
                throw GetOverviewException;
            }

            return Task.FromResult(Overview(deviceId));
        }

        public Task<ViewSyncOverview> SaveDeviceAsync(
            string deviceId,
            string displayName,
            bool syncEnabled,
            string? groupId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Overview(deviceId));
        }

        public Task<ViewSyncGroup> CreateGroupAsync(string name, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ViewSyncGroup("group-a", name, DateTimeOffset.Parse("2026-05-10T10:00:00Z")));
        }

        public Task<ViewSyncOverview> UngroupDeviceAsync(string deviceId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Overview(deviceId));
        }

        public Task<ViewSyncPublishResult> PublishUrlAsync(string deviceId, string relativeUrl, CancellationToken cancellationToken)
        {
            PublishedUrls.Add(relativeUrl);
            var routeKey = ViewSyncUrlPolicy.RouteKeyFor(relativeUrl) ?? "all";
            GroupState = new ViewSyncGroupState(
                "group-a",
                routeKey,
                relativeUrl,
                (GroupState?.Revision ?? 0) + 1,
                DateTimeOffset.Parse("2026-05-10T10:00:00Z"),
                deviceId,
                "Office PC");
            _statesByRoute[routeKey] = GroupState;
            return Task.FromResult(new ViewSyncPublishResult(true, GroupState));
        }

        public Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(string deviceId, CancellationToken cancellationToken)
        {
            return Task.FromResult(GroupState);
        }

        public Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(string deviceId, string? routeKey, CancellationToken cancellationToken)
        {
            return Task.FromResult(routeKey is null
                ? GroupState
                : _statesByRoute.GetValueOrDefault(routeKey));
        }

        public void RaiseStateChanged(ViewSyncGroupState state)
        {
            SetGroupState(state);
            StateChanged?.Invoke(this, new ViewSyncStateChangedEventArgs(state.GroupId, state));
        }

        public void SetGroupState(ViewSyncGroupState state)
        {
            GroupState = state;
            _statesByRoute[state.RouteKey] = state;
        }

        private ViewSyncOverview Overview(string deviceId)
        {
            var device = new ViewSyncDevice(
                deviceId,
                "Office PC",
                SyncEnabled: true,
                "group-a",
                DateTimeOffset.Parse("2026-05-10T10:00:00Z"));
            var group = new ViewSyncGroup("group-a", "Shared", DateTimeOffset.Parse("2026-05-10T10:00:00Z"));
            return new ViewSyncOverview(device, [group], [device], GroupState);
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
        public IntegrationSettings Settings { get; set; } = new();

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

    private sealed class ThrowingIntegrationSettingsStore : IIntegrationSettingsStore
    {
        public Task<IntegrationSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            throw new IOException("Settings database is unavailable.");
        }

        public Task SaveAsync(IntegrationSettings settings, CancellationToken cancellationToken = default)
        {
            throw new IOException("Settings database is unavailable.");
        }
    }

    private sealed class TransientFailingIntegrationSettingsStore : IIntegrationSettingsStore
    {
        private bool _hasFailed;

        public IntegrationSettings Settings { get; set; } = new();

        public int GetCallCount { get; private set; }

        public Task<IntegrationSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            GetCallCount++;
            if (!_hasFailed)
            {
                _hasFailed = true;
                throw new IOException("Settings database is temporarily unavailable.");
            }

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

    private sealed class InMemoryAppStateStore : IAppStateStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
        {
            _values.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetValuesByPrefixAsync(string prefix, CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<string, string> values = _values
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            return Task.FromResult(values);
        }
    }
}
