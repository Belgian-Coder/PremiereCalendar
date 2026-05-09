namespace PremiereCalendar.Services;

public interface ISimklSyncStateStore
{
    Task<SimklSyncState> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(SimklSyncState state, CancellationToken cancellationToken);
}

public sealed record SimklSyncState(
    string? LastActivitiesAllUtc = null,
    string? LastActivitiesJson = null,
    bool InitialSyncCompleted = false,
    DateTimeOffset? LastCheckedUtc = null);
