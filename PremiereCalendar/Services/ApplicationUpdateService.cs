using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public interface IApplicationUpdateService
{
    ApplicationUpdateStatus GetStatus();

    Task<ApplicationUpdateStartResult> StartUpdateAsync(CancellationToken cancellationToken);
}

public sealed class ApplicationUpdateService : IApplicationUpdateService
{
    private readonly IOptions<ApplicationUpdateOptions> _options;
    private readonly IApplicationUpdateProcessStarter _processStarter;
    private readonly IWebHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ApplicationUpdateService(
        IOptions<ApplicationUpdateOptions> options,
        IApplicationUpdateProcessStarter processStarter,
        IWebHostEnvironment environment,
        TimeProvider timeProvider)
    {
        _options = options;
        _processStarter = processStarter;
        _environment = environment;
        _timeProvider = timeProvider;
    }

    public ApplicationUpdateStatus GetStatus()
    {
        var validation = ValidateOptions(requireScripts: true);
        var options = NormalizedOptions();
        var repositoryPath = ResolvePath(options.RepositoryPath, _environment.ContentRootPath);
        var latestLogPath = FindLatestLogPath(ResolvePath(options.LogDirectory, _environment.ContentRootPath));
        return new ApplicationUpdateStatus(
            options.Enabled,
            validation.IsValid,
            repositoryPath,
            options.Remote,
            options.Branch,
            latestLogPath,
            validation.Message);
    }

