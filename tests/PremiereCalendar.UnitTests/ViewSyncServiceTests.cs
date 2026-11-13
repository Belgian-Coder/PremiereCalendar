using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ViewSyncServiceTests : IDisposable
{
    private readonly PostgresTestDatabase _database = PostgresTestDatabase.Create();

    [Fact]
    public async Task PublishUrlAsync_BroadcastsOnlyNewGroupStates()
    {
        var timeProvider = new TestTimeProvider(DateTimeOffset.Parse("2026-05-10T10:00:00Z"));
        var service = CreateService(timeProvider);
        var group = await service.CreateGroupAsync("Shared screens", CancellationToken.None);
        await service.SaveDeviceAsync("device-a", "Office PC", syncEnabled: true, group.GroupId, CancellationToken.None);
        var events = new List<ViewSyncStateChangedEventArgs>();
        service.StateChanged += (_, args) => events.Add(args);

        var published = await service.PublishUrlAsync(
            "device-a",
            "/movies?week=2026-04-27&day=2026-04-28",
            CancellationToken.None);
        var duplicate = await service.PublishUrlAsync(
            "device-a",
            "/movies?week=2026-04-27&day=2026-04-28",
            CancellationToken.None);

        Assert.True(published.Published);
        Assert.False(duplicate.Published);
        Assert.Single(events);
        Assert.Equal(group.GroupId, events[0].GroupId);
        Assert.Equal("/movies?week=2026-04-27&day=2026-04-28", events[0].State.RelativeUrl);
        Assert.Equal("device-a", events[0].State.UpdatedByDeviceId);
    }

    [Fact]
    public async Task GetOverviewAsync_CreatesDisabledDeviceWithoutAGroup()
    {
        var service = CreateService(new TestTimeProvider(DateTimeOffset.Parse("2026-05-10T10:00:00Z")));

        var overview = await service.GetOverviewAsync("device-a", CancellationToken.None);

        Assert.Equal("device-a", overview.Device.DeviceId);
        Assert.False(overview.Device.SyncEnabled);
        Assert.Null(overview.Device.GroupId);
        Assert.Empty(overview.Groups);
        Assert.Empty(overview.GroupDevices);
        Assert.Null(overview.GroupState);
    }

    [Fact]
    public async Task GetOverviewAsync_ReturnsDevicesAndRouteStatesGroupedByGroup()
    {
        var service = CreateService(new TestTimeProvider(DateTimeOffset.Parse("2026-05-10T10:00:00Z")));
        var livingRoom = await service.CreateGroupAsync("Living room", CancellationToken.None);
        var bedroom = await service.CreateGroupAsync("Bedroom", CancellationToken.None);
        await service.SaveDeviceAsync("device-a", "Office PC", syncEnabled: true, livingRoom.GroupId, CancellationToken.None);
        await service.SaveDeviceAsync("device-b", "Kitchen tablet", syncEnabled: true, livingRoom.GroupId, CancellationToken.None);
        await service.SaveDeviceAsync("device-c", "Bedroom TV", syncEnabled: true, bedroom.GroupId, CancellationToken.None);
        await service.PublishUrlAsync("device-b", "/series?week=2026-05-04&day=2026-05-05", CancellationToken.None);
        await service.PublishUrlAsync("device-c", "/movies?week=2026-05-11&day=2026-05-12", CancellationToken.None);

        var overview = await service.GetOverviewAsync("device-a", CancellationToken.None);

        Assert.Equal(2, overview.GroupOverviews?.Count);
        var livingRoomOverview = Assert.Single(overview.GroupOverviews!, group => group.Group.GroupId == livingRoom.GroupId);
        var bedroomOverview = Assert.Single(overview.GroupOverviews!, group => group.Group.GroupId == bedroom.GroupId);
        Assert.Contains(livingRoomOverview.Devices, device => device.DisplayName == "Office PC");
        Assert.Contains(livingRoomOverview.Devices, device => device.DisplayName == "Kitchen tablet");
        Assert.Contains(livingRoomOverview.States, state => state.RelativeUrl == "/series?week=2026-05-04&day=2026-05-05");
        Assert.Contains(bedroomOverview.Devices, device => device.DisplayName == "Bedroom TV");
        Assert.Contains(bedroomOverview.States, state => state.RelativeUrl == "/movies?week=2026-05-11&day=2026-05-12");
    }

    public void Dispose() => _database.Dispose();

    private ViewSyncService CreateService(TimeProvider timeProvider)
        => new(new ViewSyncStore(_database.Options), timeProvider);

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
