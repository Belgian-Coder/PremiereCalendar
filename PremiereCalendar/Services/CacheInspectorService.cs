using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class CacheInspectorService
{
    private readonly CalendarCacheOptions _calendarOptions;
    private readonly ImageCacheOptions _imageOptions;
    private readonly IWebHostEnvironment _environment;

    public CacheInspectorService(
        IOptions<CalendarCacheOptions> calendarOptions,
        IOptions<ImageCacheOptions> imageOptions,
        IWebHostEnvironment environment)
    {
        _calendarOptions = calendarOptions.Value;
        _imageOptions = imageOptions.Value;
        _environment = environment;
    }

    public CacheInspectorSummary GetSummary()
    {
        return new CacheInspectorSummary(
            Summarize("Calendar", ResolvePath(_calendarOptions.Directory)),
            Summarize("Images", ResolvePath(_imageOptions.Directory)));
    }

    private CacheBucketSummary Summarize(string label, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new CacheBucketSummary(label, directory, Exists: false, FileCount: 0, TotalBytes: 0, LastWriteUtc: null);
        }

        var fileCount = 0;
        var totalBytes = 0L;
        DateTimeOffset? lastWriteUtc = null;

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    fileCount++;
                    totalBytes += info.Length;
                    var writeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    lastWriteUtc = lastWriteUtc is null || writeUtc > lastWriteUtc
                        ? writeUtc
                        : lastWriteUtc;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return new CacheBucketSummary(label, directory, Exists: false, FileCount: 0, TotalBytes: 0, LastWriteUtc: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new CacheBucketSummary(
            label,
            directory,
            Exists: true,
            fileCount,
            totalBytes,
            lastWriteUtc);
    }

    private string ResolvePath(string configuredPath)
    {
        return Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configuredPath));
    }
}
