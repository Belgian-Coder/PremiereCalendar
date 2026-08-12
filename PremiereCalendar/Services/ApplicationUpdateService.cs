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
        var validation = ValidateOptions(requireScript: true);
        var options = NormalizedOptions();
        var updaterScriptPath = ResolvePath(options.UpdaterScriptPath, _environment.ContentRootPath);
        var installRoot = ResolvePath(options.InstallRoot, _environment.ContentRootPath);
        var dataRoot = ResolvePath(options.DataRoot, _environment.ContentRootPath);
        var latestLogPath = FindLatestLogPath(ResolvePath(options.LogDirectory, dataRoot));
        var activeVersionPath = Path.Combine(installRoot, "updater", "active-version.txt");
        return new ApplicationUpdateStatus(
            options.Enabled,
            validation.IsValid,
            updaterScriptPath,
            installRoot,
            dataRoot,
            options.Repository,
            latestLogPath,
            ReadSingleLine(activeVersionPath),
            ReadLogTail(latestLogPath),
            LastLogResult(latestLogPath),
            validation.Message);
    }

    public async Task<ApplicationUpdateStartResult> StartUpdateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var validation = ValidateOptions(requireScript: true);
            if (!validation.IsValid || validation.Request is null)
            {
                return new ApplicationUpdateStartResult(false, validation.Message, null);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(validation.Request.LogPath)!);
            _processStarter.Start(validation.Request);
            return new ApplicationUpdateStartResult(
                true,
                "Signed GitHub release update started. The app will reconnect after a verified update, or remain on the current version when already up to date.",
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

    private ValidationResult ValidateOptions(bool requireScript)
    {
        var options = NormalizedOptions();
        if (!options.Enabled)
        {
            return ValidationResult.Invalid("Application updates are disabled.");
        }

        if (!IsValidRepository(options.Repository))
        {
            return ValidationResult.Invalid("The GitHub repository must use the owner/name format.");
        }

        var updaterScriptPath = ResolvePath(options.UpdaterScriptPath, _environment.ContentRootPath);
        if (requireScript && !File.Exists(updaterScriptPath))
        {
            return ValidationResult.Invalid($"The signed GitHub updater was not found: {updaterScriptPath}");
        }

        var installRoot = ResolvePath(options.InstallRoot, _environment.ContentRootPath);
        var dataRoot = ResolvePath(options.DataRoot, _environment.ContentRootPath);
        var logDirectory = ResolvePath(options.LogDirectory, dataRoot);
        var stamp = _timeProvider
            .GetUtcNow()
            .UtcDateTime
            .ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var logPath = Path.Combine(logDirectory, $"application-update-{stamp}.log");
        var request = new ApplicationUpdateProcessStartRequest(
            options.PowerShellPath,
            updaterScriptPath,
            options.Repository,
            installRoot,
            dataRoot,
            logPath);
        return ValidationResult.Valid("Ready to install signed GitHub releases.", request);
    }

    private ApplicationUpdateOptions NormalizedOptions()
    {
        var options = _options.Value;
        return new ApplicationUpdateOptions
        {
            Enabled = options.Enabled,
            UpdaterScriptPath = string.IsNullOrWhiteSpace(options.UpdaterScriptPath)
                ? "D:\\Apps\\PremiereCalendar\\updater\\install-github-release.ps1"
                : options.UpdaterScriptPath.Trim(),
            LogDirectory = string.IsNullOrWhiteSpace(options.LogDirectory)
                ? "D:\\Apps\\PremiereCalendarData\\logs\\application-updates"
                : options.LogDirectory.Trim(),
            PowerShellPath = string.IsNullOrWhiteSpace(options.PowerShellPath)
                ? "powershell.exe"
                : options.PowerShellPath.Trim(),
            InstallRoot = string.IsNullOrWhiteSpace(options.InstallRoot)
                ? "D:\\Apps\\PremiereCalendar"
                : options.InstallRoot.Trim(),
            DataRoot = string.IsNullOrWhiteSpace(options.DataRoot)
                ? "D:\\Apps\\PremiereCalendarData"
                : options.DataRoot.Trim(),
            Repository = string.IsNullOrWhiteSpace(options.Repository)
                ? "Belgian-Coder/PremiereCalendar"
                : options.Repository.Trim()
        };
    }

    private static bool IsValidRepository(string repository)
    {
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts.All(part => part.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
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

    private static string? ReadSingleLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? ReadLogTail(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return null;
        }

        try
        {
            return string.Join(Environment.NewLine, File.ReadLines(logPath).Reverse().Take(12).Reverse());
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

        if (tail.Contains("installed and healthy", StringComparison.OrdinalIgnoreCase))
        {
            return "Succeeded";
        }

        if (tail.Contains("already the latest stable release", StringComparison.OrdinalIgnoreCase))
        {
            return "Already current";
        }

        if (tail.Contains("Rollback completed", StringComparison.OrdinalIgnoreCase))
        {
            return "Rolled back";
        }

        return tail.Contains("failed", StringComparison.OrdinalIgnoreCase) || tail.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            ? "Failed"
            : "Running";
    }

    private sealed record ValidationResult(bool IsValid, string Message, ApplicationUpdateProcessStartRequest? Request)
    {
        public static ValidationResult Valid(string message, ApplicationUpdateProcessStartRequest request) => new(true, message, request);

        public static ValidationResult Invalid(string message) => new(false, message, null);
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
            WorkingDirectory = Path.GetDirectoryName(request.UpdaterScriptPath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(request.UpdaterScriptPath);
        startInfo.ArgumentList.Add("-Repository");
        startInfo.ArgumentList.Add(request.Repository);
        startInfo.ArgumentList.Add("-InstallRoot");
        startInfo.ArgumentList.Add(request.InstallRoot);
        startInfo.ArgumentList.Add("-DataRoot");
        startInfo.ArgumentList.Add(request.DataRoot);
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
    string UpdaterScriptPath,
    string InstallRoot,
    string DataRoot,
    string Repository,
    string? LatestLogPath,
    string? ActiveVersion,
    string? LatestLogTail,
    string? LastResult,
    string Message);

public sealed record ApplicationUpdateStartResult(bool Started, string Message, string? LogPath);

public sealed record ApplicationUpdateProcessStartRequest(
    string PowerShellPath,
    string UpdaterScriptPath,
    string Repository,
    string InstallRoot,
    string DataRoot,
    string LogPath);

public sealed record ApplicationUpdateProcessStartResult(int ProcessId);
