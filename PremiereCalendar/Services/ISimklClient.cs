namespace PremiereCalendar.Services;

public interface ISimklClient
{
    Task<SimklSyncResult> SyncLibraryAsync(CancellationToken cancellationToken, bool forceRefresh = false);

    Task<SimklPinCodeResult> RequestPinCodeAsync(CancellationToken cancellationToken);

    Task<SimklPinStatusResult> CheckPinCodeAsync(string userCode, CancellationToken cancellationToken);
}

public enum SimklSyncStatus
{
    Disabled,
    Throttled,
    Unchanged,
    InitialSyncCompleted,
    DeltaSyncCompleted,
    Failed
}

public sealed record SimklSyncResult(
    SimklSyncStatus Status,
    string? ActivitiesAllUtc = null,
    string? Error = null);

public enum SimklPinStatus
{
    Authorized,
    Pending,
    SlowDown,
    Disabled,
    Failed
}

public sealed record SimklPinCodeResult(
    bool Success,
    string? UserCode = null,
    string? VerificationUrl = null,
    int ExpiresInSeconds = 0,
    int IntervalSeconds = 5,
    string? Error = null);

public sealed record SimklPinStatusResult(
    SimklPinStatus Status,
    string? AccessToken = null,
    string? Message = null);
