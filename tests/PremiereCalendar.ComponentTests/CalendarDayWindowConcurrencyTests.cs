using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PremiereCalendar.Components.Shared;
using PremiereCalendar.Models;

namespace PremiereCalendar.ComponentTests;

public sealed class CalendarDayWindowConcurrencyTests : BunitContext
{
    private readonly ControlledWindowMeasurement _measurement = new();

    public CalendarDayWindowConcurrencyTests()
    {
        Services.AddSingleton<IJSRuntime>(_measurement.Runtime);
    }

    [Fact]
    public async Task ForwardWhileForwardIsAwaiting_CommitsOnlyOneWindowMovement()
    {
        var component = RenderDay();
        var gate = _measurement.Hold();
        var first = component.Find("[data-day-load-more]").ClickAsync(new MouseEventArgs());
        component.WaitForAssertion(() => Assert.Equal(1, _measurement.Calls));
        Task? second = null;
        try
        {
            second = component.Find("[data-day-load-more]").ClickAsync(new MouseEventArgs());
            // Flush the renderer's dispatch queue while the measurement remains held.
            await component.InvokeAsync(() => { });
        }
        finally
        {
            gate.TrySetResult(2200);
            await first;
            if (second is not null) await second;
        }

        Assert.Equal(1, _measurement.Calls);
        AssertWindow(component, 11, 50);
        Assert.Contains("2200px", component.Find("[data-day-window-spacer]").GetAttribute("style"));
        await component.Find("[data-day-load-previous]").ClickAsync(new MouseEventArgs());
        AssertWindow(component, 1, 40);
        Assert.Empty(component.FindAll("[data-day-window-spacer]"));
    }

    [Fact]
    public async Task PreviousWhileForwardIsAwaiting_DoesNotPopTheExistingSlice()
    {
        var component = RenderDay();
        await component.Find("[data-day-load-more]").ClickAsync(new MouseEventArgs());
        AssertWindow(component, 11, 50);

        var gate = _measurement.Hold();
        var forward = component.Find("[data-day-load-more]").ClickAsync(new MouseEventArgs());
        component.WaitForAssertion(() => Assert.Equal(2, _measurement.Calls));
        Task? previous = null;
        try
        {
            previous = component.Find("[data-day-load-previous]").ClickAsync(new MouseEventArgs());
            await component.InvokeAsync(() => { });
        }
        finally
        {
            gate.TrySetResult(2200);
            await forward;
            if (previous is not null) await previous;
        }

        Assert.Equal(2, _measurement.Calls);
        AssertWindow(component, 21, 60);
        Assert.Contains("4400px", component.Find("[data-day-window-spacer]").GetAttribute("style"));
        await component.Find("[data-day-load-previous]").ClickAsync(new MouseEventArgs());
        AssertWindow(component, 11, 50);
        Assert.Contains("2200px", component.Find("[data-day-window-spacer]").GetAttribute("style"));
    }

    [Fact]
    public async Task ScopeResetWhileForwardIsAwaiting_FencesOldCompletionAndPreservesNewPendingOwner()
    {
        var component = RenderDay();
        var oldGate = _measurement.Hold();
        var oldForward = component.Find("[data-day-load-more]").ClickAsync(new MouseEventArgs());
        component.WaitForAssertion(() => Assert.Equal(1, _measurement.Calls));
        Task? newForward = null;
        TaskCompletionSource<double>? newGate = null;
        try
        {
            component.Render(parameters => parameters.Add(x => x.WindowScope, "movies"));
            AssertWindow(component, 1, 40);
            newGate = _measurement.Hold();
            newForward = component.Find("[data-day-load-more]").ClickAsync(new MouseEventArgs());
            component.WaitForAssertion(() => Assert.Equal(2, _measurement.Calls));
            oldGate.TrySetResult(2200);
            await oldForward;

            AssertWindow(component, 1, 40);
            Assert.Equal("true", component.Find("[data-testid='calendar-day']").GetAttribute("data-day-window-pending"));
            Assert.True(component.Find("[data-day-load-more]").HasAttribute("disabled"));
            Assert.Empty(component.FindAll("[data-day-window-spacer]"));
        }
        finally
        {
            oldGate.TrySetResult(2200);
            newGate?.TrySetResult(3300);
            await oldForward;
            if (newForward is not null) await newForward;
        }

        AssertWindow(component, 11, 50);
        Assert.Equal("false", component.Find("[data-testid='calendar-day']").GetAttribute("data-day-window-pending"));
        Assert.Contains("3300px", component.Find("[data-day-window-spacer]").GetAttribute("style"));
        await component.Find("[data-day-load-previous]").ClickAsync(new MouseEventArgs());
        AssertWindow(component, 1, 40);
        Assert.Empty(component.FindAll("[data-day-window-spacer]"));
    }

