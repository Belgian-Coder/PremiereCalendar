using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IAdjacentWeekPrefetcher
{
    void PrefetchAdjacentWeeks(DateOnly weekStart, CalendarFilters? filters = null);
}
