using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.ComponentTests;

public sealed partial class CalendarPageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CalendarPage_ReadyCalendarReleasesForegroundAndStreamsDetailsBeforeFinal(bool initiallyEmpty)
    {
        Services.Configure<CalendarLoadOptions>(options => options.ForegroundLoadBudgetSeconds = 30);
        var service = new GatedReadyPremiereService { InitiallyEmpty = initiallyEmpty };
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/movies?week=2026-05-04");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        try
        {
            component.WaitForAssertion(() =>
            {
                Assert.Equal("Calendar ready · Updating details", component.Find("[data-testid='calendar-enrichment']").TextContent.Trim());
                Assert.Empty(component.FindAll("[data-testid='loading']"));
                Assert.Empty(component.FindAll("[data-testid='refreshing']"));
                Assert.False(Services.GetRequiredService<CalendarPageCoordinator>().HasActiveForegroundLoad);
                Assert.Equal(initiallyEmpty ? 0 : 2, component.FindAll("[data-testid='premiere-card']").Count);
                Assert.Empty(_prefetcher.WeekStarts);
            }, TimeSpan.FromSeconds(3));
            await service.ForegroundReleased.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(service.Finished.Task.IsCompleted);

            service.ReleaseDetails.TrySetResult();
            component.WaitForAssertion(() =>
            {
                var card = Assert.Single(component.FindAll("[data-testid='premiere-card']"));
                Assert.Contains("Enriched movie", card.TextContent);
                Assert.Single(card.QuerySelectorAll("img[data-lazy-image]"));
                Assert.Single(component.FindAll("[data-testid='calendar-enrichment']"));
                Assert.Single(service.Calls);
                Assert.Empty(_prefetcher.WeekStarts);
            });
            Assert.False(service.Finished.Task.IsCompleted);

            service.ReleaseFinal.TrySetResult();
            await service.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            component.WaitForAssertion(() =>
            {
                Assert.Empty(component.FindAll("[data-testid='calendar-enrichment']"));
                Assert.Single(component.FindAll("[data-testid='premiere-card']"));
                Assert.Single(service.Calls);
                Assert.Single(_prefetcher.WeekStarts);
            });
        }
        finally
        {
            service.ReleaseDetails.TrySetResult();
            service.ReleaseFinal.TrySetResult();
        }
    }

    [Fact]
    public void CalendarPage_ReadyDetailsReapplyScoreFilterAndRemoveCardsThatNoLongerMatch()
    {
        var service = new GatedReadyPremiereService { PreserveBothCardsInDetails = true };
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/movies?week=2026-05-04&score=imdb&min=7");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        try
        {
            component.WaitForAssertion(() => Assert.Equal(2, component.FindAll("[data-testid='premiere-card']").Count));
            service.ReleaseDetails.TrySetResult();
            component.WaitForAssertion(() =>
            {
                Assert.Contains("Enriched movie", Assert.Single(component.FindAll("[data-testid='premiere-card']")).TextContent);
                Assert.Contains("score=imdb", navigation.Uri);
                Assert.Contains("min=7", navigation.Uri);
                Assert.Single(component.FindAll("[data-testid='calendar-enrichment']"));
                Assert.Single(service.Calls);
            });
        }
        finally
        {
            service.ReleaseDetails.TrySetResult();
            service.ReleaseFinal.TrySetResult();
        }
    }

    [Fact]
    public async Task CalendarPage_LateReadyDetailsCannotReplaceNewWeekOrRestartEnrichmentStatus()
    {
        var service = new GatedReadyPremiereService();
        Services.AddSingleton<IPremiereService>(service);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/movies?week=2026-05-04");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        try
        {
            component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='calendar-enrichment']")));
            await service.ForegroundReleased.Task.WaitAsync(TimeSpan.FromSeconds(3));
            navigation.NavigateTo("/movies?week=2026-05-11");
            component.WaitForAssertion(() =>
            {
                Assert.Contains("Next week movie", Assert.Single(component.FindAll("[data-testid='premiere-card']")).TextContent);
                Assert.Empty(component.FindAll("[data-testid='calendar-enrichment']"));
            });

            service.ReleaseDetails.TrySetResult();
            service.ReleaseFinal.TrySetResult();
            await service.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            component.WaitForAssertion(() =>
            {
                Assert.Contains("Next week movie", Assert.Single(component.FindAll("[data-testid='premiere-card']")).TextContent);
                Assert.Empty(component.FindAll("[data-testid='calendar-enrichment']"));
                Assert.Equal(2, service.Calls.Count);
            });
        }
        finally
        {
            service.ReleaseDetails.TrySetResult();
            service.ReleaseFinal.TrySetResult();
        }
    }

    private sealed class GatedReadyPremiereService : IPremiereService
    {
        public List<DateOnly> Calls { get; } = [];
        public TaskCompletionSource ReleaseDetails { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFinal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ForegroundReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool InitiallyEmpty { get; init; }
        public bool PreserveBothCardsInDetails { get; init; }

        public Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(
            DateOnly start, DateOnly end, CancellationToken cancellationToken, bool forceRefresh = false,
            IProgress<PremiereLoadProgress>? progress = null, CalendarFilters? filters = null) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<PremiereLoadProgress> StreamPremieresAsync(
            DateOnly start, DateOnly end, bool forceRefresh = false, CalendarFilters? filters = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(start);
            if (Calls.Count > 1)
            {
                var nextItems = new[] { CreateMovie("movie:999", "Next week movie", start) };
                yield return new PremiereLoadProgress("Complete", 1, 1, nextItems, IsFinal: true)
                {
                    IsCalendarReady = true
                };
                yield break;
            }

            using var demand = await CalendarObservationScope.Current!.AttachAsync(_ =>
            {
                ForegroundReleased.TrySetResult();
                return Task.CompletedTask;
            }, cancellationToken);
            try
            {
                PremiereItem[] cachedItems = InitiallyEmpty ? [] :
                [
                    CreateMovie("movie:1", "Cached movie", start),
                    CreateMovie("movie:2", "Removed movie", start)
                ];
                yield return new PremiereLoadProgress("Week cache", cachedItems.Length, cachedItems.Length, cachedItems, FromCache: true)
                {
                    IsCalendarReady = true,
                    IsEnriching = true
                };
                // Deliver after cancellation too, to verify superseded-load guards.
                await ReleaseDetails.Task;
                var enriched = CreateMovie("movie:1", "Enriched movie", start) with
                {
                    PosterUrl = "https://image.tmdb.org/t/p/w185/enriched.jpg",
                    ImdbScore = 8.2,
                    ImdbVoteCount = 300
                };
                PremiereItem[] details = PreserveBothCardsInDetails
                    ? [enriched, CreateMovie("movie:2", "Removed movie", start) with { ImdbScore = 4.1 }]
                    : [enriched];
                yield return new PremiereLoadProgress("Details", details.Length, details.Length, details)
                {
                    IsCalendarReady = true,
                    IsEnriching = true
                };
                await ReleaseFinal.Task;
                yield return new PremiereLoadProgress("Complete", details.Length, details.Length, details, IsFinal: true)
                {
                    IsCalendarReady = true
                };
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }
}
