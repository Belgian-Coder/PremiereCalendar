using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class ImdbDatasetImporter : IImdbDatasetImporter
{
    private const int DownloadBufferSize = 64 * 1024;
    private static readonly TimeSpan StaleDownloadAge = TimeSpan.FromHours(24);
    private const string DownloadFilePrefix = "imdb-ratings-";
    private const string DownloadFileSuffix = ".tmp";

    private readonly HttpClient _httpClient;
    private readonly IImdbRatingsStore _ratingsStore;
    private readonly ImdbDatasetOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImdbDatasetImporter> _logger;
    private readonly string _downloadDirectory;

    public ImdbDatasetImporter(
        HttpClient httpClient,
        IImdbRatingsStore ratingsStore,
        IOptions<ImdbDatasetOptions> options,
        TimeProvider timeProvider,
        ILogger<ImdbDatasetImporter> logger,
        IWebHostEnvironment? environment = null)
    {
        _httpClient = httpClient;
        _ratingsStore = ratingsStore;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _downloadDirectory = ResolveDownloadDirectory(_options.DownloadDirectory, environment);
    }

    public async Task<int> ImportRatingsAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        var importedAtUtc = _timeProvider.GetUtcNow();
        try
        {
            if (_options.MaxDownloadBytes <= 0)
            {
                throw new InvalidOperationException("IMDb dataset MaxDownloadBytes must be greater than zero.");
            }

            RemoveStaleDownloadFiles(cancellationToken);
            await using var compressedDataset = CreateCompressedDatasetFile();
            await DownloadCompressedDatasetAsync(compressedDataset, _options.MaxDownloadBytes, cancellationToken);

            // The HTTP response owns the adaptive provider lease and its total deadline.
            // It is disposed after the compressed file reaches EOF, before slower database imports begin.
            compressedDataset.Position = 0;
            using var gzip = new GZipStream(compressedDataset, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new StreamReader(gzip);
            var count = await _ratingsStore.ReplaceAllStreamingAsync(
                ReadRatingsAsync(reader, importedAtUtc, cancellationToken),
                importedAtUtc,
                cancellationToken);
            _logger.LogInformation("Imported {Count} IMDb title ratings.", count);
            return count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "IMDb ratings dataset import failed.");
            try
            {
                var state = await _ratingsStore.GetStateAsync(CancellationToken.None);
                await _ratingsStore.SaveStateAsync(
                    state with { LastError = ex.Message },
                    CancellationToken.None);
            }
            catch (Exception stateException)
            {
                _logger.LogWarning(stateException, "Could not save IMDb dataset import failure state.");
            }

            throw;
        }
    }

    private async Task DownloadCompressedDatasetAsync(
        FileStream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            _options.RatingsUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"IMDb dataset download exceeded the configured {maximumBytes} byte limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSize);
        try
        {
            long bytesCopied = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, DownloadBufferSize), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (bytesCopied > maximumBytes - read)
                {
                    throw new InvalidDataException(
                        $"IMDb dataset download exceeded the configured {maximumBytes} byte limit.");
                }

                bytesCopied += read;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await destination.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private FileStream CreateCompressedDatasetFile()
    {
        Directory.CreateDirectory(_downloadDirectory);
        var path = Path.Combine(_downloadDirectory, $"{DownloadFilePrefix}{Guid.NewGuid():N}{DownloadFileSuffix}");
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            DownloadBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
    }

    private void RemoveStaleDownloadFiles(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_downloadDirectory))
        {
            return;
        }

        var cutoffUtc = (_timeProvider.GetUtcNow() - StaleDownloadAge).UtcDateTime;
        try
        {
            foreach (var path in Directory.EnumerateFiles(_downloadDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsOwnedDownloadFile(path))
                {
                    continue;
                }

                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoffUtc)
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "Could not remove stale IMDb dataset scratch file {Path}.", path);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogDebug(ex, "Could not remove stale IMDb dataset scratch file {Path}.", path);
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not enumerate IMDb dataset scratch files in {Directory}.", _downloadDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Could not enumerate IMDb dataset scratch files in {Directory}.", _downloadDirectory);
        }
    }

    private static bool IsOwnedDownloadFile(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.Length != DownloadFilePrefix.Length + 32 + DownloadFileSuffix.Length
            || !fileName.StartsWith(DownloadFilePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(DownloadFileSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var id = fileName.Substring(DownloadFilePrefix.Length, 32);
        return Guid.TryParseExact(id, "N", out _);
    }

    private static string ResolveDownloadDirectory(string? configuredPath, IWebHostEnvironment? environment)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? "App_Data/tmp/imdb" : configuredPath;
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var root = string.IsNullOrWhiteSpace(environment?.ContentRootPath)
            ? Path.Combine(Path.GetTempPath(), "PremiereCalendar")
            : environment.ContentRootPath;
        return Path.GetFullPath(Path.Combine(root, path));
    }

    private static async IAsyncEnumerable<ImdbRatingRecord> ReadRatingsAsync(
        StreamReader reader,
        DateTimeOffset importedAtUtc,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var isHeader = true;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (isHeader)
            {
                isHeader = false;
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 3
                || string.IsNullOrWhiteSpace(parts[0])
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var averageRating)
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var voteCount))
            {
                continue;
            }

            yield return new ImdbRatingRecord(parts[0].Trim(), averageRating, voteCount, importedAtUtc);
        }
    }
}
