namespace PremiereCalendar.Services;

public sealed record ViewSyncOverview(
    ViewSyncDevice Device,
    IReadOnlyList<ViewSyncGroup> Groups,
    IReadOnlyList<ViewSyncDevice> GroupDevices,
    ViewSyncGroupState? GroupState,
    IReadOnlyList<ViewSyncGroupState>? GroupStates = null,
    IReadOnlyList<ViewSyncGroupOverview>? GroupOverviews = null);

public sealed record ViewSyncGroupOverview(
    ViewSyncGroup Group,
    IReadOnlyList<ViewSyncDevice> Devices,
    IReadOnlyList<ViewSyncGroupState> States);

public sealed record ViewSyncStateChangedEventArgs(
    string GroupId,
    ViewSyncGroupState State);

public interface IViewSyncService
{
    event EventHandler<ViewSyncStateChangedEventArgs>? StateChanged;

    Task<ViewSyncOverview> GetOverviewAsync(
        string deviceId,
        CancellationToken cancellationToken);

    Task<ViewSyncOverview> SaveDeviceAsync(
        string deviceId,
        string displayName,
        bool syncEnabled,
        string? groupId,
        CancellationToken cancellationToken);

    Task<ViewSyncGroup> CreateGroupAsync(
        string name,
        CancellationToken cancellationToken);

    Task<ViewSyncOverview> UngroupDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken);

    Task<ViewSyncPublishResult> PublishUrlAsync(
        string deviceId,
        string relativeUrl,
        CancellationToken cancellationToken);

    Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(
        string deviceId,
        string? routeKey,
        CancellationToken cancellationToken);

    Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken);
}
