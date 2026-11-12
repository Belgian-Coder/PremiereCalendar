using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.ComponentTests;

public sealed partial class CalendarPageTests
{
    private static PremiereItem WeekSeries(int id, string title, DateOnly day, string? poster = null)
    {
        return new PremiereItem
        {
            CanonicalId = $"tv:{id}",
            Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series,
            TmdbId = id,
            Title = title,
            PremiereDate = day,
            OriginalLanguage = "en",
            RuntimeMinutes = 50,
            PosterUrl = poster
        };
    }

    [Fact]
    public void CalendarPage_NextWeekOpensTheFirstDayEvenWhenItIsEmptyAndAnotherDayHasItems()
    {
        var service = new ScriptedWeekService
        {
            ItemsFor = _ => [WeekSeries(1, "Midweek Show", new DateOnly(2026, 5, 20))]
        };
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-11&day=2026-05-13");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls));

        component.Find("button[title='Next week']").Click();

        component.WaitForAssertion(() =>
        {
            var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
            Assert.Equal("2026-05-18", query["week"].ToString());
            Assert.Equal("2026-05-18", query["day"].ToString());
            Assert.Contains("active", component.Find("button[data-day-target='premiere-day-20260518']").ClassName);
            Assert.DoesNotContain("active", component.Find("button[data-day-target='premiere-day-20260520']").ClassName);
            Assert.Equal("No premieres", component.Find("[data-testid='calendar-day'] .empty-day").TextContent.Trim());
        });
    }

    [Fact]
    public void CalendarPage_WeekBoardStaysMountedWithLoadingStateWhileTheNextWeekLoads()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ScriptedWeekService
        {
            ItemsFor = start => [WeekSeries(1, "First Day Show", start), WeekSeries(2, "Second Day Show", start.AddDays(1))],
            GateForCall = call => call == 2 ? gate.Task : null
        };
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-11");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Contains("First Day Show", component.Markup));

        try
        {
            component.Find("button[title='Next week']").Click();

            component.WaitForAssertion(() =>
            {
                Assert.Equal(2, service.Calls.Count);
                Assert.Equal(7, component.FindAll("button[data-day-button]").Count);
                Assert.Single(component.FindAll("[data-testid='calendar-week']"));
                var loading = Assert.Single(component.FindAll("[data-testid='loading']"));
                // The in-day text is a plain paragraph; one persistent live region announces the state.
                Assert.Null(loading.GetAttribute("role"));
                Assert.Null(loading.GetAttribute("aria-live"));
                Assert.Contains("Loading premieres", loading.TextContent);
                var announcement = Assert.Single(component.FindAll("[data-testid='loading-announcement']"));
                Assert.Equal("status", announcement.GetAttribute("role"));
                Assert.Equal("polite", announcement.GetAttribute("aria-live"));
                Assert.Contains("Loading premieres", announcement.TextContent);
                Assert.All(component.FindAll(".day-jump-count"), count => Assert.Equal("Loading…", count.TextContent.Trim()));
                Assert.Empty(component.FindAll("[data-testid='premiere-card']"));
                Assert.DoesNotContain("First Day Show", component.Markup);
                Assert.Equal("Loading", component.Find(".calendar-result-count").TextContent.Trim());
                var skeletons = component.FindAll(".premiere-card-skeleton");
                Assert.Equal(3, skeletons.Count);
                Assert.All(skeletons, skeleton => Assert.Equal("true", skeleton.GetAttribute("aria-hidden")));
            });
        }
        finally
        {
            gate.TrySetResult();
        }

