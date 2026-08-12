using System.Collections.Concurrent;
using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace PremiereCalendar.Services;

public sealed class FileImageCache : IImageCache, IImageCacheMaintenance
{
    private const int StreamBufferSize = 81920;
    private static readonly ConcurrentDictionary<string, CacheKeyGate> CacheKeyLocks = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient _httpClient;
    private readonly ImageCacheOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<FileImageCache> _logger;
    private readonly ProviderRequestThrottler _requestThrottler;
    private readonly SemaphoreSlim _decodeGate;

    public FileImageCache(
        HttpClient httpClient,
        IOptions<ImageCacheOptions> options,
        IWebHostEnvironment environment,
        ILogger<FileImageCache> logger,
        ProviderRequestThrottler? requestThrottler = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
        _requestThrottler = requestThrottler ?? new ProviderRequestThrottler();
        _decodeGate = new SemaphoreSlim(Math.Clamp(_options.MaxConcurrentDecodes, 1, 64));
    }

    public async Task<CachedImage> GetOrAddAsync(
        string sourceUrl,
        bool forceRefresh,
        CancellationToken cancellationToken,
        int? width = null)
    {
        var sourceUri = ValidateSourceUrl(sourceUrl);
        var targetWidth = NormalizeWidth(width);
        var cacheKey = CacheKey(sourceUri, targetWidth);
        var filePath = ImagePath(cacheKey);
        var metadataPath = MetadataPath(cacheKey);
        var browserMaxAge = TimeSpan.FromDays(Math.Max(1, _options.CacheDays));
        var cacheKeyLock = AcquireCacheKeyGate(cacheKey);

        try
        {
            await cacheKeyLock.WaitAsync(cancellationToken);
        }
        catch
        {
            cacheKeyLock.Release(entered: false);
            throw;
        }
        try
        {
            if (!forceRefresh && TryReadFreshMetadata(metadataPath, out var metadata) && File.Exists(filePath))
            {
                return await ReadCachedImageAsync(filePath, metadata, cacheKey, browserMaxAge, cancellationToken);
            }

            try
            {
                var fetched = await FetchImageAsync(sourceUri, cacheKey, filePath, targetWidth, browserMaxAge, cancellationToken);
                await WriteCachedMetadataAsync(metadataPath, sourceUrl, fetched, cancellationToken);
                return fetched;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or ExternalApiException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (TryReadAnyMetadata(metadataPath, out var staleMetadata) && File.Exists(filePath))
                {
                    _logger.LogWarning(ex, "Using stale cached image for {ImageUrl}.", sourceUrl);
                    return await ReadCachedImageAsync(filePath, staleMetadata, cacheKey, browserMaxAge, cancellationToken);
                }

                throw;
            }
        }
        finally
        {
            cacheKeyLock.Release(entered: true);
        }
    }

    public Task<int> CleanupAsync(
        DateTimeOffset nowUtc,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        var directory = CacheDirectory();
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(0);
        }

        var cutoffUtc = nowUtc - retention;
        var removed = 0;
        var removedImageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var metadataPath in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cacheKey = Path.GetFileNameWithoutExtension(metadataPath);
            var imagePath = ImagePath(cacheKey);
            if (!TryReadAnyMetadata(metadataPath, out var metadata))
            {
                if (File.GetLastWriteTimeUtc(metadataPath) >= cutoffUtc.UtcDateTime)
                {
                    continue;
                }

                TryDeleteFile(metadataPath);
                TryDeleteFile(imagePath);
                removedImageKeys.Add(cacheKey);
                removed++;
                continue;
            }

            if (metadata.FetchedUtc >= cutoffUtc)
            {
                continue;
            }

