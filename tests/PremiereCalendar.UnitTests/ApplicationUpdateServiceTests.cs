using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ApplicationUpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-update-tests-{Guid.NewGuid():N}");

    public ApplicationUpdateServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task StartUpdateAsync_RefusesMissingSignedUpdater()
    {
        var starter = new CapturingApplicationUpdateProcessStarter();
        var service = CreateService(
            new ApplicationUpdateOptions
            {
                UpdaterScriptPath = Path.Combine(_root, "missing.ps1"),
                InstallRoot = Path.Combine(_root, "install"),
                DataRoot = Path.Combine(_root, "data")
            },
            starter);

        var result = await service.StartUpdateAsync(CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("updater was not found", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(starter.Request);
    }

    [Fact]
    public async Task StartUpdateAsync_StartsInstalledSignedReleaseUpdater()
    {
        var updater = CreateUpdater();
        var installRoot = Path.Combine(_root, "install");
        var dataRoot = Path.Combine(_root, "data");
        var starter = new CapturingApplicationUpdateProcessStarter();
        var service = CreateService(
            new ApplicationUpdateOptions
            {
                UpdaterScriptPath = updater,
                InstallRoot = installRoot,
                DataRoot = dataRoot,
                LogDirectory = Path.Combine(dataRoot, "logs", "application-updates"),
                Repository = "Belgian-Coder/PremiereCalendar"
            },
            starter);

        var result = await service.StartUpdateAsync(CancellationToken.None);

        Assert.True(result.Started);
        Assert.Contains("Signed GitHub release update started", result.Message, StringComparison.Ordinal);
        var request = Assert.IsType<ApplicationUpdateProcessStartRequest>(starter.Request);
        Assert.Equal(updater, request.UpdaterScriptPath);
        Assert.Equal("Belgian-Coder/PremiereCalendar", request.Repository);
        Assert.Equal(installRoot, request.InstallRoot);
        Assert.Equal(dataRoot, request.DataRoot);
        Assert.StartsWith(Path.Combine(dataRoot, "logs", "application-updates"), request.LogPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".log", request.LogPath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("owner/repo/extra")]
    [InlineData("owner/repo name")]
    public async Task StartUpdateAsync_RefusesInvalidRepository(string repository)
    {
        var service = CreateService(
            new ApplicationUpdateOptions
            {
                UpdaterScriptPath = CreateUpdater(),
                InstallRoot = Path.Combine(_root, "install"),
                DataRoot = Path.Combine(_root, "data"),
                Repository = repository
            },
            new CapturingApplicationUpdateProcessStarter());

        var result = await service.StartUpdateAsync(CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("owner/name", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStatus_ReportsActiveVersionAndLatestSignedUpdateLog()
    {
        var updater = CreateUpdater();
        var installRoot = Path.Combine(_root, "install");
        var dataRoot = Path.Combine(_root, "data");
        var updaterRoot = Path.Combine(installRoot, "updater");
        var logDirectory = Path.Combine(dataRoot, "logs", "application-updates");
        Directory.CreateDirectory(updaterRoot);
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(Path.Combine(updaterRoot, "active-version.txt"), "1.1.5\n");
        var oldLog = Path.Combine(logDirectory, "application-update-20260530-120000.log");
        var newLog = Path.Combine(logDirectory, "application-update-20260530-121000.log");
        File.WriteAllText(oldLog, "old");
        File.WriteAllText(newLog, "PremiereCalendar 1.1.5 installed and healthy.");
        File.SetLastWriteTimeUtc(oldLog, new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newLog, new DateTime(2026, 5, 30, 12, 10, 0, DateTimeKind.Utc));
        var service = CreateService(
            new ApplicationUpdateOptions
            {
                UpdaterScriptPath = updater,
                InstallRoot = installRoot,
                DataRoot = dataRoot,
                LogDirectory = logDirectory
            },
            new CapturingApplicationUpdateProcessStarter());

        var status = service.GetStatus();

        Assert.True(status.IsConfigured);
        Assert.Equal("1.1.5", status.ActiveVersion);
        Assert.Equal("Succeeded", status.LastResult);
        Assert.Equal(newLog, status.LatestLogPath);
        Assert.Contains("installed and healthy", status.LatestLogTail, StringComparison.Ordinal);
    }

    private ApplicationUpdateService CreateService(ApplicationUpdateOptions options, IApplicationUpdateProcessStarter starter)
    {
        return new ApplicationUpdateService(
            Microsoft.Extensions.Options.Options.Create(options),
            starter,
            new FakeWebHostEnvironment(_root),
            TimeProvider.System);
    }

    private string CreateUpdater()
    {
        var updater = Path.Combine(_root, "updater", "install-github-release.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(updater)!);
        File.WriteAllText(updater, "# signed updater");
        return updater;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CapturingApplicationUpdateProcessStarter : IApplicationUpdateProcessStarter
    {
        public ApplicationUpdateProcessStartRequest? Request { get; private set; }

        public ApplicationUpdateProcessStartResult Start(ApplicationUpdateProcessStartRequest request)
        {
            Request = request;
            return new ApplicationUpdateProcessStartResult(1234);
        }
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public FakeWebHostEnvironment(string root)
        {
            ContentRootPath = root;
            WebRootPath = root;
            ContentRootFileProvider = new PhysicalFileProvider(root);
            WebRootFileProvider = new PhysicalFileProvider(root);
        }

        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PremiereCalendar.UnitTests";
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
