namespace PremiereCalendar.Services;

public sealed record ViewSyncDevice(
    string DeviceId,
    string DisplayName,
    bool SyncEnabled,
    string? GroupId,
    DateTimeOffset LastSeenUtc);

public sealed record ViewSyncGroup(
    string GroupId,
    string Name,
    DateTimeOffset CreatedUtc);

public sealed record ViewSyncGroupState(
    string GroupId,
    string RouteKey,
    string RelativeUrl,
    long Revision,
    DateTimeOffset UpdatedUtc,
    string UpdatedByDeviceId,
    string UpdatedByDeviceName);

public sealed record ViewSyncPublishResult(
    bool Published,
    ViewSyncGroupState? State,
    string? Reason = null);

public interface IViewSyncStore
{
    Task<ViewSyncGroup> CreateGroupAsync(
        string name,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ViewSyncGroup>> GetGroupsAsync(CancellationToken cancellationToken);

    Task<ViewSyncDevice> RegisterDeviceAsync(
        string deviceId,
        string displayName,
        bool syncEnabled,
        string? groupId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<ViewSyncDevice?> GetDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ViewSyncDevice>> GetGroupDevicesAsync(
        string groupId,
        CancellationToken cancellationToken);

    Task UngroupDeviceAsync(
        string deviceId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<ViewSyncPublishResult> PublishUrlAsync(
        string deviceId,
        string relativeUrl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(
        string deviceId,
        string? routeKey,
        CancellationToken cancellationToken);

    Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken);

    Task<ViewSyncGroupState?> GetGroupStateAsync(
        string groupId,
        string? routeKey,
        CancellationToken cancellationToken);

    Task<ViewSyncGroupState?> GetGroupStateAsync(
        string groupId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ViewSyncGroupState>> GetGroupStatesAsync(
        string groupId,
        CancellationToken cancellationToken);
}