            TryDeleteFile(metadataPath);
            TryDeleteFile(imagePath);
            removedImageKeys.Add(cacheKey);
            removed++;
        }

        foreach (var tempPath in Directory.EnumerateFiles(directory, "*.tmp"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.GetLastWriteTimeUtc(tempPath) >= cutoffUtc.UtcDateTime)
            {
                continue;
            }

            TryDeleteFile(tempPath);
            removed++;
        }

        foreach (var imagePath in Directory.EnumerateFiles(directory, "*.bin"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cacheKey = Path.GetFileNameWithoutExtension(imagePath);
            if (removedImageKeys.Contains(cacheKey))
            {
                continue;
            }

            var lastWriteUtc = File.GetLastWriteTimeUtc(imagePath);
            if (lastWriteUtc < cutoffUtc.UtcDateTime
                && !File.Exists(MetadataPath(cacheKey)))
            {
                TryDeleteFile(imagePath);
                removed++;
            }
        }

        return Task.FromResult(removed);
    }

    private Uri ValidateSourceUrl(string sourceUrl)
    {
        if (!_options.Enabled)
        {
            throw new ExternalApiException("Image cache is disabled.");
        }

        if (!ImageSourceUrlPolicy.TryCreateAllowedUri(sourceUrl, _options.AllowedHosts, out var uri))
        {
            throw new ArgumentException("Image cache only accepts absolute HTTPS image URLs from allowed hosts.", nameof(sourceUrl));
        }

        return uri;
    }

    private bool TryReadFreshMetadata(string metadataPath, out ImageCacheMetadata metadata)
    {
        if (!TryReadAnyMetadata(metadataPath, out metadata))
        {
            return false;
        }

        var maxAge = TimeSpan.FromDays(Math.Max(1, _options.CacheDays));
        return DateTimeOffset.UtcNow - metadata.FetchedUtc <= maxAge;
    }

    private static bool TryReadAnyMetadata(string metadataPath, out ImageCacheMetadata metadata)
    {
        metadata = default!;

        if (!File.Exists(metadataPath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(metadataPath);
            var value = JsonSerializer.Deserialize<ImageCacheMetadata>(json, JsonOptions);
            if (value is null || string.IsNullOrWhiteSpace(value.ContentType))
            {
                return false;
            }

            metadata = value;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<CachedImage> FetchImageAsync(
        Uri sourceUri,
        string cacheKey,
        string filePath,
        int? targetWidth,
        TimeSpan browserMaxAge,
        CancellationToken cancellationToken)
    {
        using var lease = await _requestThrottler.AcquireAsync(
            "images",
            _options.MaxConcurrentDownloads,
            cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new ExternalApiException($"Image fetch failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var maxBytes = Math.Max(1024, _options.MaxBytes);
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 0 && contentLength > maxBytes)
        {
            throw new ExternalApiException(
                string.Create(CultureInfo.InvariantCulture, $"Image exceeds cache size limit of {maxBytes} bytes."));
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExternalApiException("Image fetch did not return an image content type.");
        }

        var cachedContentType = targetWidth is > 0
            ? CanUseRemoteImageAsRequestedWidth(sourceUri, targetWidth.Value)
                ? await WriteLimitedContentAsync(response.Content, filePath, cancellationToken) ?? contentType
                : await WriteResizedContentAsync(response.Content, filePath, targetWidth.Value, cancellationToken)
            : await WriteLimitedContentAsync(response.Content, filePath, cancellationToken);

        return new CachedImage(
            filePath,
            cachedContentType ?? contentType,
            DateTimeOffset.UtcNow,
            cacheKey,
            browserMaxAge);
    }

    private async Task<string?> WriteLimitedContentAsync(
        HttpContent content,
        string filePath,
        CancellationToken cancellationToken)
    {
        var maxBytes = Math.Max(1024, _options.MaxBytes);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempImagePath = $"{filePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await WriteLimitedResponseContentToFileAsync(content, tempImagePath, maxBytes, cancellationToken);
            File.Move(tempImagePath, filePath, overwrite: true);
            return null;
        }
        catch
        {
            TryDeleteFile(tempImagePath);
            throw;
        }
    }

    private async Task<string> WriteResizedContentAsync(
        HttpContent content,
        string filePath,
        int targetWidth,
        CancellationToken cancellationToken)
    {
        var maxBytes = Math.Max(1024, _options.MaxBytes);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempSourcePath = $"{filePath}.{Guid.NewGuid():N}.source.tmp";
        var tempImagePath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteLimitedResponseContentToFileAsync(content, tempSourcePath, maxBytes, cancellationToken);

            await _decodeGate.WaitAsync(cancellationToken);
            try
            {
                await using var sourceFile = File.OpenRead(tempSourcePath);
                using var image = await Image.LoadAsync(sourceFile, cancellationToken);
                if (image.Width > targetWidth)
                {
                    image.Mutate(operation => operation.Resize(new ResizeOptions
                    {
                        Size = new Size(targetWidth, 0),
                        Mode = ResizeMode.Max
                    }));
                }

                await using (var target = File.Create(tempImagePath))
                {
                    await image.SaveAsJpegAsync(target, new JpegEncoder { Quality = 82 }, cancellationToken);
                }
            }
            finally
            {
                _decodeGate.Release();
            }

            File.Move(tempImagePath, filePath, overwrite: true);
            return "image/jpeg";
        }
        catch
        {
            TryDeleteFile(tempImagePath);
            throw;
        }
        finally
        {
            TryDeleteFile(tempSourcePath);
        }
    }

    private static async Task WriteLimitedResponseContentToFileAsync(
        HttpContent content,
        string filePath,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(filePath);
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            long bytesWritten = 0;

            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                bytesWritten += read;
                if (bytesWritten > maxBytes)
                {
                    throw new ExternalApiException(
                        string.Create(CultureInfo.InvariantCulture, $"Image exceeds cache size limit of {maxBytes} bytes."));
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteCachedMetadataAsync(
        string metadataPath,
        string sourceUrl,
        CachedImage image,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(metadataPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var metadata = new ImageCacheMetadata(sourceUrl, image.ContentType, image.LastModifiedUtc);
        var tempMetadataPath = $"{metadataPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(
            tempMetadataPath,
            JsonSerializer.Serialize(metadata, JsonOptions),
            cancellationToken);
        File.Move(tempMetadataPath, metadataPath, overwrite: true);
    }

    private static async Task<CachedImage> ReadCachedImageAsync(
        string filePath,
        ImageCacheMetadata metadata,
        string cacheKey,
        TimeSpan browserMaxAge,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new CachedImage(
            filePath,
            metadata.ContentType,
            metadata.FetchedUtc,
            cacheKey,
            browserMaxAge);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string ImagePath(string cacheKey)
    {
        return Path.Combine(CacheDirectory(), $"{cacheKey}.bin");
    }

    private string MetadataPath(string cacheKey)
    {
        return Path.Combine(CacheDirectory(), $"{cacheKey}.json");
    }

    private string CacheDirectory()
    {
        return Path.IsPathRooted(_options.Directory)
            ? _options.Directory
            : Path.Combine(_environment.ContentRootPath, _options.Directory);
    }

    private static int? NormalizeWidth(int? width)
    {
        return width is > 0
            ? Math.Clamp(width.Value, 64, 640)
            : null;
    }

    private static bool CanUseRemoteImageAsRequestedWidth(Uri sourceUri, int targetWidth)
    {
        if (!string.Equals(sourceUri.Host, "image.tmdb.org", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = sourceUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Any(segment =>
            segment.Length > 1
            && segment[0] == 'w'
            && int.TryParse(segment[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var remoteWidth)
            && remoteWidth <= targetWidth);
    }

    private static string CacheKey(Uri sourceUri, int? width)
    {
        var normalizedUrl = width is > 0
            ? $"{sourceUri.AbsoluteUri}|w:{width.Value}"
            : sourceUri.AbsoluteUri;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static CacheKeyGateLease AcquireCacheKeyGate(string cacheKey)
    {
        while (true)
        {
            var gate = CacheKeyLocks.GetOrAdd(cacheKey, static _ => new CacheKeyGate());
            if (gate.TryAcquireReference())
            {
                return new CacheKeyGateLease(cacheKey, gate);
            }

            CacheKeyLocks.TryRemove(new KeyValuePair<string, CacheKeyGate>(cacheKey, gate));
        }
    }

    private sealed class CacheKeyGate
    {
        private int _references;
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public bool TryAcquireReference()
        {
            while (true)
            {
                var current = Volatile.Read(ref _references);
                if (current < 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

        public bool IsRetired => Volatile.Read(ref _references) < 0;

        public void Release()
        {
            ReleaseReference();
            _semaphore.Release();
        }

        public void ReleaseReference()
        {
            while (true)
            {
                var current = Volatile.Read(ref _references);
                if (current <= 0) return;
                var next = current == 1 ? -1 : current - 1;
                if (Interlocked.CompareExchange(ref _references, next, current) == current) return;
            }
        }
    }

    private sealed class CacheKeyGateLease(string cacheKey, CacheKeyGate gate)
    {
        public Task WaitAsync(CancellationToken cancellationToken) => gate.WaitAsync(cancellationToken);
        public void Release(bool entered)
        {
            if (entered)
            {
                gate.Release();
            }
            else
            {
                gate.ReleaseReference();
            }
            if (gate.IsRetired)
            {
                CacheKeyLocks.TryRemove(new KeyValuePair<string, CacheKeyGate>(cacheKey, gate));
            }
        }
    }

    private sealed record ImageCacheMetadata(
        string SourceUrl,
        string ContentType,
        DateTimeOffset FetchedUtc);
}
