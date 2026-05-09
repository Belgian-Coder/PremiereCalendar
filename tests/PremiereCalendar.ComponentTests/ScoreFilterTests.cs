using Bunit;
using Microsoft.AspNetCore.Components;
using PremiereCalendar.Components.Shared;
using PremiereCalendar.Models;

namespace PremiereCalendar.ComponentTests;

public sealed class ScoreFilterTests : BunitContext
{
    [Fact]
    public void ScoreFilter_ChangingMinimumUpdatesFiltersAndNotifies()
    {
        var filters = new CalendarFilters();
        var notifications = 0;

        var component = Render<ScoreFilter>(parameters => parameters
            .Add(x => x.Filters, filters)
            .Add(x => x.OnFiltersChanged, EventCallback.Factory.Create(this, () => notifications++)));

        component.FindAll("input[type='number']")[0].Change("4.5");

        Assert.Equal(4.5, filters.MinScore);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void ScoreFilter_ChangingMaximumUpdatesFilters()
    {
        var filters = new CalendarFilters { MaxScore = 10 };

        var component = Render<ScoreFilter>(parameters => parameters
            .Add(x => x.Filters, filters));

        component.FindAll("input[type='number']")[1].Change("8.5");

        Assert.Equal(8.5, filters.MaxScore);
    }
}
