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
            TryGit(repositoryPath, "rev-parse", "HEAD"),
            TryGit(repositoryPath, "rev-parse", $"{options.Remote}/{options.Branch}"),
            IsRepositoryDirty(repositoryPath),
            ReadLogTail(latestLogPath),
            LastLogResult(latestLogPath),
            LastBackupPath(latestLogPath),
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
            logPath,
            ResolvePath(options.TargetDirectory, _environment.ContentRootPath),
            ResolvePath(options.BackupDirectory, _environment.ContentRootPath),
            options.HealthUrl,
            options.RollbackOnFailure);
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
                : options.PowerShellPath.Trim(),
            TargetDirectory = string.IsNullOrWhiteSpace(options.TargetDirectory)
                ? "D:\\Apps\\PremiereCalendar"
                : options.TargetDirectory.Trim(),
            BackupDirectory = string.IsNullOrWhiteSpace(options.BackupDirectory)
                ? "D:\\Apps\\PremiereCalendar\\App_Data\\backups\\application-updates"
                : options.BackupDirectory.Trim(),
            HealthUrl = string.IsNullOrWhiteSpace(options.HealthUrl)
                ? "http://localhost:5298/health"
                : options.HealthUrl.Trim(),
            RollbackOnFailure = options.RollbackOnFailure
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

    private static string? TryGit(string repositoryPath, params string[] arguments)
    {
        if (!Directory.Exists(repositoryPath))
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"safe.directory={repositoryPath}");
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    private static bool? IsRepositoryDirty(string repositoryPath)
    {
        var status = TryGit(repositoryPath, "status", "--porcelain=v1");
        return status is null ? null : !string.IsNullOrWhiteSpace(status);
    }

    private static string? ReadLogTail(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return null;
        }

        try
        {
            return string.Join(
                Environment.NewLine,
                File.ReadLines(logPath).Reverse().Take(12).Reverse());
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? LastLogResult(string? logPath)
    {
        var tail = ReadLogTail(logPath);
        if (string.IsNullOrWhiteSpace(tail))
        {
            return null;
        }

        if (tail.Contains("Update completed successfully", StringComparison.OrdinalIgnoreCase))
        {
            return "Succeeded";
        }

        if (tail.Contains("Rollback completed", StringComparison.OrdinalIgnoreCase))
        {
            return "Rolled back";
        }

        return tail.Contains("failed", StringComparison.OrdinalIgnoreCase) ? "Failed" : null;
    }

    private static string? LastBackupPath(string? logPath)
    {
        var tail = ReadLogTail(logPath);
        if (string.IsNullOrWhiteSpace(tail))
        {
            return null;
        }

        const string marker = "Backup snapshot:";
        var line = tail
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(candidate => candidate.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return null;
        }

        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : line[(index + marker.Length)..].Trim();
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
        startInfo.ArgumentList.Add("-TargetDirectory");
        startInfo.ArgumentList.Add(request.TargetDirectory);
        startInfo.ArgumentList.Add("-BackupDirectory");
        startInfo.ArgumentList.Add(request.BackupDirectory);
        startInfo.ArgumentList.Add("-HealthUrl");
        startInfo.ArgumentList.Add(request.HealthUrl);
        if (!request.RollbackOnFailure)
        {
            startInfo.ArgumentList.Add("-RollbackOnFailure:$false");
        }

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
    string? CurrentCommit,
    string? RemoteCommit,
    bool? IsRepositoryDirty,
    string? LatestLogTail,
    string? LastResult,
    string? LastBackupPath,
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
    string LogPath,
    string TargetDirectory,
    string BackupDirectory,
    string HealthUrl,
    bool RollbackOnFailure);

public sealed record ApplicationUpdateProcessStartResult(int ProcessId);
