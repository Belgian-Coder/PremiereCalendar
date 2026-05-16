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

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(info => info.Exists)
            .ToArray();

        var lastWriteUtc = files.Length == 0
            ? (DateTimeOffset?)null
            : files.Max(info => new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));

        return new CacheBucketSummary(
            label,
            directory,
            Exists: true,
            files.Length,
            files.Sum(info => info.Length),
            lastWriteUtc);
    }

    private string ResolvePath(string configuredPath)
    {
        return Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configuredPath));
    }
}
