namespace PremiereCalendar.Services;

public sealed class ViewSyncService : IViewSyncService
{
    private readonly IViewSyncStore _store;
    private readonly TimeProvider _timeProvider;

    public ViewSyncService(
        IViewSyncStore store,
        TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public event EventHandler<ViewSyncStateChangedEventArgs>? StateChanged;

    public async Task<ViewSyncOverview> GetOverviewAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var device = await _store.GetDeviceAsync(deviceId, cancellationToken)
            ?? await _store.RegisterDeviceAsync(
                deviceId,
                "This browser",
                syncEnabled: false,
                groupId: null,
                now,
                cancellationToken);

        return await BuildOverviewAsync(device, cancellationToken);
    }

    public async Task<ViewSyncOverview> SaveDeviceAsync(
        string deviceId,
        string displayName,
        bool syncEnabled,
        string? groupId,
        CancellationToken cancellationToken)
    {
        var device = await _store.RegisterDeviceAsync(
            deviceId,
            displayName,
            syncEnabled,
            groupId,
            _timeProvider.GetUtcNow(),
            cancellationToken);

        return await BuildOverviewAsync(device, cancellationToken);
    }

    public async Task<ViewSyncGroup> CreateGroupAsync(
        string name,
        CancellationToken cancellationToken)
    {
        return await _store.CreateGroupAsync(name, _timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task<ViewSyncOverview> UngroupDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        await _store.UngroupDeviceAsync(deviceId, _timeProvider.GetUtcNow(), cancellationToken);
        return await GetOverviewAsync(deviceId, cancellationToken);
    }

    public async Task<ViewSyncPublishResult> PublishUrlAsync(
        string deviceId,
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        var result = await _store.PublishUrlAsync(
            deviceId,
            relativeUrl,
            _timeProvider.GetUtcNow(),
            cancellationToken);

        if (result.Published && result.State is { } state)
        {
            StateChanged?.Invoke(this, new ViewSyncStateChangedEventArgs(state.GroupId, state));
        }

        return result;
    }

    public async Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        return await _store.GetLatestStateForDeviceAsync(deviceId, cancellationToken);
    }

    public async Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(
        string deviceId,
        string? routeKey,
        CancellationToken cancellationToken)
    {
        return await _store.GetLatestStateForDeviceAsync(deviceId, routeKey, cancellationToken);
    }

    private async Task<ViewSyncOverview> BuildOverviewAsync(
        ViewSyncDevice device,
        CancellationToken cancellationToken)
    {
        var groups = await _store.GetGroupsAsync(cancellationToken);
        var groupDevices = string.IsNullOrWhiteSpace(device.GroupId)
            ? []
            : await _store.GetGroupDevicesAsync(device.GroupId, cancellationToken);
        var groupState = string.IsNullOrWhiteSpace(device.GroupId)
            ? null
            : await _store.GetGroupStateAsync(device.GroupId, cancellationToken);
        var groupStates = string.IsNullOrWhiteSpace(device.GroupId)
            ? []
            : await _store.GetGroupStatesAsync(device.GroupId, cancellationToken);

        return new ViewSyncOverview(device, groups, groupDevices, groupState, groupStates);
    }
}
