using System.Text.Json;

namespace PremiereCalendar.Services;

public sealed class BackgroundJobTimelineService
{
    private const string StoreKey = "Diagnostics.BackgroundJobs";
    private const int MaximumEvents = 30;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IAppStateStore _store;
    private readonly TimeProvider _timeProvider;

    public BackgroundJobTimelineService(IAppStateStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<BackgroundJobEvent>> GetRecentAsync(CancellationToken cancellationToken)
    {
        var events = await LoadAsync(cancellationToken);
        return events
            .OrderByDescending(entry => entry.OccurredUtc)
            .ThenBy(entry => entry.JobName, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task RecordAsync(
        string jobName,
        BackgroundJobStatus status,
        string message,
        DateTimeOffset? occurredUtc = null,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default)
    {
        var events = await LoadAsync(cancellationToken);
        events.Add(new BackgroundJobEvent(
            Guid.NewGuid().ToString("N"),
            jobName,
            status,
            message,
            occurredUtc ?? _timeProvider.GetUtcNow(),
            duration is null ? null : Convert.ToInt64(duration.Value.TotalMilliseconds)));

        var trimmed = events
            .OrderByDescending(entry => entry.OccurredUtc)
            .Take(MaximumEvents)
            .ToArray();
        await _store.SetValueAsync(StoreKey, JsonSerializer.Serialize(trimmed, JsonOptions), cancellationToken);
    }

    private async Task<List<BackgroundJobEvent>> LoadAsync(CancellationToken cancellationToken)
    {
        var json = await _store.GetValueAsync(StoreKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<BackgroundJobEvent>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
