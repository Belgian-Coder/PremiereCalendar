using System.Globalization;
using Bunit;
using PremiereCalendar.Components.Shared;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.ComponentTests;

// v1.2.19: card height stays stable, first-viewport posters, alias keys, default day, toasts, static assets.
public sealed class UiStabilityComponentTests : BunitContext
{
    private static readonly DateOnly Monday = new(2026, 5, 4);

    public UiStabilityComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static PremiereItem Movie(string id, string title, DateOnly? date = null) => new()
    {
        CanonicalId = id,
        Type = PremiereItemType.MovieFirstRelease,
        MediaType = PremiereMediaType.Movie,
        TmdbId = int.Parse(id.AsSpan(id.LastIndexOf(':') + 1), CultureInfo.InvariantCulture),
        Title = title,
        PremiereDate = date ?? Monday,
        PosterUrl = $"https://image.tmdb.org/t/p/w185/{id.Replace(':', '-')}.jpg",
        OriginalLanguage = "en"
    };

    [Fact]
    public void PremiereCard_ReservesTheScoresRowWhileDetailsArePending()
    {
        var item = Movie("movie:1", "Pending");

        var pending = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, item)
            .Add(x => x.DetailsPending, true));
        Assert.Equal("Ratings pending", pending.Find("[data-testid='scores-placeholder']").TextContent.Trim());

        var ready = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, item with { TmdbScore = 7.5, TmdbVoteCount = 1234 })
            .Add(x => x.DetailsPending, false));
        Assert.Empty(ready.FindAll("[data-testid='scores-placeholder']"));
        // The thousands separator follows the current culture.
        Assert.Contains($"({1234.ToString("N0", CultureInfo.CurrentCulture)})", ready.Find(".primary-score").TextContent);
        Assert.Single(ready.FindAll(".primary-score"));
    }

    [Fact]
    public void PremiereCard_ClampsTheOverviewAndKeepsTheFullTextInDetails()
    {
        var overview = string.Join(' ', Enumerable.Repeat("A long overview sentence.", 30));
        var component = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, Movie("movie:2", "Long") with { Overview = overview }));

        Assert.Equal(overview, component.Find("[data-testid='card-overview']").TextContent.Trim());
        Assert.Empty(component.FindAll("[data-testid='card-overview-full']"));

        component.Find(".card-details summary").Click();

        Assert.Equal(overview, component.Find("[data-testid='card-overview-full']").TextContent.Trim());
    }

    [Fact]
    public void PremiereCard_EagerPosterRendersTheRealSourceWithHighPriority()
    {
        var item = Movie("movie:3", "Eager");

        var eager = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, item)
            .Add(x => x.PosterEager, true));
        var image = eager.Find(".poster-frame img");
        Assert.StartsWith("/cached-image?url=", image.GetAttribute("src"));
        Assert.Equal("high", image.GetAttribute("fetchpriority"));
        Assert.Equal("async", image.GetAttribute("decoding"));
        Assert.Equal("104", image.GetAttribute("width"));
        Assert.Equal("156", image.GetAttribute("height"));
        Assert.False(image.HasAttribute("data-lazy-image"));
        Assert.False(image.HasAttribute("data-lazy-src"));
        Assert.Null(image.GetAttribute("loading"));

        var lazy = Render<PremiereCard>(parameters => parameters.Add(x => x.Item, item));
        Assert.True(lazy.Find(".poster-frame img").HasAttribute("data-lazy-image"));
        Assert.Equal("lazy", lazy.Find(".poster-frame img").GetAttribute("loading"));
    }

    [Fact]
    public void CalendarDay_OnlyTheFirstFourCardsLoadPostersEagerly()
    {
        var items = Enumerable.Range(1, 6).Select(index => Movie($"movie:{index}", $"Movie {index}")).ToArray();

        var component = Render<CalendarDay>(parameters => parameters
            .Add(x => x.Day, Monday)
            .Add(x => x.Items, items));

        var images = component.FindAll("[data-testid='premiere-card'] .poster-frame img");
        Assert.Equal(6, images.Count);
        Assert.Equal(4, images.Count(image => image.GetAttribute("fetchpriority") == "high" && !image.HasAttribute("data-lazy-image")));
        Assert.Equal(2, images.Count(image => image.HasAttribute("data-lazy-image")));
        Assert.All(images.Take(4), image => Assert.False(image.HasAttribute("data-lazy-image")));
    }

    [Fact]
    public void CalendarDay_KeepsTheCardMountedWhenAnItemChangesIdentityButKeepsItsAlias()
    {
        var before = Movie("movie:10", "Provider row") with { ExternalCandidateKey = "provider:key-10" };
        var after = Movie("movie:100", "Verified row") with { ExternalCandidateKey = "provider:key-10" };
        var component = Render<CalendarDay>(parameters => parameters
            .Add(x => x.Day, Monday)
            .Add(x => x.Items, new[] { before }));
        var mountedCard = component.FindComponent<PremiereCard>().Instance;

        component.Render(parameters => parameters
            .Add(x => x.Day, Monday)
            .Add(x => x.Items, new[] { after }));

        var updated = Assert.Single(component.FindAll("[data-testid='premiere-card']"));
        // The same component instance proves the card was updated in place, not remounted.
        Assert.Same(mountedCard, component.FindComponent<PremiereCard>().Instance);
        Assert.Equal("movie:100", updated.GetAttribute("data-canonical-id"));
        Assert.Contains("Verified row", updated.TextContent);
    }

    [Fact]
    public void CalendarDay_HidesTheCountOfTheTypeADedicatedPageDoesNotShow()
    {
        var series = new PremiereItem
        {
            CanonicalId = "tv:5",
            Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series,
            TmdbId = 5,
            Title = "A series",
            PremiereDate = Monday
        };

        var onSeriesPage = Render<CalendarDay>(parameters => parameters
            .Add(x => x.Day, Monday)
            .Add(x => x.WindowScope, "Series")
            .Add(x => x.Items, new[] { series }));
        Assert.Equal("1 series", string.Concat(onSeriesPage.Find(".day-header-meta").TextContent.Trim()));

        var onAllPage = Render<CalendarDay>(parameters => parameters
            .Add(x => x.Day, Monday)
            .Add(x => x.WindowScope, "All")
            .Add(x => x.Items, new[] { series }));
        Assert.Contains("0 movies", onAllPage.Find(".day-header-meta").TextContent);
    }

    [Fact]
    public void CalendarWeek_BareUrlForTheCurrentWeekSelectsToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var weekStart = CalendarFilters.StartOfWeek(today);

        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, weekStart)
            .Add(x => x.Items, Array.Empty<PremiereItem>()));

        var selected = Assert.Single(component.FindAll("[data-day-button][aria-selected='true']"));
        Assert.Equal($"premiere-day-{today:yyyyMMdd}", selected.GetAttribute("data-day-target"));
    }

    [Fact]
    public void CalendarWeek_BareUrlForAnotherWeekSelectsTheFirstDay()
    {
        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, Monday)
            .Add(x => x.Items, Array.Empty<PremiereItem>()));

        var selected = Assert.Single(component.FindAll("[data-day-button][aria-selected='true']"));
        Assert.Equal("premiere-day-20260504", selected.GetAttribute("data-day-target"));
    }

    [Fact]
    public void CalendarWeek_DoesNotMoveTheSelectionWhileALoadIsUpdating()
    {
        var wednesday = Monday.AddDays(2);
        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, Monday)
            .Add(x => x.IsUpdating, true)
            .Add(x => x.Items, Array.Empty<PremiereItem>()));

        component.Render(parameters => parameters
            .Add(x => x.WeekStart, Monday)
            .Add(x => x.IsUpdating, true)
            .Add(x => x.Items, new[] { Movie("movie:20", "Midweek", wednesday) }));

        Assert.Equal(
            "premiere-day-20260504",
            Assert.Single(component.FindAll("[data-day-button][aria-selected='true']")).GetAttribute("data-day-target"));
    }

    [Fact]
    public void ToastStack_HasNoLiveRegionHostAndErrorToastsCanBeDismissed()
    {
        var dismissed = new List<Guid>();
        var error = new ToastMessage(Guid.NewGuid(), ToastKind.Error, "Failed", "Something broke");
        var success = new ToastMessage(Guid.NewGuid(), ToastKind.Success, "Done", "All good");

        var component = Render<ToastStack>(parameters => parameters
            .Add(x => x.Messages, new[] { error, success })
            .Add(x => x.OnDismiss, dismissed.Add));

        // Persistent containers: polite for info/success, alert for errors; the toasts themselves have no role.
        Assert.Null(component.Find(".toast-host").GetAttribute("aria-live"));
        Assert.Equal("polite", component.Find(".toast-polite").GetAttribute("aria-live"));
        Assert.Equal("alert", component.Find(".toast-alerts").GetAttribute("role"));
        Assert.NotNull(component.Find(".toast-alerts .toast-message.error"));
        Assert.NotNull(component.Find(".toast-polite .toast-message.success"));
        Assert.Empty(component.FindAll(".toast-message[role]"));
        Assert.Empty(component.FindAll(".toast-message.success .toast-dismiss"));

        component.Find(".toast-message.error .toast-dismiss").Click();

        Assert.Equal([error.Id], dismissed);
    }

    [Fact]
    public void ToastStack_KeepsBothContainersWhenEmpty()
    {
        var component = Render<ToastStack>();

        Assert.Equal("polite", component.Find(".toast-polite").GetAttribute("aria-live"));
        Assert.Equal("alert", component.Find(".toast-alerts").GetAttribute("role"));
    }

    [Fact]
    public void PremiereCard_KeepsItsPosterModeWhenItCrossesTheEagerBoundary()
    {
        var item = Movie("movie:30", "Stays eager");
        var component = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, item)
            .Add(x => x.PosterEager, true));

        component.Render(parameters => parameters
            .Add(x => x.Item, item with { Title = "Stays eager" })
            .Add(x => x.PosterEager, false));

        var image = component.Find(".poster-frame img");
        Assert.Equal("high", image.GetAttribute("fetchpriority"));
        Assert.False(image.HasAttribute("data-lazy-image"));
    }

    [Fact]
    public void PremiereCard_KeepsTheScoresOnOneFixedLineWithTheFullTextInItsTitle()
    {
        var component = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, Movie("movie:31", "Scored") with
            {
                ImdbScore = 7.1, ImdbVoteCount = 1000, TmdbScore = 7.5, TmdbVoteCount = 20,
                RottenTomatoesScore = 80, RottenTomatoesAudienceScore = 90
            }));

        var row = component.Find(".primary-score");
        Assert.Contains("RT critics 80%", row.GetAttribute("title"));
        Assert.Contains("IMDb 7.1 / 10", row.GetAttribute("title"));
    }

    [Fact]
    public void StaticAssets_ApplyDensityBeforeFirstPaintAndAvoidFocusStealing()
    {
        var app = ReadRepoFile("PremiereCalendar/Components/App.razor");
        var densityScript = app.IndexOf("premiere-calendar:density", StringComparison.Ordinal);
        Assert.True(densityScript >= 0 && densityScript < app.IndexOf("<ResourcePreloader", StringComparison.Ordinal));
        Assert.Contains("document.documentElement.dataset.density", app);

        var weekScroll = ReadRepoFile("PremiereCalendar/wwwroot/week-scroll.js");
        Assert.DoesNotContain("setTimeout(focus, 2500)", weekScroll);
        Assert.Contains("pointerdown", weekScroll);
        Assert.Contains("[data-filter-save]", weekScroll);

        var palette = ReadRepoFile("PremiereCalendar/wwwroot/command-palette.js");
        Assert.DoesNotContain("setTimeout(focus, 500)", palette);
        Assert.Contains("pointerdown", palette);

        var viewSync = ReadRepoFile("PremiereCalendar/wwwroot/view-sync-storage.js");
        Assert.DoesNotContain("function lastSeenStorage", viewSync);
        Assert.Contains("const lastSeenStorage", viewSync);
    }

    [Fact]
    public void DarkThemeOverviewKeepsAReadableContrast()
    {
        var css = ReadRepoFile("PremiereCalendar/wwwroot/app.css");
        var start = css.IndexOf("html[data-theme=\"dark\"] .card-description {", StringComparison.Ordinal);
        Assert.True(start >= 0, "dark card-description rule missing");
        var block = css[start..css.IndexOf('}', start)];
        Assert.Contains("rgba(255, 255, 255, 0.06)", block);

        // Card surface in dark mode is #1f2b36; the overview sits on a 6% white overlay.
        var surface = new[] { 0x1f, 0x2b, 0x36 };
        var background = surface.Select(channel => channel + (255 - channel) * 0.06).ToArray();
        var text = new[] { 0xdb, 0xe5, 0xea };
        Assert.True(Contrast(text.Select(Convert.ToDouble).ToArray(), background) >= 4.5);
    }

    private static double Contrast(double[] foreground, double[] background)
    {
        var a = Luminance(foreground);
        var b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(double[] rgb)
    {
        var linear = rgb.Select(channel =>
        {
            var value = channel / 255;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }).ToArray();
        return 0.2126 * linear[0] + 0.7152 * linear[1] + 0.0722 * linear[2];
    }

    private static string ReadRepoFile(string relativePath, [System.Runtime.CompilerServices.CallerFilePath] string testFile = "")
    {
        // Build output lives outside the repository; start from this source file instead.
        var directory = new DirectoryInfo(Path.GetDirectoryName(testFile)!);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{relativePath} was not found above {testFile}.");
    }
}
