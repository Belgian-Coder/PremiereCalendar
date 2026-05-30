using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ViewSyncServiceTests
{
    [Fact]
    public async Task PublishUrlAsync_BroadcastsOnlyNewGroupStates()
    {
        var root = CreateRoot();
        try
        {
            var timeProvider = new TestTimeProvider(DateTimeOffset.Parse("2026-05-10T10:00:00Z"));
            var service = CreateService(root, timeProvider);
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
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(root);
        }
    }

    [Fact]
    public async Task GetOverviewAsync_CreatesDisabledDeviceWithoutAGroup()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root, new TestTimeProvider(DateTimeOffset.Parse("2026-05-10T10:00:00Z")));

            var overview = await service.GetOverviewAsync("device-a", CancellationToken.None);

            Assert.Equal("device-a", overview.Device.DeviceId);
            Assert.False(overview.Device.SyncEnabled);
            Assert.Null(overview.Device.GroupId);
            Assert.Empty(overview.Groups);
            Assert.Empty(overview.GroupDevices);
            Assert.Null(overview.GroupState);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(root);
        }
    }

    [Fact]
    public async Task GetOverviewAsync_ReturnsDevicesAndRouteStatesGroupedByGroup()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root, new TestTimeProvider(DateTimeOffset.Parse("2026-05-10T10:00:00Z")));
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
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(root);
        }
    }

    private static ViewSyncService CreateService(string root, TimeProvider timeProvider)
    {
        return new ViewSyncService(
            new SqliteViewSyncStore(
                Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/app.db" }),
                new FakeWebHostEnvironment(root)),
            timeProvider);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"premiere-calendar-view-sync-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed class FakeWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = contentRootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
