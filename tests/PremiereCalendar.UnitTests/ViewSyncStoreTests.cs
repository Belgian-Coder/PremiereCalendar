using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ViewSyncStoreTests : IDisposable
{
    private readonly PostgresTestDatabase _database = PostgresTestDatabase.Create();

    [Fact]
    public async Task ViewSyncStore_RoundTripsGroupsDevicesAndLatestUrl()
    {
        var store = CreateStore();
        var now = DateTimeOffset.Parse("2026-05-10T10:00:00Z");

        var group = await store.CreateGroupAsync("Living room", now, CancellationToken.None);
        var device = await store.RegisterDeviceAsync(
            "device-a",
            "Office PC",
            syncEnabled: true,
            group.GroupId,
            now.AddMinutes(1),
            CancellationToken.None);
        var published = await store.PublishUrlAsync(
            "device-a",
            "/series?week=2026-05-04&day=2026-05-05&seriesLang=en,nl",
            now.AddMinutes(2),
            CancellationToken.None);

        Assert.Equal(group.GroupId, device.GroupId);
        Assert.True(device.SyncEnabled);
        Assert.True(published.Published);
        Assert.NotNull(published.State);
        Assert.Equal(1, published.State.Revision);
        Assert.Equal("Office PC", published.State.UpdatedByDeviceName);

        var duplicate = await store.PublishUrlAsync(
            "device-a",
            "/series?week=2026-05-04&day=2026-05-05&seriesLang=en,nl",
            now.AddMinutes(3),
            CancellationToken.None);

        Assert.False(duplicate.Published);
        Assert.Equal(1, duplicate.State?.Revision);

        var restartedStore = CreateStore();
        var loadedDevice = await restartedStore.GetDeviceAsync("device-a", CancellationToken.None);
        var loadedState = await restartedStore.GetLatestStateForDeviceAsync("device-a", CancellationToken.None);

        Assert.NotNull(loadedDevice);
        Assert.Equal("Office PC", loadedDevice.DisplayName);
        Assert.Equal(group.GroupId, loadedDevice.GroupId);
        Assert.NotNull(loadedState);
        Assert.Equal("/series?week=2026-05-04&day=2026-05-05&seriesLang=en,nl", loadedState.RelativeUrl);
        Assert.Equal(1, loadedState.Revision);
    }

    [Fact]
    public async Task ViewSyncStore_KeepsOneActiveGroupPerDeviceAndUngroups()
    {
        var store = CreateStore();
        var now = DateTimeOffset.Parse("2026-05-10T10:00:00Z");
        var first = await store.CreateGroupAsync("Desk", now, CancellationToken.None);
        var second = await store.CreateGroupAsync("TV", now, CancellationToken.None);

        await store.RegisterDeviceAsync("device-a", "Office PC", true, first.GroupId, now, CancellationToken.None);
        await store.RegisterDeviceAsync("device-a", "Office PC", true, second.GroupId, now.AddMinutes(1), CancellationToken.None);

        var firstDevices = await store.GetGroupDevicesAsync(first.GroupId, CancellationToken.None);
        var secondDevices = await store.GetGroupDevicesAsync(second.GroupId, CancellationToken.None);
        var device = await store.GetDeviceAsync("device-a", CancellationToken.None);

        Assert.Empty(firstDevices);
        Assert.Single(secondDevices);
        Assert.Equal(second.GroupId, device?.GroupId);

        await store.UngroupDeviceAsync("device-a", now.AddMinutes(2), CancellationToken.None);
        device = await store.GetDeviceAsync("device-a", CancellationToken.None);

        Assert.NotNull(device);
        Assert.Null(device.GroupId);
        Assert.False(device.SyncEnabled);
    }

    [Fact]
    public async Task ViewSyncStore_KeepsLatestUrlPerCalendarRoute()
    {
        var store = CreateStore();
        var now = DateTimeOffset.Parse("2026-05-10T10:00:00Z");
        var group = await store.CreateGroupAsync("Shared", now, CancellationToken.None);
        await store.RegisterDeviceAsync("device-a", "Office PC", true, group.GroupId, now, CancellationToken.None);

        await store.PublishUrlAsync("device-a", "/movies?week=2026-04-27&day=2026-04-28", now.AddMinutes(1), CancellationToken.None);
        await store.PublishUrlAsync("device-a", "/series?week=2026-05-04&day=2026-05-05", now.AddMinutes(2), CancellationToken.None);

        var movie = await store.GetLatestStateForDeviceAsync("device-a", "movies", CancellationToken.None);
        var series = await store.GetLatestStateForDeviceAsync("device-a", "series", CancellationToken.None);
        var latest = await store.GetLatestStateForDeviceAsync("device-a", null, CancellationToken.None);

        Assert.NotNull(movie);
        Assert.Equal("movies", movie.RouteKey);
        Assert.Equal("/movies?week=2026-04-27&day=2026-04-28", movie.RelativeUrl);
        Assert.NotNull(series);
        Assert.Equal("series", series.RouteKey);
        Assert.Equal("/series?week=2026-05-04&day=2026-05-05", series.RelativeUrl);
        Assert.NotNull(latest);
        Assert.Equal("series", latest.RouteKey);
    }

    public void Dispose() => _database.Dispose();

    private ViewSyncStore CreateStore() => new(_database.Options);
}
