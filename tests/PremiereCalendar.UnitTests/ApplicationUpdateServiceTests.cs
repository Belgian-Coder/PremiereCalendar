using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
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
    public async Task StartUpdateAsync_RefusesMissingRepositoryPath()
    {
        var starter = new CapturingApplicationUpdateProcessStarter();
        var service = CreateService(
            new ApplicationUpdateOptions
            {
                RepositoryPath = Path.Combine(_root, "missing"),
                LogDirectory = "App_Data/logs/updates"
            },
            starter);

        var result = await service.StartUpdateAsync(CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("Repository path", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(starter.Request);
    }

    [Fact]
    public async Task StartUpdateAsync_StartsDetachedScriptWithConfiguredRepoRemoteAndBranch()
    {
        var repo = CreateRepositoryWithUpdateScripts();
        var starter = new CapturingApplicationUpdateProcessStarter();
        var service = CreateService(
            new ApplicationUpdateOptions
            {
                RepositoryPath = repo,
                Remote = "origin",
                Branch = "main",
                InstallScriptPath = "Install-PremiereCalendar.ps1",
                UpdateScriptPath = "deploy/Update-And-Install-PremiereCalendar.ps1",
                LogDirectory = "App_Data/logs/application-updates"
            },
            starter);

        var result = await service.StartUpdateAsync(CancellationToken.None);

        Assert.True(result.Started);
        Assert.Contains("Update started", result.Message, StringComparison.OrdinalIgnoreCase);
        var request = Assert.IsType<ApplicationUpdateProcessStartRequest>(starter.Request);
        Assert.Equal(repo, request.RepositoryPath);
        Assert.Equal("origin", request.Remote);
        Assert.Equal("main", request.Branch);
        Assert.Equal(Path.Combine(repo, "Install-PremiereCalendar.ps1"), request.InstallScriptPath);
        Assert.Equal(Path.Combine(repo, "deploy", "Update-And-Install-PremiereCalendar.ps1"), request.UpdateScriptPath);
        Assert.StartsWith(Path.Combine(_root, "App_Data", "logs", "application-updates"), request.LogPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".log", request.LogPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartUpdateAsync_DefaultsToMainWhenBranchIsOmitted()
    {
        var repo = CreateRepositoryWithUpdateScripts();
        var starter = new CapturingApplicationUpdateProcessStarter();
        var service = CreateService(
            new ApplicationUpdateOptions
            {
                RepositoryPath = repo,
                Remote = "origin",
                InstallScriptPath = "Install-PremiereCalendar.ps1",
                UpdateScriptPath = "deploy/Update-And-Install-PremiereCalendar.ps1",
                LogDirectory = "App_Data/logs/application-updates"
            },
            starter);

        var result = await service.StartUpdateAsync(CancellationToken.None);

        Assert.True(result.Started);
        var request = Assert.IsType<ApplicationUpdateProcessStartRequest>(starter.Request);
        Assert.Equal("main", request.Branch);
    }

    [Fact]
    public void GetStatus_ReportsConfiguredRepositoryAndLatestLog()
    {
        var repo = CreateRepositoryWithUpdateScripts();
        var logDirectory = Path.Combine(_root, "App_Data", "logs", "application-updates");
        Directory.CreateDirectory(logDirectory);
        var oldLog = Path.Combine(logDirectory, "application-update-20260530-120000.log");
        var newLog = Path.Combine(logDirectory, "application-update-20260530-121000.log");
        File.WriteAllText(oldLog, "old");
        File.WriteAllText(newLog, "new");
        File.SetLastWriteTimeUtc(oldLog, new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newLog, new DateTime(2026, 5, 30, 12, 10, 0, DateTimeKind.Utc));
        var service = CreateService(
            new ApplicationUpdateOptions
            {
                RepositoryPath = repo,
                Remote = "origin",
                Branch = "main",
                LogDirectory = "App_Data/logs/application-updates"
            },
            new CapturingApplicationUpdateProcessStarter());

        var status = service.GetStatus();

        Assert.True(status.IsConfigured);
        Assert.Equal(repo, status.RepositoryPath);
        Assert.Equal("origin", status.Remote);
        Assert.Equal("main", status.Branch);
        Assert.Equal(newLog, status.LatestLogPath);
    }

    private ApplicationUpdateService CreateService(
        ApplicationUpdateOptions options,
        IApplicationUpdateProcessStarter starter)
    {
        return new ApplicationUpdateService(
            Microsoft.Extensions.Options.Options.Create(options),
            starter,
            new FakeWebHostEnvironment(_root),
            TimeProvider.System);
    }

    private string CreateRepositoryWithUpdateScripts()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, "deploy"));
        File.WriteAllText(Path.Combine(repo, "Install-PremiereCalendar.ps1"), "# install");
        File.WriteAllText(Path.Combine(repo, "deploy", "Update-And-Install-PremiereCalendar.ps1"), "# update");
        return repo;
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