        component.WaitForAssertion(() =>
        {
            Assert.Empty(component.FindAll("[data-testid='loading']"));
            Assert.Empty(component.FindAll(".premiere-card-skeleton"));
            Assert.Equal("", component.Find("[data-testid='loading-announcement']").TextContent.Trim());
            Assert.Contains("First Day Show", component.Markup);
            Assert.DoesNotContain("Loading…", component.Markup);
        });
        Assert.Equal(2, service.Calls.Count);
    }

    [Fact]
    public void CalendarPage_WeekNavigationStartsExactlyOneLoadPerClick()
    {
        var service = new ScriptedWeekService { ItemsFor = start => [WeekSeries(1, "Show", start)] };
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-11");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls));

        component.Find("button[title='Next week']").Click();
        component.WaitForAssertion(() => Assert.Equal(2, service.Calls.Count));
        component.Find("button[title='Previous week']").Click();
        component.WaitForAssertion(() => Assert.Equal(3, service.Calls.Count));
        component.Find("button[title='This week']").Click();
        component.WaitForAssertion(() => Assert.Equal(4, service.Calls.Count));

        Assert.Equal(
            [new DateOnly(2026, 5, 11), new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 11)],
            service.Calls.Take(3).Select(call => call));
    }

    [Fact]
    public async Task CalendarPage_ReplacesAQueryUrlOnceWithTheExplicitProfileWithoutReloading()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-11&day=2026-05-13");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        const string explicitUrl = "http://localhost/series?week=2026-05-11&day=2026-05-13&seriesLang=en,nl&seriesRuntimeMin=0&seriesRuntimeMax=360";
        component.WaitForAssertion(() => Assert.Equal(explicitUrl, navigation.Uri));
        await Task.Delay(300);

        Assert.Single(service.Calls);
        Assert.Equal(explicitUrl, navigation.Uri);
        Assert.Equal(["/series?week=2026-05-11&day=2026-05-13&seriesLang=en,nl&seriesRuntimeMin=0&seriesRuntimeMax=360"], _viewSyncService.PublishedUrls);
    }

    [Fact]
    public async Task CalendarPage_LeavesAPlainUrlWithoutQueryUntouched()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        await Task.Delay(200);
        Assert.Equal("http://localhost/series", navigation.Uri);
        Assert.Empty(_viewSyncService.PublishedUrls);
        Assert.Single(service.Calls);
    }

    [Fact]
    public void CalendarPage_CopyViewLinkCopiesTheExplicitFilterUrl()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls));

        component.Find("button[data-command-palette-toggle]").Click();
        component.FindAll(".command-palette-item")
            .Single(button => button.TextContent.Contains("Copy view link", StringComparison.Ordinal))
            .Click();

        var copied = Assert.Single(JSInterop.Invocations["premiereCalendarActions.copyText"]).Arguments[0] as string;
        Assert.NotNull(copied);
        Assert.StartsWith("http://localhost/series?week=", copied);
        Assert.Contains("seriesLang=en,nl", copied);
        Assert.Contains("seriesRuntimeMin=0", copied);
        Assert.Contains("seriesRuntimeMax=360", copied);
    }

    [Theory]
    [InlineData("/series?week=2026-05-11")]
    [InlineData("/movies?week=2026-05-11")]
    [InlineData("/?week=2026-05-11")]
    [InlineData("/series?week=2026-05-11&seriesLang=all&seriesRuntimeMin=30&seriesRuntimeMax=200")]
    [InlineData("/movies?week=2026-05-11&movieLang=fr,nl&movieRuntimeMin=0&movieRuntimeMax=240")]
    [InlineData("/?week=2026-05-11&seriesLang=nl&movieLang=en,fr&movieRuntimeMin=90")]
    [InlineData("/?week=2026-05-11&seriesLang=all&movieLang=all&seriesRuntimeMin=20&seriesRuntimeMax=100&movieRuntimeMin=0&movieRuntimeMax=360")]
    public async Task CalendarPage_ExplicitUrlRoundTripsToTheSameCriteriaAndMediaFilters(string url)
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(url);
        var first = Render<PremiereCalendar.Components.Pages.Calendar>();
        first.WaitForAssertion(() =>
        {
            Assert.Single(service.Calls);
            Assert.Contains("RuntimeMax=", navigation.Uri);
        });
        var canonical = navigation.Uri;
        var firstFilters = Assert.Single(service.FilterSnapshots)!;
        await first.InvokeAsync(first.Instance.Dispose);

        navigation.NavigateTo(canonical);
        var second = Render<PremiereCalendar.Components.Pages.Calendar>();
        second.WaitForAssertion(() => Assert.Equal(2, service.Calls.Count));
        await Task.Delay(200);
        var secondFilters = service.FilterSnapshots[1]!;

        Assert.Equal(canonical, navigation.Uri);
        Assert.Equal(2, service.Calls.Count);
        Assert.Equal(
            PremiereDiscoveryCriteria.FromFilters(firstFilters).CacheKey(),
            PremiereDiscoveryCriteria.FromFilters(secondFilters).CacheKey());
        Assert.Equal(firstFilters.SeriesFilters.OriginalLanguages, secondFilters.SeriesFilters.OriginalLanguages);
        Assert.Equal(firstFilters.MovieFilters.OriginalLanguages, secondFilters.MovieFilters.OriginalLanguages);
        Assert.Equal(firstFilters.SeriesFilters.RuntimeMinMinutes, secondFilters.SeriesFilters.RuntimeMinMinutes);
        Assert.Equal(firstFilters.SeriesFilters.RuntimeMaxMinutes, secondFilters.SeriesFilters.RuntimeMaxMinutes);
        Assert.Equal(firstFilters.MovieFilters.RuntimeMinMinutes, secondFilters.MovieFilters.RuntimeMinMinutes);
        Assert.Equal(firstFilters.MovieFilters.RuntimeMaxMinutes, secondFilters.MovieFilters.RuntimeMaxMinutes);
    }

    [Fact]
    public void CalendarPage_StartsAdjacentWeekPrefetchOncePerLoadWhenReadyButNotForStaleOrFailedReadyUpdates()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ScriptedWeekService
        {
            ItemsFor = start => [WeekSeries(1, "Show", start)],
            EmitReady = true,
            ReadyIsStale = true,
            FinalGate = gate.Task
        };
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-11");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        try
        {
            component.WaitForAssertion(() => Assert.Contains("Show", component.Markup));
            Assert.Empty(_prefetcher.WeekStarts);
        }
        finally
        {
            gate.TrySetResult();
        }

        // The authoritative end-of-stream update still starts the prefetch exactly once.
        component.WaitForAssertion(() => Assert.Equal([new DateOnly(2026, 5, 11)], _prefetcher.WeekStarts));
    }

    [Fact]
    public async Task CalendarPage_EnqueuesSelectedDayPostersFirstWhenTheCalendarIsReadyAndAgainAtTheFinalUpdate()
    {
        var queue = new FakePosterWarmupQueue();
        Services.AddSingleton<IPosterWarmupQueue>(queue);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string Poster(string name) => $"https://image.tmdb.org/t/p/w185/{name}.jpg";
        var service = new ScriptedWeekService
        {
            ItemsFor = _ =>
            [
                WeekSeries(1, "Mon Show", new DateOnly(2026, 5, 4), Poster("mon")),
                WeekSeries(2, "Wed A", new DateOnly(2026, 5, 6), Poster("wed-a")),
                WeekSeries(3, "Tue Show", new DateOnly(2026, 5, 5), Poster("tue")),
                WeekSeries(4, "Wed B", new DateOnly(2026, 5, 6), Poster("wed-b")),
                WeekSeries(5, "No Poster", new DateOnly(2026, 5, 6)),
                WeekSeries(6, "Next Week", new DateOnly(2026, 5, 11), Poster("next"))
            ],
            EmitReady = true,
            FinalGate = gate.Task
        };
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04&day=2026-05-06");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        string[] expected = [Poster("wed-a"), Poster("wed-b"), Poster("mon"), Poster("tue")];
        try
        {
            component.WaitForAssertion(() => Assert.Single(queue.Batches));
            Assert.Equal(expected, queue.Batches[0]);
            Assert.Equal([new DateOnly(2026, 5, 4)], _prefetcher.WeekStarts);
        }
        finally
        {
            gate.TrySetResult();
        }

        component.WaitForAssertion(() => Assert.Equal(2, queue.Batches.Count));
        Assert.Equal(expected, queue.Batches[1]);
        await Task.Delay(100);
        Assert.Equal(2, queue.Batches.Count);
        Assert.Single(_prefetcher.WeekStarts);
    }

    private const string CanonicalSeriesUrl = "/series?week=2026-05-11&day=2026-05-12&seriesLang=en,nl&seriesRuntimeMin=0&seriesRuntimeMax=360";
    private const string NextWeekSeriesUrl = "/series?week=2026-05-18&day=2026-05-19&seriesLang=en,nl&seriesRuntimeMin=0&seriesRuntimeMax=360";

    private static ViewSyncGroupState SeriesGroupState(string url, long revision, string device = "device-b")
    {
        return new ViewSyncGroupState(
            "group-a",
            "series",
            url,
            revision,
            DateTimeOffset.Parse("2026-05-10T10:01:00Z"),
            device,
            "Tablet");
    }

    [Fact]
    public async Task CalendarPage_ReloadOnOwnLastUrlFollowsAGroupThatMovedOn()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        JSInterop.Setup<ViewSyncLastSeen?>("premiereViewSync.getLastSeen", "series")
            .SetResult(new ViewSyncLastSeen(CanonicalSeriesUrl, 5, "group-a"));
        _viewSyncService.SetGroupState(SeriesGroupState(NextWeekSeriesUrl, 6));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(CanonicalSeriesUrl);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("week=2026-05-18", navigation.Uri);
            Assert.Equal(new DateOnly(2026, 5, 18), Assert.Single(service.Calls).Start);
        });
        await Task.Delay(200);
        Assert.Single(service.Calls);
        Assert.Empty(_viewSyncService.PublishedUrls);
    }

    [Fact]
    public async Task CalendarPage_ReloadOnOwnLastUrlStaysWhenTheGroupHasNotMovedOn()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        JSInterop.Setup<ViewSyncLastSeen?>("premiereViewSync.getLastSeen", "series")
            .SetResult(new ViewSyncLastSeen(CanonicalSeriesUrl, 5, "group-a"));
        _viewSyncService.SetGroupState(SeriesGroupState(CanonicalSeriesUrl, 5, "device-a"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(CanonicalSeriesUrl);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Equal(new DateOnly(2026, 5, 11), Assert.Single(service.Calls).Start));
        await Task.Delay(200);
        Assert.Equal("http://localhost" + CanonicalSeriesUrl, navigation.Uri);
        Assert.Single(service.Calls);
        Assert.Empty(_viewSyncService.PublishedUrls);
    }

    [Fact]
    public async Task CalendarPage_ExplicitUrlTheDeviceHasNeverSeenStaysAndIsPublishedAndRemembered()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        _viewSyncService.SetGroupState(SeriesGroupState(NextWeekSeriesUrl, 6));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(CanonicalSeriesUrl);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Equal([CanonicalSeriesUrl], _viewSyncService.PublishedUrls));
        await Task.Delay(100);
        Assert.Equal(new DateOnly(2026, 5, 11), Assert.Single(service.Calls).Start);
        Assert.Equal("http://localhost" + CanonicalSeriesUrl, navigation.Uri);
        var remembered = Assert.Single(JSInterop.Invocations["premiereViewSync.setLastSeen"]);
        Assert.Equal("series", remembered.Arguments[0]);
        Assert.Equal(CanonicalSeriesUrl, remembered.Arguments[1]);
        Assert.Equal("group-a", remembered.Arguments[3]);
    }

    [Theory]
    [InlineData("group-z")]
    [InlineData(null)]
    public async Task CalendarPage_LastSeenRecordFromAnotherGroupOrWithoutGroupCountsAsUnseen(string? recordedGroup)
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        JSInterop.Setup<ViewSyncLastSeen?>("premiereViewSync.getLastSeen", "series")
            .SetResult(new ViewSyncLastSeen(CanonicalSeriesUrl, 5, recordedGroup));
        _viewSyncService.SetGroupState(SeriesGroupState(NextWeekSeriesUrl, 6));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(CanonicalSeriesUrl);

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Equal([CanonicalSeriesUrl], _viewSyncService.PublishedUrls));
        await Task.Delay(100);
        Assert.Equal(new DateOnly(2026, 5, 11), Assert.Single(service.Calls).Start);
        Assert.Equal("http://localhost" + CanonicalSeriesUrl, navigation.Uri);
    }

    [Fact]
    public async Task CalendarPage_RememberingTheViewSyncUrlNeverBlocksNavigation()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var pending = JSInterop.SetupVoid("premiereViewSync.setLastSeen", _ => true);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04&day=2026-05-05");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls));

        await component.InvokeAsync(() => _viewSyncService.RaiseStateChanged(SeriesGroupState(
            "/series?week=2026-05-11&day=2026-05-11&seriesLang=en,nl&seriesRuntimeMin=0&seriesRuntimeMax=360", 10)));
        component.Find("button[title='Next week']").Click();

        component.WaitForAssertion(() => Assert.Contains("week=2026-05-18", navigation.Uri));
        pending.SetVoidResult();
    }

    [Fact]
    public void CalendarPage_PlainUrlStillFollowsTheGroupEvenWhenAnotherUrlWasRemembered()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        JSInterop.Setup<ViewSyncLastSeen?>("premiereViewSync.getLastSeen", "series")
            .SetResult(new ViewSyncLastSeen(CanonicalSeriesUrl, 5, "group-a"));
        _viewSyncService.SetGroupState(SeriesGroupState(NextWeekSeriesUrl, 6));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("week=2026-05-18", navigation.Uri);
            Assert.Equal(new DateOnly(2026, 5, 18), Assert.Single(service.Calls).Start);
        });
    }

    [Fact]
    public void CalendarPage_AppliedSyncStateDoesNotBlockTheSavedFilterRestoreOfALaterPlainNavigation()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        JSInterop.Setup<string?>("premiereFilterStorage.get", "premiere-calendar:filters:v2:movies")
            .SetResult("movieLang=fr&movieRuntimeMin=0&movieRuntimeMax=360");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series?week=2026-05-04&day=2026-05-05");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls));

        component.InvokeAsync(() => _viewSyncService.RaiseStateChanged(SeriesGroupState(
            "/series?week=2026-05-04&day=2026-05-06&seriesLang=en,nl&seriesRuntimeMin=0&seriesRuntimeMax=360", 10)));
        component.WaitForAssertion(() => Assert.Contains("day=2026-05-06", navigation.Uri));

        navigation.NavigateTo("/movies");

        component.WaitForAssertion(() => Assert.Contains("movieLang=fr", navigation.Uri));
    }

    [Fact]
    public void CalendarPage_NavigatingBackToTheDevicesOwnEarlierUrlAfterAnAppliedSyncStatePublishesAgain()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        const string ownUrl = "/series?week=2026-05-04&day=2026-05-05&seriesLang=en,nl&seriesRuntimeMin=0&seriesRuntimeMax=360";
        navigation.NavigateTo("/series?week=2026-05-04&day=2026-05-05");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Equal([ownUrl], _viewSyncService.PublishedUrls));

        component.InvokeAsync(() => _viewSyncService.RaiseStateChanged(SeriesGroupState(
            "/series?week=2026-05-11&day=2026-05-11&seriesLang=en,nl&seriesRuntimeMin=0&seriesRuntimeMax=360", 10)));
        component.WaitForAssertion(() => Assert.Contains("week=2026-05-11", navigation.Uri));
        Assert.Single(_viewSyncService.PublishedUrls);

        navigation.NavigateTo("http://localhost" + ownUrl);

        component.WaitForAssertion(() => Assert.Equal([ownUrl, ownUrl], _viewSyncService.PublishedUrls));
    }

    [Fact]
    public async Task CalendarPage_WeekClickStillLoadsWhenASyncStateResetsTheForceFlagBeforeTheLocationChangeArrives()
    {
        var navigation = new DeferredNavigationManager();
        Services.AddSingleton<NavigationManager>(navigation);
        var service = new ScriptedWeekService { ItemsFor = start => [WeekSeries(1, "Show", start.AddDays(1))] };
        Services.AddSingleton<IPremiereService>(service);
        navigation.NavigateTo("/series?week=2026-05-11&day=2026-05-12");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() =>
        {
            Assert.Single(service.Calls);
            Assert.Contains("Show", component.Markup);
            Assert.Contains("RuntimeMax=", navigation.Uri);
        });
        navigation.Deferred = true;

        component.Find("button[title='Next week']").Click();
        // Real Blazor Server delivers LocationChanged a round trip later; a synced state arrives in between
        // for the same week and resets the force-load flag.
        await component.InvokeAsync(() => _viewSyncService.RaiseStateChanged(SeriesGroupState(
            "/series?week=2026-05-18&day=2026-05-20&seriesLang=en,nl&seriesRuntimeMin=0&seriesRuntimeMax=360", 50)));
        component.WaitForAssertion(() => Assert.Contains("day=2026-05-20", navigation.PendingUri));
        Assert.Single(service.Calls);
        Assert.Single(component.FindAll("[data-testid='loading']"));

        navigation.DeliverPending();

        component.WaitForAssertion(() =>
        {
            Assert.Equal(2, service.Calls.Count);
            Assert.Empty(component.FindAll("[data-testid='loading']"));
        });
        Assert.Equal(new DateOnly(2026, 5, 18), service.Calls[1]);
    }

    [Fact]
    public void CalendarPage_LanguageCheckboxTreatsRegionalDutchAsTheSameSelection()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-04&seriesLang=nl-BE");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls));

        component.Find("button[title='Open filters']").Click();
        var checkbox = FilterCheckbox(component, "series-language-filter", "Dutch (NL)");
        Assert.True(checkbox.HasAttribute("checked"));
        checkbox.Change(false);

        component.WaitForAssertion(() =>
            Assert.False(FilterCheckbox(component, "series-language-filter", "Dutch (NL)").HasAttribute("checked")));
    }

    [Fact]
    public void CalendarPage_LoadsTheFilterCatalogWhenAPlainUrlIsRestoredFromSavedFilters()
    {
        var catalog = new CountingFilterCatalogService();
        Services.AddSingleton<IFilterCatalogService>(catalog);
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        JSInterop.Setup<string?>("premiereFilterStorage.get", "premiere-calendar:filters:v2:series")
            .SetResult("seriesLang=nl&seriesRuntimeMin=0&seriesRuntimeMax=360");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("seriesLang=nl", navigation.Uri);
            Assert.Single(service.Calls);
        });
        AssertCatalogLoadedOnceAndShown(component, catalog);
    }

    [Fact]
    public void CalendarPage_LoadsTheFilterCatalogWhenTheFirstRenderFollowsTheSyncGroup()
    {
        var catalog = new CountingFilterCatalogService();
        Services.AddSingleton<IFilterCatalogService>(catalog);
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        _viewSyncService.SetGroupState(SeriesGroupState(NextWeekSeriesUrl, 6));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/series");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("week=2026-05-18", navigation.Uri);
            Assert.Single(service.Calls);
        });
        AssertCatalogLoadedOnceAndShown(component, catalog);
    }

    [Fact]
    public void CalendarPage_LoadsTheFilterCatalogExactlyOnceOnTheNormalFirstRender()
    {
        var catalog = new CountingFilterCatalogService();
        Services.AddSingleton<IFilterCatalogService>(catalog);
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/series?week=2026-05-11&day=2026-05-12");

        var component = Render<PremiereCalendar.Components.Pages.Calendar>();

        component.WaitForAssertion(() => Assert.Single(service.Calls));
        AssertCatalogLoadedOnceAndShown(component, catalog);
    }

    private static void AssertCatalogLoadedOnceAndShown(
        IRenderedComponent<PremiereCalendar.Components.Pages.Calendar> component,
        CountingFilterCatalogService catalog)
    {
        component.WaitForAssertion(() => Assert.Equal(1, catalog.Calls));
        component.Find("button[title='Open filters']").Click();
        component.WaitForAssertion(() =>
            Assert.NotEmpty(FilterCheckboxes(component, "series-language-filter", "German (DE)")));
        Assert.Equal(1, catalog.Calls);
    }

    private sealed class CountingFilterCatalogService : IFilterCatalogService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<FilterCatalog> GetCatalogAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new FilterCatalog
            {
                Languages = [new("en", "English (EN)"), new("de", "German (DE)")],
                Countries = [new("BE", "Belgium (BE)")]
            });
        }
    }

    private sealed class DeferredNavigationManager : NavigationManager
    {
        public DeferredNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/");
        }

        public bool Deferred { get; set; }

        public string PendingUri { get; private set; } = "";

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            var absolute = ToAbsoluteUri(uri).ToString();
            if (Deferred)
            {
                PendingUri = absolute;
                return;
            }

            Uri = absolute;
            NotifyLocationChanged(false);
        }

        public void DeliverPending()
        {
            Uri = PendingUri;
            PendingUri = "";
            NotifyLocationChanged(false);
        }
    }

    private sealed class FakePosterWarmupQueue : IPosterWarmupQueue
    {
        public List<string[]> Batches { get; } = [];

        public void Enqueue(IEnumerable<string> posterUrls)
        {
            lock (Batches)
            {
                Batches.Add(posterUrls.ToArray());
            }
        }
    }

    private sealed class ScriptedWeekService : IPremiereService
    {
        public List<DateOnly> Calls { get; } = [];

        public required Func<DateOnly, IReadOnlyList<PremiereItem>> ItemsFor { get; init; }

        public Func<int, Task?>? GateForCall { get; init; }

        public bool EmitReady { get; init; }

        public bool ReadyIsStale { get; init; }

        public Task? FinalGate { get; init; }

        public Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(
            DateOnly start, DateOnly end, CancellationToken cancellationToken, bool forceRefresh = false,
            IProgress<PremiereLoadProgress>? progress = null, CalendarFilters? filters = null) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<PremiereLoadProgress> StreamPremieresAsync(
            DateOnly start, DateOnly end, bool forceRefresh = false, CalendarFilters? filters = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            int call;
            lock (Calls)
            {
                Calls.Add(start);
                call = Calls.Count;
            }

            if (GateForCall?.Invoke(call) is { } callGate)
            {
                await callGate.WaitAsync(cancellationToken);
            }

            var items = ItemsFor(start);
            if (EmitReady)
            {
                yield return new PremiereLoadProgress("Week cache", items.Count, items.Count, items, FromCache: true)
                {
                    IsCalendarReady = true,
                    IsStaleCache = ReadyIsStale,
                    IsEnriching = true
                };
                if (FinalGate is not null)
                {
                    await FinalGate.WaitAsync(cancellationToken);
                }
            }

            yield return new PremiereLoadProgress("Complete", items.Count, items.Count, items, IsFinal: true)
            {
                IsCalendarReady = true
            };
        }
    }
}