    [Fact]
    public async Task ShrinkingItemsWhileForwardIsAwaiting_FencesOldBoundsAndPreservesNewPendingOwner()
    {
        var component = RenderDay();
        var oldGate = _measurement.Hold();
        var oldForward = component.Find("[data-day-load-more]").ClickAsync(new MouseEventArgs());
        component.WaitForAssertion(() => Assert.Equal(1, _measurement.Calls));
        Task? newForward = null;
        TaskCompletionSource<double>? newGate = null;
        try
        {
            component.Render(parameters => parameters.Add(x => x.Items, Items(45)));
            AssertWindow(component, 1, 40, 45);
            newGate = _measurement.Hold();
            newForward = component.Find("[data-day-load-more]").ClickAsync(new MouseEventArgs());
            component.WaitForAssertion(() => Assert.Equal(2, _measurement.Calls));
            oldGate.TrySetResult(2200);
            await oldForward;

            AssertWindow(component, 1, 40, 45);
            Assert.Equal("true", component.Find("[data-testid='calendar-day']").GetAttribute("data-day-window-pending"));
            Assert.Empty(component.FindAll("[data-day-window-spacer]"));
        }
        finally
        {
            oldGate.TrySetResult(2200);
            newGate?.TrySetResult(1100);
            await oldForward;
            if (newForward is not null) await newForward;
        }

        AssertWindow(component, 6, 45, 45);
        Assert.Contains("1100px", component.Find("[data-day-window-spacer]").GetAttribute("style"));
        await component.Find("[data-day-load-previous]").ClickAsync(new MouseEventArgs());
        AssertWindow(component, 1, 40, 45);
    }

    private IRenderedComponent<CalendarDay> RenderDay()
    {
        var day = new DateOnly(2026, 5, 4);
        return Render<CalendarDay>(parameters => parameters
            .Add(x => x.Day, day)
            .Add(x => x.DayId, "premiere-day-20260504")
            .Add(x => x.Items, Items(80))
            .Add(x => x.InitialVisibleCount, 40));
    }

    private static PremiereItem[] Items(int count) =>
        Enumerable.Range(1, count).Select(index => new PremiereItem
        {
            CanonicalId = $"movie:{index}",
            Type = PremiereItemType.MovieFirstRelease,
            MediaType = PremiereMediaType.Movie,
            TmdbId = index,
            Title = $"Movie {index}",
            PremiereDate = new DateOnly(2026, 5, 4)
        }).ToArray();

    private static void AssertWindow(IRenderedComponent<CalendarDay> component, int first, int last, int total = 80)
    {
        var actual = component.FindAll("[data-testid='premiere-card']")
            .Select(card => card.GetAttribute("data-canonical-id")).ToArray();
        Assert.Equal(Enumerable.Range(first, last - first + 1).Select(index => $"movie:{index}"), actual);
        Assert.Equal(actual.Length, actual.Distinct().Count());
        Assert.InRange(actual.Length, 1, 40);
        // The count label is absent when the final batch has no remaining items.
        if (last < total) Assert.Contains($"Showing {first}-{last} of {total}", component.Markup);
        var section = component.Find("[data-testid='calendar-day']");
        Assert.Equal((first - 1).ToString(), section.GetAttribute("data-day-window-start"));
        Assert.Equal(last.ToString(), section.GetAttribute("data-day-window-end"));
        Assert.Equal(total.ToString(), section.GetAttribute("data-day-window-total"));
    }

    private sealed class ControlledWindowMeasurement
    {
        private TaskCompletionSource<double>? _gate;
        public IJSRuntime Runtime { get; }
        public int Calls { get; private set; }

        public ControlledWindowMeasurement() => Runtime = new RuntimeAdapter(this);

        public TaskCompletionSource<double> Hold() =>
            _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ValueTask<TValue> Measure<TValue>(string identifier)
        {
            Assert.Equal("premiereCalendarDayWindow.measureTrimHeight", identifier);
            Assert.Equal(typeof(double), typeof(TValue));
            Calls++;
            var task = _gate?.Task ?? Task.FromResult(2200d);
            return new ValueTask<TValue>(Complete<TValue>(task));
        }

        private static async Task<TValue> Complete<TValue>(Task<double> task) => (TValue)(object)await task;

        private sealed class RuntimeAdapter(ControlledWindowMeasurement owner) : IJSRuntime
        {
            ValueTask<TValue> IJSRuntime.InvokeAsync<TValue>(string identifier, object?[]? args) => owner.Measure<TValue>(identifier);
            ValueTask<TValue> IJSRuntime.InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => owner.Measure<TValue>(identifier);
        }
    }
}
