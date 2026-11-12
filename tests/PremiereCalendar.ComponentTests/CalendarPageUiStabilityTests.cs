using System.Threading.Channels;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.ComponentTests;

// v1.2.19 UI work package: stable board, throttle flush, final sort, alias reconcile, filter dialog.
public sealed partial class CalendarPageTests
{
    private static readonly DateOnly UiDay = new(2026, 5, 4);
    private static readonly TimeSpan UiWait = TimeSpan.FromSeconds(4);

    private static PremiereItem UiMovie(string id, string title, string language = "en") =>
        CreateMovie(id, title, UiDay) with { OriginalLanguage = language };

    private static PremiereLoadProgress UiUpdate(string source, IReadOnlyList<PremiereItem> items, bool isFinal = false) =>
        new(source, items.Count, items.Count, items, IsFinal: isFinal);

    private static string[] CardTitles(IRenderedComponent<PremiereCalendar.Components.Pages.Calendar> component) =>
        component.FindAll("[data-testid='premiere-card'] h3").Select(node => node.TextContent.Trim()).ToArray();

    private static IReadOnlyList<IElement> FilterCheckboxes(
        IRenderedComponent<PremiereCalendar.Components.Pages.Calendar> component, string testId, string label) =>
        component.FindAll($"[data-testid='{testId}'] label.dropdown-check-option")
            .Where(option => option.TextContent.Trim() == label)
            .Select(option => option.QuerySelector("input")!)
            .ToArray();

    private static IElement FilterCheckbox(
        IRenderedComponent<PremiereCalendar.Components.Pages.Calendar> component, string testId, string label) =>
        Assert.Single(FilterCheckboxes(component, testId, label));

    private static string[] LanguageOptionLabels(IRenderedComponent<PremiereCalendar.Components.Pages.Calendar> component, string slug) =>
        component.FindAll($"[data-testid='{slug}-language-filter'] label.dropdown-check-option")
            .Select(option => option.TextContent.Trim())
            .ToArray();

    private int FocusSelectorCalls()
    {
        try
        {
            return JSInterop.Invocations["premiereCalendarWeek.focusSelector"].Count;
        }
        catch (KeyNotFoundException)
        {
            return 0;
        }
    }

    private IRenderedComponent<PremiereCalendar.Components.Pages.Calendar> RenderStreamed(
        ChannelPremiereService service, string url = "/movies?week=2026-05-04")
    {
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return Render<PremiereCalendar.Components.Pages.Calendar>();
    }

    [Fact]
    public async Task CalendarPage_StatusRowIsAlwaysRenderedBeforeAndAfterResults()
    {
        var service = new ChannelPremiereService();
        var component = RenderStreamed(service);

        // Reserved from the first render, before any result exists.
        Assert.Single(component.FindAll("[data-testid='calendar-status-row']"));
        var header = component.Find("[data-testid='calendar-command-bar']");
        Assert.NotNull(header.QuerySelector("[data-testid='calendar-status-row']"));

        await service.Updates.Writer.WriteAsync(UiUpdate("Source", [UiMovie("movie:1", "First")]));
        component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='premiere-card']")), UiWait);
        Assert.Single(component.FindAll("[data-testid='calendar-status-row']"));

