using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.ComponentTests;

public sealed partial class CalendarPageTests
{
    [Fact]
    public async Task CalendarPage_FinalUpdateThatIsNotReadyRemovesCardsTheServiceExcluded()
    {
        var service = new DirtyFinalPremiereService();
        Services.AddSingleton<IPremiereService>(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/movies?week=2026-05-04");
        var component = Render<PremiereCalendar.Components.Pages.Calendar>();
        try
        {
            component.WaitForAssertion(() => Assert.Equal(2, component.FindAll("[data-testid='premiere-card']").Count),
                TimeSpan.FromSeconds(3));
            service.ReleaseFinal.TrySetResult();
            await service.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            component.WaitForAssertion(() =>
            {
                var card = Assert.Single(component.FindAll("[data-testid='premiere-card']"));
                Assert.Contains("Kept movie", card.TextContent);
                Assert.DoesNotContain("Excluded movie", component.Markup);
            }, TimeSpan.FromSeconds(3));
        }
        finally { service.ReleaseFinal.TrySetResult(); }
    }

    private sealed class DirtyFinalPremiereService : IPremiereService
    {
        public TaskCompletionSource ReleaseFinal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(
            DateOnly start, DateOnly end, CancellationToken cancellationToken, bool forceRefresh = false,
            IProgress<PremiereLoadProgress>? progress = null, CalendarFilters? filters = null) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<PremiereLoadProgress> StreamPremieresAsync(
            DateOnly start, DateOnly end, bool forceRefresh = false, CalendarFilters? filters = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                PremiereItem[] shown =
                [
                    CreateMovie("movie:1", "Kept movie", start),
                    CreateMovie("movie:2", "Excluded movie", start)
                ];
                // A provisional, not-ready update shows both cards.
                yield return new PremiereLoadProgress("Discovery", shown.Length, shown.Length, shown);
                await ReleaseFinal.Task;
                // A dirty final is not ready, but it is the service's last word: movie 2 is gone.
                PremiereItem[] final = [shown[0]];
                yield return new PremiereLoadProgress("Complete", final.Length, final.Length, final, IsFinal: true)
                {
                    IsCalendarReady = false,
                    HasSourceErrors = true,
                    FailedSourceNames = ["Item cache validation"]
                };
            }
            finally { Finished.TrySetResult(); }
        }
    }
}
