using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PremiereCalendar.BrowserTests;

public sealed class BrowserAppFixture : IAsyncLifetime
{
    private Process? _process;
    private string? _temporaryRoot;
    private readonly List<string> _output = [];
    public string BaseUrl { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var repositoryRoot = FindRepositoryRoot();
        var port = ReservePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _temporaryRoot = Path.Combine(Path.GetTempPath(), $"premiere-calendar-browser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryRoot);
        var dotnet = Path.Combine(repositoryRoot, ".dotnet", "dotnet.exe");
        if (!File.Exists(dotnet)) dotnet = "dotnet";
        var application = Path.Combine(repositoryRoot, "PremiereCalendar", "bin", "Release", "net11.0", "PremiereCalendar.dll");
        if (!File.Exists(application)) throw new FileNotFoundException("Build the Release application before browser tests.", application);

        var startInfo = new ProcessStartInfo(dotnet, $"\"{application}\"")
        {
            WorkingDirectory = Path.Combine(repositoryRoot, "PremiereCalendar"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        Set(startInfo, "ASPNETCORE_URLS", BaseUrl);
        Set(startInfo, "Urls", BaseUrl);
        Set(startInfo, "ASPNETCORE_ENVIRONMENT", "Development");
        Set(startInfo, "BrowserTesting__Enabled", "true");
        Set(startInfo, "ProviderScheduler__Enabled", "false");
        Set(startInfo, "CalendarWarmup__Enabled", "false");
        Set(startInfo, "ProviderDeltaSync__Enabled", "false");
        Set(startInfo, "ImdbDataset__Enabled", "false");
        Set(startInfo, "Tmdb__BearerToken", "deterministic-browser-token");
        Set(startInfo, "AppDatabase__Path", Path.Combine(_temporaryRoot, "data", "calendar.db"));
        Set(startInfo, "AppDatabase__MigrationBackupDirectory", Path.Combine(_temporaryRoot, "backups"));
        Set(startInfo, "CalendarCache__Directory", Path.Combine(_temporaryRoot, "calendar-cache"));
        Set(startInfo, "ImageCache__Directory", Path.Combine(_temporaryRoot, "image-cache"));
        Set(startInfo, "Telemetry__LogDirectory", Path.Combine(_temporaryRoot, "logs"));
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start PremiereCalendar.");
        _process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (_output) _output.Add(eventArgs.Data); };
        _process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) lock (_output) _output.Add(eventArgs.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process.HasExited) throw new InvalidOperationException($"PremiereCalendar exited with code {_process.ExitCode}.");
            try
            {
                using var response = await client.GetAsync($"{BaseUrl}/health/ready");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(200);
        }
        string diagnostics;
        lock (_output) diagnostics = string.Join(Environment.NewLine, _output.TakeLast(80));
        throw new TimeoutException($"PremiereCalendar did not become ready for browser tests.{Environment.NewLine}{diagnostics}");
    }

    public Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }
        _process?.Dispose();
        if (_temporaryRoot is not null && Directory.Exists(_temporaryRoot)) Directory.Delete(_temporaryRoot, recursive: true);
        return Task.CompletedTask;
    }

    private static void Set(ProcessStartInfo startInfo, string key, string value) => startInfo.Environment[key] = value;

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PremiereCalendar.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("PremiereCalendar repository root was not found.");
    }
}

[CollectionDefinition(Name)]
public sealed class BrowserAppCollection : ICollectionFixture<BrowserAppFixture>
{
    public const string Name = "PremiereCalendar browser application";
}
