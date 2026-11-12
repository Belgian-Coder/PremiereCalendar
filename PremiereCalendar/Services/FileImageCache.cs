using System.Collections.Concurrent;
using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using ImageMagick;

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
    private readonly ImageDecodeLimiter _decodeLimiter;
    private readonly ImageRefreshQueue? _refreshQueue;

    public FileImageCache(
        HttpClient httpClient,
        IOptions<ImageCacheOptions> options,
        IWebHostEnvironment environment,
        ILogger<FileImageCache> logger,
        ProviderRequestThrottler? requestThrottler = null,
        ImageDecodeLimiter? decodeLimiter = null,
        ImageRefreshQueue? refreshQueue = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
        _requestThrottler = requestThrottler ?? new ProviderRequestThrottler();
        _decodeLimiter = decodeLimiter ?? new ImageDecodeLimiter(options);
        _refreshQueue = refreshQueue;
    }

    public async Task<CachedImage> GetOrAddAsync(
        string sourceUrl,
        bool forceRefresh,
        CancellationToken cancellationToken,
        int? width = null,
        ImageCacheFormat format = ImageCacheFormat.Original)
    {
        return await GetOrAddCoreAsync(sourceUrl, forceRefresh, cancellationToken, width, format, allowBackgroundRefresh: true);
    }

    private async Task<CachedImage> GetOrAddCoreAsync(
        string sourceUrl, bool forceRefresh, CancellationToken cancellationToken,
        int? width, ImageCacheFormat format, bool allowBackgroundRefresh)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceUri = ValidateSourceUrl(sourceUrl);
        var targetWidth = NormalizeWidth(width);
        format = targetWidth is > 0 ? format : ImageCacheFormat.Original;
        var cacheKey = CacheKey(sourceUri, targetWidth, format);
        var metadataPath = MetadataPath(cacheKey);
        var browserMaxAge = TimeSpan.FromDays(Math.Max(1, _options.CacheDays));
        // Immutable generation paths can be served without waiting for a refresh that
        // already owns this key. Legacy entries still migrate under the key gate below.
        if (!forceRefresh && allowBackgroundRefresh && _refreshQueue is not null
            && TryReadAnyMetadata(metadataPath, out var available)
            && available.BodyGenerationVersion == BodyGenerationVersion
            && TryGetBodyPath(cacheKey, available, out var availablePath))
        {
            var stale = !IsFresh(available);
            if (stale)
            {
                _refreshQueue.TryEnqueue(metadataPath, token => GetOrAddCoreAsync(
                    sourceUrl, false, token, width, format, allowBackgroundRefresh: false));
            }
            return await ReadCachedImageAsync(availablePath, available, metadataPath, cacheKey,
                stale ? TimeSpan.FromMinutes(1) : browserMaxAge, cancellationToken, stale);
        }
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
            using var operationTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 1, 300)));
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                operationTimeout.Token);
            var operationToken = operationCancellation.Token;

            try
            {
                if (!forceRefresh && TryReadFreshMetadata(metadataPath, out var metadata)
                    && TryGetBodyPath(cacheKey, metadata, out var cachedPath))
                {
                    return await ReadCachedImageAsync(cachedPath, metadata, metadataPath, cacheKey, browserMaxAge, operationToken);
                }

                var fetched = await FetchImageAsync(
                    sourceUri,
                    cacheKey,
                    targetWidth,
                    format,
                    browserMaxAge,
                    operationToken);
                // Retention starts when a generation is retired, not when its
                // unchanged bytes were first downloaded. A lookup may still be
                // about to open the old path after this metadata pointer moves.
                if (TryReadAnyMetadata(metadataPath, out var previousMetadata)
                    && TryGetBodyPath(cacheKey, previousMetadata, out var previousPath)
                    && !string.Equals(previousPath, fetched.FilePath, StringComparison.Ordinal))
                {
                    try { File.SetLastWriteTimeUtc(previousPath, DateTime.UtcNow); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "Could not extend retired image retention for {ImagePath}.", previousPath);
                    }
                }
                await WriteCachedMetadataAsync(metadataPath, sourceUrl, fetched, operationToken);
                return fetched;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or ExternalApiException
                or UnauthorizedAccessException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (TryReadAnyMetadata(metadataPath, out var staleMetadata)
                    && TryGetBodyPath(cacheKey, staleMetadata, out var stalePath))
                {
                    _logger.LogWarning(ex, "Using stale cached image for {ImageUrl}.", sourceUrl);
                    var staleImage = await ReadCachedImageAsync(
                        stalePath,
                        staleMetadata,
                        metadataPath,
                        cacheKey,
                        browserMaxAge,
                        cancellationToken,
                        isStale: true);
                    return staleImage with { BrowserMaxAge = TimeSpan.FromMinutes(1) };
                }

                throw;
            }
        }
        finally
        {
            cacheKeyLock.Release(entered: true);
        }
    }

    public async Task<int> CleanupAsync(
        DateTimeOffset nowUtc,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        var directory = CacheDirectory();
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var cutoffUtc = nowUtc - retention;
        var removed = 0;
        var liveBodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expiredMetadataKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var metadataPath in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cacheKey = Path.GetFileNameWithoutExtension(metadataPath);
            if (!IsValidCacheKey(cacheKey))
            {
                continue;
            }

            var gate = AcquireCacheKeyGate(cacheKey);
            await WaitForGateAsync(gate, cancellationToken);
            try
            {
                if (!TryReadAnyMetadata(metadataPath, out var metadata))
                {
                    if (File.GetLastWriteTimeUtc(metadataPath) < cutoffUtc.UtcDateTime)
                    {
                        TryDeleteFile(metadataPath);
                        expiredMetadataKeys.Add(cacheKey);
                        removed++;
                    }

                    continue;
                }

                if (metadata.FetchedUtc < cutoffUtc)
                {
                    TryDeleteFile(metadataPath);
                    expiredMetadataKeys.Add(cacheKey);
                    removed++;
                    continue;
                }

                if (TryGetBodyPath(cacheKey, metadata, out var bodyPath))
                {
                    liveBodies.Add(Path.GetFullPath(bodyPath));
                }
            }
            finally
            {
                gate.Release(entered: true);
            }
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

            if (!TryGetCacheKeyFromBodyPath(imagePath, out var cacheKey))
            {
                continue;
            }

            var gate = AcquireCacheKeyGate(cacheKey);
            await WaitForGateAsync(gate, cancellationToken);
            try
            {
                if (liveBodies.Contains(Path.GetFullPath(imagePath)))
                {
                    continue;
                }

                if (TryReadAnyMetadata(MetadataPath(cacheKey), out var currentMetadata)
                    && TryGetBodyPath(cacheKey, currentMetadata, out var currentBody)
                    && string.Equals(Path.GetFullPath(currentBody), Path.GetFullPath(imagePath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.GetLastWriteTimeUtc(imagePath) < cutoffUtc.UtcDateTime)
                {
                    TryDeleteFile(imagePath);
                    if (!expiredMetadataKeys.Contains(cacheKey))
                    {
                        removed++;
                    }
                }
            }
            finally
            {
                gate.Release(entered: true);
            }
        }

        return removed;
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

        return IsFresh(metadata);
    }

    private bool IsFresh(ImageCacheMetadata metadata)
        => DateTimeOffset.UtcNow - metadata.FetchedUtc <= TimeSpan.FromDays(Math.Max(1, _options.CacheDays));

    private static bool TryReadAnyMetadata(string metadataPath, out ImageCacheMetadata metadata)
    {
        metadata = default!;

        if (!File.Exists(metadataPath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                metadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.SequentialScan);
            if (stream.Length > 64 * 1024)
            {
                return false;
            }

            var value = JsonSerializer.Deserialize<ImageCacheMetadata>(stream, JsonOptions);
            if (value is null
                || string.IsNullOrWhiteSpace(value.ContentType)
                || !IsCacheableRasterContentType(value.ContentType)
                || value.FetchedUtc == default)
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
        int? targetWidth,
        ImageCacheFormat format,
        TimeSpan browserMaxAge,
        CancellationToken cancellationToken)
    {
        var needsResize = targetWidth is > 0
            && !(format == ImageCacheFormat.Original && CanUseRemoteImageAsRequestedWidth(sourceUri, targetWidth.Value));
        var stagedBodyPath = StagedImagePath(cacheKey);
        IDisposable? stagingLease = needsResize
            ? await _decodeLimiter.AcquireStagingSlotAsync(cancellationToken)
            : null;
        try
        {
            using (stagingLease)
            {
                var lease = await _requestThrottler.AcquireAsync(
                    "images",
                    _options.MaxConcurrentDownloads,
                    cancellationToken);
                using (lease)
                {
                    using var response = await SendImageRequestAsync(sourceUri, format, cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        var message = $"Image fetch failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.";
                        if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                        {
                            throw new HttpRequestException(message, inner: null, response.StatusCode);
                        }
                        throw new ExternalApiException(message);
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

                    string cachedContentType;
                    if (needsResize)
                    {
                        cachedContentType = await WriteResizedContentAsync(
                            response.Content,
                            response,
                            stagedBodyPath,
                            targetWidth!.Value,
                            format,
                            contentType,
                            sourceUri.AbsolutePath,
                            lease,
                            cancellationToken);
                    }
                    else
                    {
                        await WriteLimitedContentAsync(response.Content, response, stagedBodyPath, cancellationToken);
                        lease.Dispose();
                        // Verbatim bytes are only ever stored (and later served from this origin) when they are
                        // a real raster image; the upstream header alone is not trusted.
                        cachedContentType = await SniffRasterContentTypeAsync(stagedBodyPath, cancellationToken)
                            ?? throw new ExternalApiException("Image response is not a supported raster image (jpeg, png, gif, webp or avif).");
                    }

                    var fetchedUtc = DateTimeOffset.UtcNow;
                    var contentHash = await ComputeContentHashAsync(stagedBodyPath, cancellationToken);
                    var generationPath = GenerationImagePath(cacheKey, contentHash);
                    PublishImmutableBody(stagedBodyPath, generationPath);
                    return new CachedImage(generationPath, cachedContentType, fetchedUtc, cacheKey, browserMaxAge, contentHash);
                }
            }
        }
        catch
        {
            TryDeleteFile(stagedBodyPath);
            throw;
        }
    }

    private const int MaxImageRedirects = 3;

    private async Task<HttpResponseMessage> SendImageRequestAsync(
        Uri sourceUri, ImageCacheFormat format, CancellationToken cancellationToken)
    {
        var current = sourceUri;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (format == ImageCacheFormat.Webp)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
            }
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));

            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            try
            {
                // Covers a handler that already followed redirects: where the request ended must still be allowed.
                var finalUri = response.RequestMessage?.RequestUri ?? current;
                if (!ImageSourceUrlPolicy.TryCreateAllowedUri(finalUri.AbsoluteUri, _options.AllowedHosts, out _))
                {
                    throw new ExternalApiException("Image fetch was redirected outside the allowed image hosts.");
                }

                if (response.StatusCode is System.Net.HttpStatusCode.MovedPermanently
                    or System.Net.HttpStatusCode.Found
                    or System.Net.HttpStatusCode.SeeOther
                    or System.Net.HttpStatusCode.TemporaryRedirect
                    or System.Net.HttpStatusCode.PermanentRedirect
                    && response.Headers.Location is { } location)
                {
                    if (hop >= MaxImageRedirects)
                    {
                        throw new ExternalApiException("Image fetch followed too many redirects.");
                    }

                    var next = location.IsAbsoluteUri ? location : new Uri(finalUri, location);
                    if (!ImageSourceUrlPolicy.TryCreateAllowedUri(next.AbsoluteUri, _options.AllowedHosts, out var allowed))
                    {
                        throw new ExternalApiException("Image fetch was redirected outside the allowed image hosts.");
                    }

                    current = allowed;
                    response.Dispose();
                    continue;
                }

                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    private static async Task<string?> SniffRasterContentTypeAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[64];
        int read;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            StreamBufferSize, FileOptions.Asynchronous))
        {
            read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        }

        return SniffRasterContentType(header.AsSpan(0, read));
    }

    internal static string? SniffRasterContentType(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return "image/jpeg";
        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (header.Length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (header.Length >= 12 && header[4..8].SequenceEqual("ftyp"u8) && IsAvifBrandSet(header)) return "image/avif";
        return null;
    }

    // The major brand (or a compatible brand) must be avif/avis. A bare mif1 is also what HEIC files declare.
    private static bool IsAvifBrandSet(ReadOnlySpan<byte> header)
    {
        if (header[8..12].SequenceEqual("avif"u8) || header[8..12].SequenceEqual("avis"u8)) return true;
        if (!header[8..12].SequenceEqual("mif1"u8)) return false;
        // Skip the minor version (4 bytes); the rest of the box lists compatible brands, 4 bytes each.
        var boxEnd = Math.Min(header.Length, (int)Math.Min(int.MaxValue, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[..4])));
        for (var offset = 16; offset + 4 <= boxEnd; offset += 4)
        {
            if (header[offset..(offset + 4)].SequenceEqual("avif"u8) || header[offset..(offset + 4)].SequenceEqual("avis"u8)) return true;
        }
        return false;
    }

    private static bool IsCacheableRasterContentType(string contentType) =>
        contentType.Trim().ToLowerInvariant() is "image/jpeg" or "image/png" or "image/gif" or "image/webp" or "image/avif";

    private async Task<string?> WriteLimitedContentAsync(
        HttpContent content,
        HttpResponseMessage response,
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
            response.Dispose();
            ReplaceAtomically(tempImagePath, filePath);
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
        HttpResponseMessage response,
        string filePath,
        int targetWidth,
        ImageCacheFormat format,
        string? sourceContentType,
        string sourcePath,
        IDisposable downloadLease,
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
        var outputContentType = format == ImageCacheFormat.Webp ? "image/webp" : "image/jpeg";
        try
        {
            await WriteLimitedResponseContentToFileAsync(content, tempSourcePath, maxBytes, cancellationToken);
            response.Dispose();
            downloadLease.Dispose();
            // The allow-list is decided on the bytes before any decoder (or the upstream header) is trusted. AVIF passes it
            // but ImageMagick's policy has no AVIF coder, so an AVIF can only be stored verbatim (no width), never resized.
            var sniffedContentType = await SniffRasterContentTypeAsync(tempSourcePath, cancellationToken)
                ?? throw new ExternalApiException("Image response is not a supported raster image (jpeg, png, gif, webp or avif).");

            using (await _decodeLimiter.AcquireDecodeAsync(cancellationToken))
            {
                try
                {
                    ImageMagickRuntime.EnsureInitialized(_environment.ContentRootPath);
                    cancellationToken.ThrowIfCancellationRequested();

                    var maxDecodedPixels = Math.Max(1, _options.MaxDecodedPixels);
                    var isApng = ApngValidator.ValidateIfAnimated(tempSourcePath, maxDecodedPixels, cancellationToken);
                    var sourceFormatHint = GetSourceFormatHint(sniffedContentType);
                    var (metadataFrames, usedFormatHint) = PingImageMetadata(tempSourcePath, sourceFormatHint);
                    using (metadataFrames)
                    {

                        cancellationToken.ThrowIfCancellationRequested();
                        if (metadataFrames.Count == 0)
                        {
                            throw new ExternalApiException("Image response could not be identified as a supported raster image.");
                        }
                        if (metadataFrames.Count > ImageMagickRuntime.MaxFrameCount)
                        {
                            throw new ExternalApiException(
                                $"Image exceeds the decoded frame limit of {ImageMagickRuntime.MaxFrameCount}.");
                        }

                        var sourceFormat = metadataFrames[0].Format;
                        if (!IsAllowedRasterFormat(sourceFormat)
                            || metadataFrames.Any(frame => frame.Format != sourceFormat))
                        {
                            throw new ExternalApiException("Image response uses an unsupported image format.");
                        }

                        ValidateDecodedPixelBudget(metadataFrames, maxDecodedPixels);
                        var frameCount = metadataFrames.Count;

                        // Already at or below the requested width: keep the verified original bytes and their real
                        // type instead of a lossy decode and re-encode.
                        if (sourceFormat is MagickFormat.Jpeg or MagickFormat.Png or MagickFormat.Gif or MagickFormat.WebP
                            && metadataFrames.All(frame => frame.Width <= (uint)targetWidth)
                            && await SniffRasterContentTypeAsync(tempSourcePath, cancellationToken) is { } passThroughType)
                        {
                            File.Copy(tempSourcePath, tempImagePath, overwrite: true);
                            ReplaceAtomically(tempImagePath, filePath);
                            return passThroughType;
                        }

                        using var images = new MagickImageCollection();
                        await using (var sourceStream = OpenImageReader(tempSourcePath))
                        {
                            if (usedFormatHint && sourceFormatHint is { } formatHint)
                            {
                                images.Read(sourceStream, new MagickReadSettings
                                {
                                    Format = formatHint
                                });
                            }
                            else
                            {
                                images.Read(sourceStream);
                            }
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        if (images.Count != frameCount
                            || images.Count > ImageMagickRuntime.MaxFrameCount
                            || images.Any(frame => !IsAllowedRasterFormat(frame.Format) || frame.Format != sourceFormat))
                        {
                            throw new ExternalApiException("Image response frame metadata changed during decoding.");
                        }

                        ValidateDecodedPixelBudget(images, maxDecodedPixels);
                        if (isApng)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await using var source = OpenImageReader(tempSourcePath);
                            await using var target = new FileStream(
                                tempImagePath,
                                FileMode.Create,
                                FileAccess.Write,
                                FileShare.None,
                                StreamBufferSize,
                                FileOptions.Asynchronous | FileOptions.SequentialScan);
                            await source.CopyToAsync(target, StreamBufferSize, cancellationToken);
                            await target.FlushAsync(cancellationToken);
                            outputContentType = "image/png";
                        }
                        else
                        {
                            if (images.Count > 1 && sourceFormat is MagickFormat.Gif or MagickFormat.WebP)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                images.Coalesce();
                                cancellationToken.ThrowIfCancellationRequested();
                                ValidateDecodedPixelBudget(images, maxDecodedPixels);
                            }

                            foreach (var image in images)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (image.Width > targetWidth)
                                {
                                    image.Resize(new MagickGeometry((uint)targetWidth, 0)
                                    {
                                        IgnoreAspectRatio = false
                                    });
                                }

                                image.Quality = format == ImageCacheFormat.Webp ? 80u : 82u;
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                            await using var target = new FileStream(
                                tempImagePath,
                                FileMode.Create,
                                FileAccess.Write,
                                FileShare.None,
                                StreamBufferSize,
                                FileOptions.Asynchronous | FileOptions.SequentialScan);
                            if (format == ImageCacheFormat.Webp)
                            {
                                images.Write(target, MagickFormat.WebP);
                            }
                            else
                            {
                                images[0].Write(target, MagickFormat.Jpeg);
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                            await target.FlushAsync(cancellationToken);
                        }
                    }
                }
                catch (Exception ex) when (ex is MagickResourceLimitErrorException or MagickCacheErrorException)
                {
                    // A frame-list limit is a deterministic property of the
                    // input. Other native capacity failures can be transient
                    // and must remain on the IOException stale/503 path.
                    if (ex.Message.Contains("list length", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ExternalApiException(
                            $"Image exceeds the decoded frame limit of {ImageMagickRuntime.MaxFrameCount}.",
                            ex);
                    }

                    throw new IOException("Image decoder could not allocate within configured resource limits.", ex);
                }
                catch (Exception ex) when (ex is MagickException
                    or ArgumentException
                    or InvalidOperationException
                    or OverflowException)
                {
                    throw new ExternalApiException("Image response could not be decoded.", ex);
                }
            }

            ReplaceAtomically(tempImagePath, filePath);
            return outputContentType;
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

    private static bool IsAllowedRasterFormat(MagickFormat format)
    {
        return format is MagickFormat.Gif
            or MagickFormat.Jpeg
            or MagickFormat.Png
            or MagickFormat.WebP;
    }

    // The decoder is chosen from the verified magic bytes only, never from the upstream content type or the URL extension.
    private static MagickFormat? GetSourceFormatHint(string sniffedContentType) => sniffedContentType switch
    {
        "image/jpeg" => MagickFormat.Jpeg,
        "image/png" => MagickFormat.Png,
        "image/gif" => MagickFormat.Gif,
        "image/webp" => MagickFormat.WebP,
        _ => null
    };

    private static (MagickImageCollection Frames, bool UsedFormatHint) PingImageMetadata(
        string path,
        MagickFormat? fallbackFormatHint)
    {
        var frames = new MagickImageCollection();
        try
        {
            using var stream = OpenImageReader(path);
            frames.Ping(stream);
            return (frames, false);
        }
        catch (MagickException) when (fallbackFormatHint is not null)
        {
            frames.Dispose();
            frames = new MagickImageCollection();
            try
            {
                using var stream = OpenImageReader(path);
                frames.Ping(stream, new MagickReadSettings
                {
                    Format = fallbackFormatHint.Value
                });
                return (frames, true);
            }
            catch
            {
                frames.Dispose();
                throw;
            }
        }
        catch
        {
            frames.Dispose();
            throw;
        }
    }

    private static FileStream OpenImageReader(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static void ValidateDecodedPixelBudget(IEnumerable<IMagickImage<byte>> frames, long maxDecodedPixels)
    {
        long totalPixels = 0;
        var frameCount = 0;
        foreach (var frame in frames)
        {
            frameCount++;
            var page = frame.Page;
            var canvasWidth = Math.Max((long)frame.Width, page.Width);
            var canvasHeight = Math.Max((long)frame.Height, page.Height);
            canvasWidth = Math.Max(canvasWidth, (long)frame.Width + Math.Abs((long)page.X));
            canvasHeight = Math.Max(canvasHeight, (long)frame.Height + Math.Abs((long)page.Y));

            if (canvasWidth <= 0
                || canvasHeight <= 0
                || canvasWidth > maxDecodedPixels / canvasHeight)
            {
                throw PixelBudgetExceeded(maxDecodedPixels, frameCount);
            }

            var framePixels = canvasWidth * canvasHeight;
            if (totalPixels > maxDecodedPixels - framePixels)
            {
                throw PixelBudgetExceeded(maxDecodedPixels, frameCount);
            }

            totalPixels += framePixels;
        }

        if (frameCount == 0)
        {
            throw new ExternalApiException("Image response contains no frames.");
        }
    }

    private static ExternalApiException PixelBudgetExceeded(long maxDecodedPixels, int frameCount)
    {
        return new ExternalApiException(
            string.Create(CultureInfo.InvariantCulture, $"Image exceeds the decoded pixel limit of {maxDecodedPixels} across {frameCount} frame(s)."));
    }
    private static void ReplaceAtomically(string temporaryPath, string destinationPath)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!File.Exists(destinationPath))
            {
                try
                {
                    File.Move(temporaryPath, destinationPath);
                    return;
                }
                catch (IOException) when (File.Exists(destinationPath))
                {
                    continue;
                }
            }

            try
            {
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
                return;
            }
            catch (FileNotFoundException) when (attempt < 2)
            {
                // Another writer removed the old entry between the existence check and replace.
            }
        }

        throw new IOException($"Could not atomically replace cache file '{destinationPath}'.");
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

        if (!IsValidContentHash(image.ContentHash))
        {
            throw new InvalidDataException("Image cache metadata requires a valid content hash.");
        }

        var metadata = new ImageCacheMetadata(sourceUrl, image.ContentType, image.LastModifiedUtc, image.ContentHash, BodyGenerationVersion);
        var tempMetadataPath = $"{metadataPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                tempMetadataPath,
                JsonSerializer.Serialize(metadata, JsonOptions),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceAtomically(tempMetadataPath, metadataPath);
        }
        catch
        {
            TryDeleteFile(tempMetadataPath);
            throw;
        }
    }

    private async Task<CachedImage> ReadCachedImageAsync(
        string filePath,
        ImageCacheMetadata metadata,
        string metadataPath,
        string cacheKey,
        TimeSpan browserMaxAge,
        CancellationToken cancellationToken,
        bool isStale = false)
    {
        var contentHash = metadata.BodyGenerationVersion == BodyGenerationVersion
            ? metadata.ContentHash!
            : await ComputeContentHashAsync(filePath, cancellationToken);

        if (metadata.BodyGenerationVersion != BodyGenerationVersion)
        {
            var generationPath = GenerationImagePath(cacheKey, contentHash);
            await PublishLegacyBodyAsync(filePath, generationPath, cancellationToken);
            filePath = generationPath;
        }

        if (metadata.BodyGenerationVersion != BodyGenerationVersion
            || !IsValidContentHash(metadata.ContentHash))
        {
            var updated = new CachedImage(
                filePath,
                metadata.ContentType,
                metadata.FetchedUtc,
                cacheKey,
                browserMaxAge,
                contentHash,
                isStale);
            try
            {
                await WriteCachedMetadataAsync(metadataPath, metadata.SourceUrl, updated, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not persist the content hash for cached image {ImagePath}.", filePath);
            }
        }

        return new CachedImage(
            filePath,
            metadata.ContentType,
            metadata.FetchedUtc,
            cacheKey,
            browserMaxAge,
            contentHash,
            isStale);
    }

    private static async Task<string> ComputeContentHashAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = OpenImageReader(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsValidContentHash(string? contentHash)
    {
        return contentHash is { Length: 64 }
            && contentHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
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

    private string GenerationImagePath(string cacheKey, string contentHash)
    {
        if (!IsValidCacheKey(cacheKey) || !IsValidContentHash(contentHash))
        {
            throw new InvalidDataException("Image cache body generation identity is invalid.");
        }

        return Path.Combine(CacheDirectory(), $"{cacheKey}.{contentHash}.bin");
    }

    private string StagedImagePath(string cacheKey)
    {
        return Path.Combine(CacheDirectory(), $"{cacheKey}.{Guid.NewGuid():N}.stage.tmp");
    }

    private bool TryGetBodyPath(string cacheKey, ImageCacheMetadata metadata, out string bodyPath)
    {
        if (metadata.BodyGenerationVersion == BodyGenerationVersion)
        {
            if (IsValidContentHash(metadata.ContentHash))
            {
                bodyPath = GenerationImagePath(cacheKey, metadata.ContentHash!);
                return File.Exists(bodyPath);
            }

            bodyPath = string.Empty;
            return false;
        }

        bodyPath = ImagePath(cacheKey);
        return File.Exists(bodyPath);
    }

    private static bool TryGetCacheKeyFromBodyPath(string path, out string cacheKey)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var separator = name.IndexOf('.', StringComparison.Ordinal);
        cacheKey = separator < 0 ? name : name[..separator];
        return IsValidCacheKey(cacheKey)
            && (separator < 0 || IsValidContentHash(name[(separator + 1)..]));
    }

    private static bool IsValidCacheKey(string? cacheKey)
    {
        return cacheKey is { Length: 64 }
            && cacheKey.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static void PublishImmutableBody(string stagedPath, string generationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(generationPath)!);
        if (File.Exists(generationPath))
        {
            TryDeleteFile(stagedPath);
            return;
        }

        try
        {
            File.Move(stagedPath, generationPath);
        }
        catch (IOException) when (File.Exists(generationPath))
        {
            TryDeleteFile(stagedPath);
        }
    }

    private static async Task PublishLegacyBodyAsync(string legacyPath, string generationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(generationPath)!);
        if (File.Exists(generationPath))
        {
            return;
        }

        var tempPath = $"{generationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var source = OpenImageReader(legacyPath))
            await using (var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, StreamBufferSize, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            try
            {
                File.Move(tempPath, generationPath);
            }
            catch (IOException) when (File.Exists(generationPath))
            {
                // Another process published identical bytes under their verified SHA-256 name.
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
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

    private static string CacheKey(Uri sourceUri, int? width, ImageCacheFormat format)
    {
        var normalizedUrl = width is > 0
            ? $"{sourceUri.AbsoluteUri}|w:{width.Value}"
            : sourceUri.AbsoluteUri;
        if (format != ImageCacheFormat.Original)
        {
            normalizedUrl += $"|format:{format.ToString().ToLowerInvariant()}";
        }
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

    private static async Task WaitForGateAsync(CacheKeyGateLease gate, CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch
        {
            gate.Release(entered: false);
            throw;
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
        DateTimeOffset FetchedUtc,
        string? ContentHash = null,
        int? BodyGenerationVersion = null);

    private const int BodyGenerationVersion = 2;
}
