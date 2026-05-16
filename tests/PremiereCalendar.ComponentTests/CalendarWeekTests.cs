using Bunit;
using Microsoft.AspNetCore.Components.Web;
using PremiereCalendar.Components.Shared;
using PremiereCalendar.Models;

namespace PremiereCalendar.ComponentTests;

public sealed class CalendarWeekTests : BunitContext
{
    public CalendarWeekTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void CalendarWeek_RendersSevenDayButtonsAndOneSelectedDay()
    {
        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, Array.Empty<PremiereItem>())
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        Assert.Single(component.FindAll("[data-testid='calendar-day']"));
        Assert.Equal(7, component.FindAll(".day-jump-link").Count);
        Assert.Equal(7, component.FindAll("[data-day-button]").Count);
        Assert.Equal(7, component.FindAll(".day-jump-compact-date").Count);
        Assert.NotEmpty(component.FindAll(".mobile-day-jump-strip"));
        Assert.Single(component.FindAll("button[title='Open filters']"));
        Assert.Empty(component.FindAll("[data-testid='week-scroll-control']"));
        Assert.NotEmpty(component.FindAll(".calendar-grid"));
        Assert.NotEmpty(component.FindAll("button[data-day-target='premiere-day-20260504']"));
        Assert.Equal(
            "Monday, 04 May 2026, no premieres",
            component.Find("button[data-day-target='premiere-day-20260504']").GetAttribute("aria-label"));
        Assert.NotEmpty(component.FindAll("#premiere-day-20260504"));
        Assert.Equal("tabpanel", component.Find("[data-testid='calendar-day']").GetAttribute("role"));
        Assert.Equal("premiere-day-tab-20260504", component.Find("[data-testid='calendar-day']").GetAttribute("aria-labelledby"));
        Assert.Single(component.FindAll(".empty-day"));
        Assert.Contains("No premieres", component.Markup);
    }

    [Fact]
    public void CalendarWeek_GroupsPremieresIntoMatchingDay()
    {
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "tv:10",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 10,
                Title = "Monday Launch",
                PremiereDate = new DateOnly(2026, 5, 4),
                TmdbScore = 7.5,
                PosterUrl = "https://image.tmdb.org/t/p/w185/poster.jpg"
            }
        };

        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb)
            .Add(x => x.ImageCacheVersion, "abc123"));

        Assert.Single(component.FindAll("[data-testid='premiere-card']"));
        Assert.Contains("Monday Launch", component.Markup);
        Assert.Contains("1 series", component.Markup);
        Assert.Equal(
            "Monday, 04 May 2026, 1 series",
            component.Find("button[data-day-target='premiere-day-20260504']").GetAttribute("aria-label"));
        Assert.Contains("w=185&amp;v=abc123", component.Markup);
        Assert.DoesNotContain("ImageCacheVersion", component.Markup);
        Assert.Empty(component.FindAll(".empty-day"));
    }

    [Fact]
    public void CalendarWeek_ClickingDaySwitchesSelectedDay()
    {
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "tv:9",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 9,
                Title = "Monday Launch",
                PremiereDate = new DateOnly(2026, 5, 4),
                TmdbScore = 7.1
            },
            new PremiereItem
            {
                CanonicalId = "tv:10",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 10,
                Title = "Tuesday Launch",
                PremiereDate = new DateOnly(2026, 5, 5),
                TmdbScore = 7.5
            }
        };

        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        Assert.Contains("Monday Launch", component.Markup);
        Assert.DoesNotContain("Tuesday Launch", component.Markup);
        Assert.Contains("Monday", component.Markup);
        Assert.Single(component.FindAll("[data-testid='calendar-day']"));

        component.Find("button[data-day-target='premiere-day-20260505']").Click();

        Assert.Contains("Tuesday Launch", component.Markup);
        Assert.Contains("Tuesday", component.Markup);
        Assert.Single(component.FindAll("[data-testid='calendar-day']"));
        Assert.Contains("active", component.Find("button[data-day-target='premiere-day-20260505']").ClassName);
    }

    [Fact]
    public void CalendarWeek_ClearsExternalSelectedDayWhenParameterReturnsToNull()
    {
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "tv:9",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 9,
                Title = "Monday Launch",
                PremiereDate = new DateOnly(2026, 5, 4),
                TmdbScore = 7.1
            },
            new PremiereItem
            {
                CanonicalId = "tv:10",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 10,
                Title = "Wednesday Launch",
                PremiereDate = new DateOnly(2026, 5, 6),
                TmdbScore = 7.5
            }
        };

        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb)
            .Add(x => x.SelectedDay, new DateOnly(2026, 5, 6)));

        Assert.Contains("active", component.Find("button[data-day-target='premiere-day-20260506']").ClassName);
        Assert.Contains("Wednesday Launch", component.Markup);

        component.Render(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb)
            .Add(x => x.SelectedDay, (DateOnly?)null));

        Assert.Contains("active", component.Find("button[data-day-target='premiere-day-20260504']").ClassName);
        Assert.DoesNotContain("active", component.Find("button[data-day-target='premiere-day-20260506']").ClassName);
        Assert.Contains("Monday Launch", component.Markup);
        Assert.DoesNotContain("Wednesday Launch", component.Markup);
    }

    [Fact]
    public void CalendarWeek_PreservesInternalSelectionWhenNoExternalSelectionWasProvided()
    {
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "tv:9",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 9,
                Title = "Monday Launch",
                PremiereDate = new DateOnly(2026, 5, 4),
                TmdbScore = 7.1
            },
            new PremiereItem
            {
                CanonicalId = "tv:10",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 10,
                Title = "Tuesday Launch",
                PremiereDate = new DateOnly(2026, 5, 5),
                TmdbScore = 7.5
            }
        };

        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));
        component.Find("button[data-day-target='premiere-day-20260505']").Click();

        component.Render(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb)
            .Add(x => x.ImageCacheVersion, "fresh"));

        Assert.Contains("active", component.Find("button[data-day-target='premiere-day-20260505']").ClassName);
        Assert.Contains("Tuesday Launch", component.Markup);
    }

    [Fact]
    public void CalendarWeek_AppliesSortParametersBeforeGrouping()
    {
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "movie:2",
                Type = PremiereItemType.MovieFirstRelease,
                MediaType = PremiereMediaType.Movie,
                TmdbId = 2,
                Title = "Beta",
                PremiereDate = new DateOnly(2026, 5, 4),
                TmdbScore = 6
            },
            new PremiereItem
            {
                CanonicalId = "movie:1",
                Type = PremiereItemType.MovieFirstRelease,
                MediaType = PremiereMediaType.Movie,
                TmdbId = 1,
                Title = "Alpha",
                PremiereDate = new DateOnly(2026, 5, 4),
                TmdbScore = 9
            }
        };

        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.SortMode, PremiereSortMode.Title)
            .Add(x => x.SortDirection, SortDirection.Ascending)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        Assert.True(
            component.Markup.IndexOf("Alpha", StringComparison.Ordinal) <
            component.Markup.IndexOf("Beta", StringComparison.Ordinal));

        component.Render(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.SortMode, PremiereSortMode.Score)
            .Add(x => x.SortDirection, SortDirection.Ascending)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        Assert.True(
            component.Markup.IndexOf("Beta", StringComparison.Ordinal) <
            component.Markup.IndexOf("Alpha", StringComparison.Ordinal));
    }

    [Fact]
    public void CalendarWeek_UpdatesSelectedDayAnimationKeyWhenDayChanges()
    {
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "tv:9",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 9,
                Title = "Monday Launch",
                PremiereDate = new DateOnly(2026, 5, 4),
                TmdbScore = 7.1
            },
            new PremiereItem
            {
                CanonicalId = "tv:10",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 10,
                Title = "Tuesday Launch",
                PremiereDate = new DateOnly(2026, 5, 5),
                TmdbScore = 7.5
            }
        };

        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        Assert.Equal("20260504", component.Find("[data-testid='calendar-day']").GetAttribute("data-day-animation-key"));

        component.Find("button[data-day-target='premiere-day-20260505']").Click();

        Assert.Equal("20260505", component.Find("[data-testid='calendar-day']").GetAttribute("data-day-animation-key"));
    }

    [Fact]
    public async Task CalendarWeek_ScrollAdjacentDayMovesWithinWeek()
    {
        DateOnly? selectedDay = null;
        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, Array.Empty<PremiereItem>())
            .Add(x => x.ScoreSource, ScoreSource.Tmdb)
            .Add(x => x.OnSelectedDayChanged, day => selectedDay = day));

        await component.InvokeAsync(() => component.Instance.SelectAdjacentDayByScrollAsync(1));

        Assert.Equal(new DateOnly(2026, 5, 5), selectedDay);
        Assert.Contains("active", component.Find("button[data-day-target='premiere-day-20260505']").ClassName);
        Assert.DoesNotContain("active", component.Find("button[data-day-target='premiere-day-20260504']").ClassName);
        var focusInvocation = JSInterop.Invocations["premiereCalendarWeek.focusDayButton"].Single();
        Assert.Equal("premiere-day-20260505", focusInvocation.Arguments[0]);
    }

    [Fact]
    public async Task CalendarWeek_ScrollAdjacentDayRequestsNextCalendarDayAcrossWeekBoundary()
    {
        DateOnly? requestedDay = null;
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "tv:10",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 10,
                Title = "Sunday Launch",
                PremiereDate = new DateOnly(2026, 5, 10)
            }
        };
        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb)
            .Add(x => x.SelectedDay, new DateOnly(2026, 5, 10))
            .Add(x => x.OnAdjacentCalendarDayRequested, day => requestedDay = day));

        await component.InvokeAsync(() => component.Instance.SelectAdjacentDayByScrollAsync(1));

        Assert.Equal(new DateOnly(2026, 5, 11), requestedDay);
        Assert.Contains("active", component.Find("button[data-day-target='premiere-day-20260510']").ClassName);
    }

    [Fact]
    public void CalendarWeek_ArrowRightRequestsNextCalendarDayAcrossWeekBoundary()
    {
        DateOnly? requestedDay = null;
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "tv:10",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 10,
                Title = "Sunday Launch",
                PremiereDate = new DateOnly(2026, 5, 10)
            }
        };
        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb)
            .Add(x => x.SelectedDay, new DateOnly(2026, 5, 10))
            .Add(x => x.OnAdjacentCalendarDayRequested, day => requestedDay = day));

        component.Find("button[data-day-target='premiere-day-20260510']")
            .KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });

        Assert.Equal(new DateOnly(2026, 5, 11), requestedDay);
    }

    [Fact]
    public void CalendarWeek_ArrowLeftRequestsPreviousCalendarDayAcrossWeekBoundary()
    {
        DateOnly? requestedDay = null;
        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, Array.Empty<PremiereItem>())
            .Add(x => x.ScoreSource, ScoreSource.Tmdb)
            .Add(x => x.SelectedDay, new DateOnly(2026, 5, 4))
            .Add(x => x.OnAdjacentCalendarDayRequested, day => requestedDay = day));

        component.Find("button[data-day-target='premiere-day-20260504']")
            .KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });

        Assert.Equal(new DateOnly(2026, 5, 3), requestedDay);
    }

    [Fact]
    public void CalendarWeek_UsesVirtualizedRowsForDenseDays()
    {
        var items = Enumerable.Range(1, 45)
            .Select(index => new PremiereItem
            {
                CanonicalId = $"tv:{index}",
                Type = PremiereItemType.SeriesEpisode,
                MediaType = PremiereMediaType.Series,
                TmdbId = index,
                Title = $"Episode {index}",
                PremiereDate = new DateOnly(2026, 5, 4),
                TmdbScore = 7
            })
            .ToArray();

        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        Assert.Single(component.FindAll("[data-testid='virtualized-day']"));
        Assert.Contains("Showing all 45 with virtual scrolling", component.Markup);
        Assert.Contains("visually-hidden", component.Find(".day-virtualized-list .day-load-controls").ClassName);
        Assert.Empty(component.FindAll("[data-day-load-more]"));
    }

    [Fact]
    public void CalendarWeek_VirtualizedRowsPackTwoCardsForDesktopDensity()
    {
        var items = Enumerable.Range(1, 45)
            .Select(index => new PremiereItem
            {
                CanonicalId = $"tv:{index}",
                Type = PremiereItemType.SeriesEpisode,
                MediaType = PremiereMediaType.Series,
                TmdbId = index,
                Title = $"Episode {index}",
                PremiereDate = new DateOnly(2026, 5, 4),
                TmdbScore = 7
            })
            .ToArray();

        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        var rows = component.FindAll(".day-card-row");
        Assert.NotEmpty(rows);
        Assert.Equal(2, rows[0].QuerySelectorAll("[data-testid='premiere-card']").Length);
        Assert.Equal(2, rows[1].QuerySelectorAll("[data-testid='premiere-card']").Length);
        Assert.All(rows, row => Assert.InRange(row.QuerySelectorAll("[data-testid='premiere-card']").Length, 1, 2));
    }

    [Fact]
    public void CalendarDay_ShowAllLoadsRemainingSmallDayItemsAtOnce()
    {
        var items = Enumerable.Range(1, 12)
            .Select(index => new PremiereItem
            {
                CanonicalId = $"movie:{index}",
                Type = PremiereItemType.MovieFirstRelease,
                MediaType = PremiereMediaType.Movie,
                TmdbId = index,
                Title = $"Movie {index}",
                PremiereDate = new DateOnly(2026, 5, 4),
                TmdbScore = 7
            })
            .ToArray();

        var component = Render<CalendarDay>(parameters => parameters
            .Add(x => x.Day, new DateOnly(2026, 5, 4))
            .Add(x => x.DayId, "premiere-day-20260504")
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        Assert.Equal(10, component.FindAll("[data-testid='premiere-card']").Count);

        component.FindAll(".day-load-controls button").Last().Click();

        Assert.Equal(12, component.FindAll("[data-testid='premiere-card']").Count);
        Assert.Empty(component.FindAll("[data-day-autoload-sentinel]"));
    }

    [Fact]
    public void CalendarWeek_UsesTenCardBatchesForSmallerDays()
    {
        var items = Enumerable.Range(1, 12)
            .Select(index => new PremiereItem
            {
                CanonicalId = $"movie:{index}",
                Type = PremiereItemType.MovieFirstRelease,
                MediaType = PremiereMediaType.Movie,
                TmdbId = index,
                Title = $"Movie {index}",
                PremiereDate = new DateOnly(2026, 5, 4),
                TmdbScore = 7
            })
            .ToArray();

        var component = Render<CalendarWeek>(parameters => parameters
            .Add(x => x.WeekStart, new DateOnly(2026, 5, 4))
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        Assert.Equal(10, component.FindAll("[data-testid='premiere-card']").Count);
        Assert.Single(component.FindAll("[data-day-load-more]"));
        Assert.Single(component.FindAll("[data-day-autoload-sentinel]"));
        Assert.Empty(component.FindAll("[data-testid='virtualized-day']"));

        component.Find("[data-day-load-more]").Click();

        Assert.Equal(12, component.FindAll("[data-testid='premiere-card']").Count);
        Assert.DoesNotContain("Collapse", component.Markup);
        Assert.Empty(component.FindAll("[data-day-load-more]"));
    }

    [Fact]
    public void CalendarDay_RendersHiddenAdjacentDayScrollPrompts()
    {
        var component = Render<CalendarDay>(parameters => parameters
            .Add(x => x.Day, new DateOnly(2026, 5, 4))
            .Add(x => x.DayId, "premiere-day-20260504")
            .Add(x => x.Items, Array.Empty<PremiereItem>())
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        var prompts = component.FindAll("[data-day-scroll-prompt]");

        Assert.Equal(2, prompts.Count);
        Assert.Contains("Scroll to yesterday", prompts[0].TextContent);
        Assert.Contains("Scroll to tomorrow", prompts[1].TextContent);
        Assert.Equal("previous", prompts[0].GetAttribute("data-day-scroll-prompt"));
        Assert.Equal("next", prompts[1].GetAttribute("data-day-scroll-prompt"));
        Assert.Equal("true", prompts[0].GetAttribute("aria-hidden"));
        Assert.Equal("true", prompts[1].GetAttribute("aria-hidden"));
        Assert.Contains("day-scroll-prompt-top", prompts[0].ClassName);
        Assert.Contains("day-scroll-prompt-bottom", prompts[1].ClassName);
    }

    [Fact]
    public void CalendarDay_RendersEndSpacerAfterDayContent()
    {
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "movie:1",
                Type = PremiereItemType.MovieFirstRelease,
                MediaType = PremiereMediaType.Movie,
                TmdbId = 1,
                Title = "Bottom Space",
                PremiereDate = new DateOnly(2026, 5, 4)
            }
        };

        var component = Render<CalendarDay>(parameters => parameters
            .Add(x => x.Day, new DateOnly(2026, 5, 4))
            .Add(x => x.DayId, "premiere-day-20260504")
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        var spacer = Assert.Single(component.FindAll("[data-day-end-spacer]"));
        Assert.Equal("true", spacer.GetAttribute("aria-hidden"));
        Assert.Contains("day-end-spacer", spacer.ClassName);
        Assert.Contains("Scroll to tomorrow", component.Markup);
        Assert.True(component.Markup.IndexOf("day-end-spacer", StringComparison.Ordinal)
            < component.Markup.IndexOf("Scroll to tomorrow", StringComparison.Ordinal));
    }

    [Fact]
    public void CalendarDay_SkipsRenderWhenDayFingerprintIsUnchanged()
    {
        var items = Enumerable.Range(1, 12)
            .Select(index => new PremiereItem
            {
                CanonicalId = $"tv:{index}",
                Type = PremiereItemType.SeriesEpisode,
                MediaType = PremiereMediaType.Series,
                TmdbId = index,
                Title = $"Episode {index}",
                PremiereDate = new DateOnly(2026, 5, 4)
            })
            .ToArray();

        var component = Render<CalendarDay>(parameters => parameters
            .Add(x => x.Day, new DateOnly(2026, 5, 4))
            .Add(x => x.DayId, "premiere-day-20260504")
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        var renderCount = component.RenderCount;
        component.Render(parameters => parameters
            .Add(x => x.Day, new DateOnly(2026, 5, 4))
            .Add(x => x.DayId, "premiere-day-20260504")
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        Assert.Equal(renderCount, component.RenderCount);
    }

    [Fact]
    public void CalendarDay_RerendersWhenScoreSourceChanges()
    {
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "movie:1",
                Type = PremiereItemType.MovieFirstRelease,
                MediaType = PremiereMediaType.Movie,
                TmdbId = 1,
                Title = "Score Change",
                PremiereDate = new DateOnly(2026, 5, 4),
                TmdbScore = 7,
                ImdbScore = 8
            }
        };

        var component = Render<CalendarDay>(parameters => parameters
            .Add(x => x.Day, new DateOnly(2026, 5, 4))
            .Add(x => x.DayId, "premiere-day-20260504")
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        var renderCount = component.RenderCount;
        component.Render(parameters => parameters
            .Add(x => x.Day, new DateOnly(2026, 5, 4))
            .Add(x => x.DayId, "premiere-day-20260504")
            .Add(x => x.Items, items)
            .Add(x => x.ScoreSource, ScoreSource.Imdb));

        Assert.True(component.RenderCount > renderCount);
        Assert.NotEmpty(component.FindAll(".selected-score"));
    }

    [Fact]
    public void CalendarDay_RerendersWhenItemContentChangesForSameCanonicalId()
    {
        var firstItems = new[]
        {
            new PremiereItem
            {
                CanonicalId = "movie:1",
                Type = PremiereItemType.MovieFirstRelease,
                MediaType = PremiereMediaType.Movie,
                TmdbId = 1,
                Title = "Original Title",
                PremiereDate = new DateOnly(2026, 5, 4),
                Overview = "Original description"
            }
        };
        var updatedItems = new[]
        {
            firstItems[0] with
            {
                Title = "Updated Title",
                Overview = "Updated description"
            }
        };

        var component = Render<CalendarDay>(parameters => parameters
            .Add(x => x.Day, new DateOnly(2026, 5, 4))
            .Add(x => x.DayId, "premiere-day-20260504")
            .Add(x => x.Items, firstItems)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        component.Render(parameters => parameters
            .Add(x => x.Day, new DateOnly(2026, 5, 4))
            .Add(x => x.DayId, "premiere-day-20260504")
            .Add(x => x.Items, updatedItems)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        Assert.Contains("Updated Title", component.Markup);
        Assert.Contains("Updated description", component.Markup);
        Assert.DoesNotContain("Original Title", component.Markup);
    }
}