        await service.Updates.Writer.WriteAsync(UiUpdate("Complete", [UiMovie("movie:1", "First")], isFinal: true));
        service.Updates.Writer.TryComplete();
        component.WaitForAssertion(() => Assert.Empty(component.FindAll("[data-testid='refreshing']")), UiWait);
        Assert.Single(component.FindAll("[data-testid='calendar-status-row']"));
    }

    [Fact]
    public async Task CalendarPage_KeepsPreviousFreshnessMarkedCheckingWhileNextWeekLoads()
    {
        _cacheMaintenance.DefaultMetadata = new CalendarCacheMetadata(
            DateTimeOffset.Parse("2026-05-10T08:15:00Z"), ItemCount: 12, SchemaVersion: 3,
            CalendarCacheCompleteness.Complete);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ScriptedWeekService
        {
            ItemsFor = start => [CreateMovie("movie:1", "Week item", start)],
            GateForCall = call => call == 2 ? gate.Task : null
        };
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/movies?week=2026-05-04");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        try
        {
            component.WaitForAssertion(() =>
            {
                Assert.Contains("Movies cache", component.Find("[data-testid='data-freshness-card']").TextContent);
                Assert.DoesNotContain("Checking cache", component.Find("[data-testid='data-freshness-card']").TextContent);
            }, UiWait);

            navigation.NavigateTo("/movies?week=2026-05-11");
            component.WaitForAssertion(() =>
            {
                Assert.Equal(2, service.Calls.Count);
                Assert.Empty(component.FindAll("[data-testid='premiere-card']"));
                var card = component.Find("[data-testid='data-freshness-card']");
                Assert.Contains("Movies cache", card.TextContent);
                // Neutral placeholder: the previous week's text is never shown as current.
                Assert.Contains("Checking cache", card.TextContent);
                Assert.DoesNotContain("12 items", card.TextContent);
                Assert.Single(component.FindAll("[data-testid='calendar-status-row']"));
            }, UiWait);

            gate.TrySetResult();
            component.WaitForAssertion(() =>
            {
                Assert.Single(component.FindAll("[data-testid='premiere-card']"));
                Assert.DoesNotContain("Checking cache", component.Find("[data-testid='data-freshness-card']").TextContent);
            }, UiWait);
        }
        finally
        {
            gate.TrySetResult();
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CalendarPage_DayHeaderOnSeriesPageDoesNotShowMovieCount()
    {
        var service = new ChannelPremiereService();
        var component = RenderStreamed(service, "/series?week=2026-05-04");
        var series = new PremiereItem
        {
            CanonicalId = "tv:77",
            Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series,
            TmdbId = 77,
            Title = "Only Series",
            PremiereDate = UiDay,
            OriginalLanguage = "en"
        };
        await service.Updates.Writer.WriteAsync(UiUpdate("Source", [series]));

        component.WaitForAssertion(() =>
        {
            var meta = component.Find(".day-header-meta").TextContent;
            Assert.Contains("1 series", meta);
            Assert.DoesNotContain("movie", meta);
        }, UiWait);
    }

    [Fact]
    public async Task CalendarPage_VerifiedRowReplacesItsUnverifiedTwinAtTheEndOfTheVerifiedCards()
    {
        var service = new ChannelPremiereService();
        var component = RenderStreamed(service);
        var first = UiMovie("movie:1", "First");
        var third = UiMovie("movie:3", "Third");
        var unverified = UiMovie("movie:2", "Provider row") with
        {
            VerificationState = PremiereVerificationState.Unverified,
            ExternalCandidateKey = "provider:key-2"
        };
        await service.Updates.Writer.WriteAsync(UiUpdate("Provider", [first, unverified]));
        component.WaitForAssertion(() => Assert.Equal(2, component.FindAll("[data-testid='premiere-card']").Count), UiWait);
        // A verified card arrives after the unverified row.
        await service.Updates.Writer.WriteAsync(UiUpdate("TMDb", [first, unverified, third]));
        component.WaitForAssertion(() => Assert.Equal(["First", "Third", "Provider row"], CardTitles(component)), UiWait);

        var verified = UiMovie("movie:22", "Verified row") with { ExternalCandidateKey = "provider:key-2" };
        await service.Updates.Writer.WriteAsync(UiUpdate("Resolved", [first, verified, third]));

        component.WaitForAssertion(() =>
        {
            // No duplicate, the unverified section is gone, and the verified cards before it did not move.
            Assert.Equal(["First", "Third", "Verified row"], CardTitles(component));
            Assert.Empty(component.FindAll("[data-testid='unverified-premieres']"));
            Assert.Equal("movie:22", component.FindAll("[data-testid='premiere-card']")[2].GetAttribute("data-canonical-id"));
        }, UiWait);
    }

    [Fact]
    public async Task CalendarPage_ShowsTheHeldBatchAndAppliesTheSortWhenTheStreamEndsWithoutAFinal()
    {
        var service = new ChannelPremiereService();
        var component = RenderStreamed(service, "/movies?week=2026-05-04&sort=title");
        var zeta = UiMovie("movie:1", "Zeta");
        var alpha = UiMovie("movie:2", "Alpha");
        await service.Updates.Writer.WriteAsync(UiUpdate("One", [zeta]));
        component.WaitForAssertion(() => Assert.Equal(["Zeta"], CardTitles(component)), UiWait);

        await service.Updates.Writer.WriteAsync(UiUpdate("Two", [zeta, alpha]));
        service.Updates.Writer.TryComplete();

        component.WaitForAssertion(() => Assert.Equal(["Alpha", "Zeta"], CardTitles(component)), UiWait);
    }

    [Fact]
    public async Task CalendarPage_FreshnessLeftCheckingByAPreviousWeekIsResolvedWhenTheBackgroundBudgetEnds()
    {
        Services.Configure<PremiereCalendar.Options.CalendarLoadOptions>(options =>
        {
            options.ForegroundLoadBudgetSeconds = 1;
            options.BackgroundCompletionBudgetSeconds = 1;
        });
        _cacheMaintenance.DefaultMetadata = new CalendarCacheMetadata(
            DateTimeOffset.Parse("2026-05-10T08:15:00Z"), ItemCount: 12, SchemaVersion: 3,
            CalendarCacheCompleteness.Complete);
        var service = new NeverFinishingSecondWeekService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/movies?week=2026-05-04");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() =>
            Assert.Contains("Movies cache", component.Find("[data-testid='data-freshness-card']").TextContent), UiWait);

        navigation.NavigateTo("/movies?week=2026-05-11");
        component.WaitForAssertion(() =>
            Assert.Contains("Checking cache", component.Find("[data-testid='data-freshness-card']").TextContent), UiWait);

        // The load hits its background deadline without a final update: the flag must not stay on.
        component.WaitForAssertion(() =>
            Assert.DoesNotContain(component.FindAll("[data-testid='data-freshness-card']"),
                card => card.TextContent.Contains("Checking cache")), TimeSpan.FromSeconds(8));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CalendarPage_ThrottledUpdateIsFlushedWithoutAnotherUpdate()
    {
        var service = new ChannelPremiereService();
        var component = RenderStreamed(service);

        await service.Updates.Writer.WriteAsync(UiUpdate("One", [UiMovie("movie:1", "First")]));
        component.WaitForAssertion(() => Assert.Equal(["First"], CardTitles(component)), UiWait);

        // Arrives inside the throttle window and the stream then goes quiet.
        await service.Updates.Writer.WriteAsync(UiUpdate("Two", [UiMovie("movie:1", "First"), UiMovie("movie:2", "Second")]));

        component.WaitForAssertion(() => Assert.Equal(["First", "Second"], CardTitles(component)), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task CalendarPage_AppliesChosenSortOnlyWhenTheLoadIsFinal()
    {
        var service = new ChannelPremiereService();
        var component = RenderStreamed(service, "/movies?week=2026-05-04&sort=title");
        var zeta = UiMovie("movie:1", "Zeta");
        var alpha = UiMovie("movie:2", "Alpha");

        await service.Updates.Writer.WriteAsync(UiUpdate("One", [zeta]));
        component.WaitForAssertion(() => Assert.Equal(["Zeta"], CardTitles(component)), UiWait);
        await service.Updates.Writer.WriteAsync(UiUpdate("Two", [zeta, alpha]));
        component.WaitForAssertion(() => Assert.Equal(["Zeta", "Alpha"], CardTitles(component)), UiWait);

        await service.Updates.Writer.WriteAsync(UiUpdate("Complete", [zeta, alpha], isFinal: true));
        service.Updates.Writer.TryComplete();

        component.WaitForAssertion(() => Assert.Equal(["Alpha", "Zeta"], CardTitles(component)), UiWait);
    }

    [Fact]
    public async Task CalendarPage_FinalDoesNotReorderStreamedCardsForTheDefaultDateSort()
    {
        var service = new ChannelPremiereService();
        var component = RenderStreamed(service);
        var zeta = UiMovie("movie:1", "Zeta");
        var alpha = UiMovie("movie:2", "Alpha");

        await service.Updates.Writer.WriteAsync(UiUpdate("One", [zeta]));
        component.WaitForAssertion(() => Assert.Equal(["Zeta"], CardTitles(component)), UiWait);
        await service.Updates.Writer.WriteAsync(UiUpdate("Two", [zeta, alpha]));
        component.WaitForAssertion(() => Assert.Equal(["Zeta", "Alpha"], CardTitles(component)), UiWait);

        await service.Updates.Writer.WriteAsync(UiUpdate("Complete", [zeta, alpha], isFinal: true));
        service.Updates.Writer.TryComplete();
        component.WaitForAssertion(() => Assert.Empty(component.FindAll("[data-testid='refreshing']")), UiWait);

        Assert.Equal(["Zeta", "Alpha"], CardTitles(component));
    }

    [Fact]
    public void CalendarPage_ProgressRendersDoNotRefocusTheHeading()
    {
        var service = new FakePremiereService
        {
            ReportPartialProgress = true,
            DelayAfterPartialProgress = TimeSpan.FromSeconds(3)
        };
        Services.AddSingleton<IPremiereService>(service);
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        ExpandQueryProgress(component);
        component.WaitForAssertion(() => Assert.NotEmpty(component.FindAll(".query-progress-entry")), UiWait);
        component.FindAll(".query-progress-entry")[0].Click();
        component.WaitForAssertion(() =>
            Assert.NotEmpty(component.FindAll("[data-testid='query-progress'] button[aria-label='Clear loaded-source filter']")), UiWait);
        component.Find("[data-testid='query-progress'] button[aria-label='Clear loaded-source filter']").Click();

        // Two explicit attempts from the handler plus one queued after the render.
        component.WaitForAssertion(() => Assert.True(FocusSelectorCalls() >= 3), UiWait);
        var afterFirstAttempt = FocusSelectorCalls();

        // Further renders while the load is still running must not queue another focus.
        component.Find("button[data-command-palette-toggle]").Click();
        component.Find("button[data-command-palette-toggle]").Click();
        Thread.Sleep(300);
        Assert.Equal(afterFirstAttempt, FocusSelectorCalls());
    }

    [Fact]
    public void CalendarPage_LiveRegionsAreLimitedToTheStageAnnouncement()
    {
        var service = new FakePremiereService { ReportPartialProgress = true };
        Services.AddSingleton<IPremiereService>(service);
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.NotEmpty(component.FindAll("[data-testid='premiere-card']")), UiWait);

        Assert.Null(component.FindAll("[data-testid='query-progress']").FirstOrDefault()?.GetAttribute("aria-live"));
        var live = component.FindAll("[aria-live]").Select(node => node.GetAttribute("data-testid")).ToArray();
        Assert.Contains("loading-announcement", live);
        Assert.DoesNotContain("refreshing", live);
        Assert.DoesNotContain("calendar-enrichment", live);
        Assert.DoesNotContain("query-progress", live);
    }

    [Fact]
    public async Task CalendarPage_FilterDialogKeepsItsOptionsWhileLoadingAndRefreshesWhenReopened()
    {
        var service = new ChannelPremiereService();
        var component = RenderStreamed(service);
        await service.Updates.Writer.WriteAsync(UiUpdate("One", [UiMovie("movie:1", "First")]));
        component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='premiere-card']")), UiWait);

        // Wait until the dialog is really open (and has captured one item) before the next update is written.
        await component.Find("button[title='Open filters']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        component.WaitForAssertion(() =>
            Assert.Contains("of 1 loaded cards match", component.Find("#filter-pane-summary").TextContent), UiWait);
        Assert.DoesNotContain("DE", LanguageOptionLabels(component, "movie"));

        await service.Updates.Writer.WriteAsync(UiUpdate("Two",
            [UiMovie("movie:1", "First"), UiMovie("movie:2", "Second"), UiMovie("movie:3", "German film", "de")]));
        component.WaitForAssertion(() => Assert.Equal(2, component.FindAll("[data-testid='premiere-card']").Count), UiWait);

        // The open dialog works from the items captured when it opened.
        Assert.DoesNotContain("DE", LanguageOptionLabels(component, "movie"));
        Assert.Contains("of 1 loaded cards match", component.Find("#filter-pane-summary").TextContent);

        await component.Find("button[title='Close filters']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        component.WaitForAssertion(() => Assert.Empty(component.FindAll("[data-testid='filter-pane']")), UiWait);
        await component.Find("button[title='Open filters']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        component.WaitForAssertion(() =>
            Assert.Contains("of 3 loaded cards match", component.Find("#filter-pane-summary").TextContent), UiWait);
        Assert.Contains("DE", LanguageOptionLabels(component, "movie"));
    }

    [Fact]
    public async Task CalendarPage_FilterDialogNormalisesItemLanguagesBeforeMergingThem()
    {
        var service = new ChannelPremiereService();
        var component = RenderStreamed(service);
        await service.Updates.Writer.WriteAsync(UiUpdate("One",
            [UiMovie("movie:1", "Flemish", "nl-BE"), UiMovie("movie:2", "Vlaams", "vls"), UiMovie("movie:3", "English")]));
        component.WaitForAssertion(() => Assert.NotEmpty(component.FindAll("[data-testid='premiere-card']")), UiWait);

        // The click can be queued behind load work, so wait for the opened dialog instead of asserting at once.
        await component.Find("button[title='Open filters']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        component.WaitForAssertion(() =>
            Assert.Contains("of 3 loaded cards match", component.Find("#filter-pane-summary").TextContent), UiWait);

        component.WaitForAssertion(() =>
        {
            var labels = LanguageOptionLabels(component, "movie");
            Assert.Single(labels, label => label == "Dutch (NL)");
            Assert.DoesNotContain("NL-BE", labels);
            Assert.DoesNotContain("VLS", labels);
        }, UiWait);
    }

    [Fact]
    public void CalendarPage_FilterCatalogArrivingWhileTheDialogIsOpenShowsItsOptions()
    {
        var catalog = new DeferredFilterCatalogService();
        Services.AddSingleton<IFilterCatalogService>(catalog);
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/movies?week=2026-05-04");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='premiere-card']")), UiWait);

        component.Find("button[title='Open filters']").Click();
        Assert.Empty(FilterCheckboxes(component, "movie-language-filter", "German (DE)"));

        catalog.Gate.SetResult(new FilterCatalog { Languages = [new("de", "German (DE)")] });

        component.WaitForAssertion(() =>
            Assert.Single(FilterCheckboxes(component, "movie-language-filter", "German (DE)")), UiWait);
    }

    [Fact]
    public void CalendarPage_FailedFilterCatalogLoadIsRetriedOnTheNextOpen()
    {
        var catalog = new DeferredFilterCatalogService { FailFirstCall = true };
        Services.AddSingleton<IFilterCatalogService>(catalog);
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/movies?week=2026-05-04");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() =>
        {
            Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Equal(1, catalog.Calls);
        }, UiWait);

        component.Find("button[title='Open filters']").Click();
        catalog.Gate.SetResult(new FilterCatalog { Languages = [new("de", "German (DE)")] });

        component.WaitForAssertion(() =>
        {
            Assert.Equal(2, catalog.Calls);
            Assert.Single(FilterCheckboxes(component, "movie-language-filter", "German (DE)"));
        }, UiWait);
    }

    [Fact]
    public void CalendarPage_FilterDialogSaveButtonRestoresFocusToTheOpener()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls), UiWait);

        component.Find("button[title='Open filters']").Click();

        Assert.True(component.Find("button[title='Save filters']").HasAttribute("data-filter-save"));
        Assert.All(component.FindAll(".filter-pane input[type='checkbox']"),
            checkbox => Assert.False(checkbox.HasAttribute("aria-label")));
        // Each option list names its group, so identical option names stay distinguishable.
        Assert.Contains(component.FindAll(".filter-pane [role='group']"),
            group => group.GetAttribute("aria-label") is "Series filters languages" or "Movie filters languages");
    }

    [Fact]
    public void CalendarPage_ErrorToastStaysUntilDismissed()
    {
        var service = new FakePremiereService();
        Services.AddSingleton<IPremiereService>(service);
        JSInterop.SetupVoid("premiereCalendarActions.copyText", _ => true)
            .SetException(new Microsoft.JSInterop.JSException("clipboard blocked"));
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(service.Calls), UiWait);

        component.Find("button[data-command-palette-toggle]").Click();
        component.FindAll(".command-palette-item").Single(item => item.TextContent.Contains("Copy view link")).Click();

        var toast = component.WaitForElement(".toast-message.error", UiWait);
        Assert.Contains("toast-alerts", toast.ParentElement!.ClassName);
        Assert.Equal("alert", component.Find(".toast-alerts").GetAttribute("role"));

        // The same error again replaces its twin instead of stacking.
        if (component.FindAll(".command-palette-item").Count == 0)
        {
            component.Find("button[data-command-palette-toggle]").Click();
        }
        component.FindAll(".command-palette-item").Single(item => item.TextContent.Contains("Copy view link")).Click();
        Assert.Single(component.FindAll(".toast-message.error"));

        Thread.Sleep(TimeSpan.FromSeconds(3.5));
        Assert.Single(component.FindAll(".toast-message.error"));

        component.Find(".toast-message.error .toast-dismiss").Click();
        Assert.Empty(component.FindAll(".toast-message.error"));
    }

    private sealed class NeverFinishingSecondWeekService : IPremiereService
    {
        private int _calls;

        public Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(
            DateOnly start, DateOnly end, CancellationToken cancellationToken, bool forceRefresh = false,
            IProgress<PremiereLoadProgress>? progress = null, CalendarFilters? filters = null) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<PremiereLoadProgress> StreamPremieresAsync(
            DateOnly start, DateOnly end, bool forceRefresh = false, CalendarFilters? filters = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var items = new[] { CreateMovie("movie:1", "Week item", start) };
            if (Interlocked.Increment(ref _calls) == 1)
            {
                yield return new PremiereLoadProgress("Complete", 1, 1, items, IsFinal: true);
                yield break;
            }

            yield return new PremiereLoadProgress("Partial", 1, 1, items);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class ChannelPremiereService : IPremiereService
    {
        public Channel<PremiereLoadProgress> Updates { get; } = Channel.CreateUnbounded<PremiereLoadProgress>();

        public Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(
            DateOnly start, DateOnly end, CancellationToken cancellationToken, bool forceRefresh = false,
            IProgress<PremiereLoadProgress>? progress = null, CalendarFilters? filters = null) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<PremiereLoadProgress> StreamPremieresAsync(
            DateOnly start, DateOnly end, bool forceRefresh = false, CalendarFilters? filters = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in Updates.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update;
            }
        }
    }

    private sealed class DeferredFilterCatalogService : IFilterCatalogService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool FailFirstCall { get; init; }

        public TaskCompletionSource<FilterCatalog> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<FilterCatalog> GetCatalogAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            var call = Interlocked.Increment(ref _calls);
            if (FailFirstCall && call == 1)
            {
                throw new InvalidOperationException("catalog unavailable");
            }

            return Gate.Task;
        }
    }
}