    public async Task<ApplicationUpdateStartResult> StartUpdateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var validation = ValidateOptions(requireScripts: true);
            if (!validation.IsValid || validation.Request is null)
            {
                return new ApplicationUpdateStartResult(false, validation.Message, null);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(validation.Request.LogPath)!);
            _processStarter.Start(validation.Request);
            return new ApplicationUpdateStartResult(
                true,
                $"Update started from {validation.Request.Remote}/{validation.Request.Branch}. The app may restart while the installer runs.",
                validation.Request.LogPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ApplicationUpdateStartResult(false, $"Update could not be started: {ex.Message}", null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ValidationResult ValidateOptions(bool requireScripts)
    {
        var options = NormalizedOptions();
        if (!options.Enabled)
        {
            return ValidationResult.Invalid("Application updates are disabled.");
        }

        if (string.IsNullOrWhiteSpace(options.RepositoryPath))
        {
            return ValidationResult.Invalid("Repository path is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.Remote))
        {
            return ValidationResult.Invalid("Git remote is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.Branch))
        {
            return ValidationResult.Invalid("Git branch is not configured.");
        }

        var repositoryPath = ResolvePath(options.RepositoryPath, _environment.ContentRootPath);
        if (!Directory.Exists(repositoryPath))
        {
            return ValidationResult.Invalid($"Repository path does not exist: {repositoryPath}");
        }

        if (!HasGitMetadata(repositoryPath))
        {
            return ValidationResult.Invalid($"Repository path is not a Git repository: {repositoryPath}");
        }

        var updateScriptPath = ResolvePath(options.UpdateScriptPath, repositoryPath);
        var installScriptPath = ResolvePath(options.InstallScriptPath, repositoryPath);
        if (requireScripts && !File.Exists(updateScriptPath))
        {
            return ValidationResult.Invalid($"Update script was not found: {updateScriptPath}");
        }

        if (requireScripts && !File.Exists(installScriptPath))
        {
            return ValidationResult.Invalid($"Install script was not found: {installScriptPath}");
        }

        var logDirectory = ResolvePath(options.LogDirectory, _environment.ContentRootPath);
        var stamp = _timeProvider
            .GetUtcNow()
            .UtcDateTime
            .ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var logPath = Path.Combine(logDirectory, $"application-update-{stamp}.log");
        var request = new ApplicationUpdateProcessStartRequest(
            options.PowerShellPath,
            updateScriptPath,
            repositoryPath,
            options.Remote,
            options.Branch,
            installScriptPath,
            logPath);
        return ValidationResult.Valid("Ready to update from GitHub.", request);
    }

    private ApplicationUpdateOptions NormalizedOptions()
    {
        var options = _options.Value;
        return new ApplicationUpdateOptions
        {
            Enabled = options.Enabled,
            RepositoryPath = options.RepositoryPath.Trim(),
            Remote = string.IsNullOrWhiteSpace(options.Remote) ? "origin" : options.Remote.Trim(),
            Branch = string.IsNullOrWhiteSpace(options.Branch) ? "main" : options.Branch.Trim(),
            InstallScriptPath = string.IsNullOrWhiteSpace(options.InstallScriptPath)
                ? "Install-PremiereCalendar.ps1"
                : options.InstallScriptPath.Trim(),
            UpdateScriptPath = string.IsNullOrWhiteSpace(options.UpdateScriptPath)
                ? "deploy/Update-And-Install-PremiereCalendar.ps1"
                : options.UpdateScriptPath.Trim(),
            LogDirectory = string.IsNullOrWhiteSpace(options.LogDirectory)
                ? "App_Data/logs/application-updates"
                : options.LogDirectory.Trim(),
            PowerShellPath = string.IsNullOrWhiteSpace(options.PowerShellPath)
                ? "powershell.exe"
                : options.PowerShellPath.Trim()
        };
    }

    private static bool HasGitMetadata(string repositoryPath)
    {
        var gitPath = Path.Combine(repositoryPath, ".git");
        return Directory.Exists(gitPath) || File.Exists(gitPath);
    }

    private static string ResolvePath(string path, string basePath)
    {
        var trimmed = path.Trim();
        return Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(basePath, trimmed));
    }

    private static string? FindLatestLogPath(string logDirectory)
    {
        return Directory.Exists(logDirectory)
            ? Directory
                .EnumerateFiles(logDirectory, "application-update-*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault()
            : null;
    }

    private sealed record ValidationResult(
        bool IsValid,
        string Message,
        ApplicationUpdateProcessStartRequest? Request)
    {
        public static ValidationResult Valid(string message, ApplicationUpdateProcessStartRequest request)
        {
            return new ValidationResult(true, message, request);
        }

        public static ValidationResult Invalid(string message)
        {
            return new ValidationResult(false, message, null);
        }
    }
}

public interface IApplicationUpdateProcessStarter
{
    ApplicationUpdateProcessStartResult Start(ApplicationUpdateProcessStartRequest request);
}

public sealed class DefaultApplicationUpdateProcessStarter : IApplicationUpdateProcessStarter
{
    public ApplicationUpdateProcessStartResult Start(ApplicationUpdateProcessStartRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.PowerShellPath,
            WorkingDirectory = request.RepositoryPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(request.UpdateScriptPath);
        startInfo.ArgumentList.Add("-RepositoryPath");
        startInfo.ArgumentList.Add(request.RepositoryPath);
        startInfo.ArgumentList.Add("-Remote");
        startInfo.ArgumentList.Add(request.Remote);
        startInfo.ArgumentList.Add("-Branch");
        startInfo.ArgumentList.Add(request.Branch);
        startInfo.ArgumentList.Add("-InstallScriptPath");
        startInfo.ArgumentList.Add(request.InstallScriptPath);
        startInfo.ArgumentList.Add("-LogPath");
        startInfo.ArgumentList.Add(request.LogPath);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell update process did not start.");
        var processId = process.Id;
        process.Dispose();
        return new ApplicationUpdateProcessStartResult(processId);
    }
}

public sealed record ApplicationUpdateStatus(
    bool IsEnabled,
    bool IsConfigured,
    string RepositoryPath,
    string Remote,
    string Branch,
    string? LatestLogPath,
    string Message);

public sealed record ApplicationUpdateStartResult(
    bool Started,
    string Message,
    string? LogPath);

public sealed record ApplicationUpdateProcessStartRequest(
    string PowerShellPath,
    string UpdateScriptPath,
    string RepositoryPath,
    string Remote,
    string Branch,
    string InstallScriptPath,
    string LogPath);

public sealed record ApplicationUpdateProcessStartResult(int ProcessId);
