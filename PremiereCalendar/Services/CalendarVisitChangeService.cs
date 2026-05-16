using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PremiereCalendar.Services;

public sealed class CalendarVisitChangeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IAppStateStore _store;
    private readonly TimeProvider _timeProvider;

    public CalendarVisitChangeService(IAppStateStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task<CalendarVisitChangeSummary> RecordVisitAsync(
        CalendarVisitScope scope,
        IReadOnlyCollection<string> canonicalIds,
        CancellationToken cancellationToken)
    {
        var key = StoreKey(scope);
        var now = _timeProvider.GetUtcNow();
        var previous = await LoadAsync(key, cancellationToken);
        var nextIds = canonicalIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var newCount = 0;
        var removedCount = 0;
        if (previous is not null)
        {
            var previousIds = previous.CanonicalIds.ToHashSet(StringComparer.Ordinal);
            var nextSet = nextIds.ToHashSet(StringComparer.Ordinal);
            newCount = nextSet.Count(id => !previousIds.Contains(id));
            removedCount = previousIds.Count(id => !nextSet.Contains(id));
        }

        var snapshot = new VisitSnapshot(now, nextIds);
        await _store.SetValueAsync(key, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken);
        return new CalendarVisitChangeSummary(
            previous is not null,
            newCount,
            removedCount,
            previous?.SeenUtc,
            now);
    }

    private async Task<VisitSnapshot?> LoadAsync(string key, CancellationToken cancellationToken)
    {
        var json = await _store.GetValueAsync(key, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VisitSnapshot>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StoreKey(CalendarVisitScope scope)
    {
        var raw = $"{scope.PageMode}|{scope.WeekStart:yyyyMMdd}|{scope.CacheKey}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return $"Calendar.Visits.{hash}";
    }

    private sealed record VisitSnapshot(DateTimeOffset SeenUtc, string[] CanonicalIds);
}
