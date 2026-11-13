using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PremiereCalendar.Services;

/// <summary>Crash-safe compression of closed traces; the raw trace remains authoritative until both publications succeed.</summary>
internal static partial class PerformanceTraceArchive
{
    internal const int MaximumIndexBytes = 4096;

    [GeneratedRegex(@"^performance-\d{8}T\d{13}Z-[a-f0-9]{32}-\d{6,}\.ndjson$", RegexOptions.CultureInvariant)]
    private static partial Regex TraceName();

    internal static bool IsTrace(string path) => TraceName().IsMatch(Path.GetFileName(path));

    internal static FileStream AcquireLease(string rawPath)
    {
        var directory = Path.Combine(Path.GetDirectoryName(rawPath)!, ".leases");
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, Path.GetFileName(rawPath) + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    internal static FileStream? TryAcquireLease(string rawPath)
    {
        try { return AcquireLease(rawPath); }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33 or 35) { return null; }
    }

    // Caller must hold this trace's lease. Raw data remains authoritative when both forms exist.
    internal static void CleanupInterruptedArtifacts(string rawPath, Action<string>? beforeDelete = null)
    {
        foreach (var path in new[] { rawPath + ".gz.pending", rawPath + ".gz.index.json.pending" }) Delete(path);
        if (File.Exists(rawPath))
        {
            Delete(rawPath + ".gz");
            Delete(rawPath + ".gz.index.json");
        }
        else if (!File.Exists(rawPath + ".gz")) Delete(rawPath + ".gz.index.json");
        void Delete(string path)
        {
            if (!File.Exists(path)) return;
            beforeDelete?.Invoke(path);
            File.Delete(path);
        }
    }

    internal static bool Archive(string rawPath, long maximumSourceBytes, Action? beforePublishIndex = null,
        CancellationToken cancellationToken = default, object? publicationGate = null,
        Action<CancellationToken>? beforeArchive = null)
    {
        if (!IsTrace(rawPath)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        var archivePath = rawPath + ".gz";
        var indexPath = archivePath + ".index.json";
        // Fixed temporary names cap crash remnants to one gzip and one tiny index per trace.
        var temporaryArchive = archivePath + ".pending";
        var temporaryIndex = indexPath + ".pending";
        // Serialize archival across application instances before touching shared staging paths.
        using var archiveLock = TryAcquireLease(rawPath);
        if (archiveLock is null || !File.Exists(rawPath)) return false;
        try
        {
            // The matching writer lease prevents archiving a live trace on Windows and Unix.
            using (var source = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (source.Length > maximumSourceBytes) return false;
                beforeArchive?.Invoke(cancellationToken);
                string hash;
                using (var target = new FileStream(temporaryArchive, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using (var compressor = new GZipStream(target, CompressionLevel.Fastest, leaveOpen: true))
                    {
                        var buffer = new byte[81920];
                        int count;
                        while ((count = source.Read(buffer)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            digest.AppendData(buffer, 0, count);
                            compressor.Write(buffer, 0, count);
                        }
                    }
                    hash = Convert.ToHexStringLower(digest.GetHashAndReset());
                    target.Flush(flushToDisk: true);
                }

                source.Position = 0;
                var index = BuildIndex(source, new FileInfo(temporaryArchive).Length, hash, cancellationToken);
                var indexBytes = JsonSerializer.SerializeToUtf8Bytes(index, JsonSerializerOptions.Web);
                if (indexBytes.Length > MaximumIndexBytes) throw new InvalidDataException("Trace index exceeds its bound.");
                // Incompressible traces remain raw; archival never grows retained storage.
                if (indexBytes.Length + index.CompressedBytes >= source.Length) return false;
                using (var target = new FileStream(temporaryIndex, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    target.Write(indexBytes);
                    target.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (publicationGate is null) Publish();
                else lock (publicationGate) Publish();
                return true;

                void Publish()
                {
                    File.Move(temporaryArchive, archivePath, overwrite: true);
                    beforePublishIndex?.Invoke();
                    File.Move(temporaryIndex, indexPath, overwrite: true);
                    File.Delete(rawPath);
                }
            }
        }
        finally
        {
            File.Delete(temporaryArchive);
            File.Delete(temporaryIndex);
        }
    }

    private static TraceIndex BuildIndex(Stream source, long compressedBytes, string hash, CancellationToken cancellationToken)
    {
        var index = new TraceIndex { RawBytes = source.Length, CompressedBytes = compressedBytes, Sha256 = hash };
        using var reader = new StreamReader(source, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index.Records++;
            if (line.Length > 8192) { index.MalformedRecords++; continue; }
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("timestampUtc", out var time)
                    || !time.TryGetDateTimeOffset(out var timestamp))
                {
                    index.InvalidTimestampRecords++;
                    continue;
                }
                index.MinimumTimestampUtc = Min(index.MinimumTimestampUtc, timestamp);
                index.MaximumTimestampUtc = Max(index.MaximumTimestampUtc, timestamp);
                if (root.TryGetProperty("instanceId", out var instance))
                {
                    var id = instance.GetString();
                    if (index.InstanceId is null) index.InstanceId = id;
                    else if (index.InstanceId != id) index.MultipleInstances = true;
                }
                if (root.TryGetProperty("processElapsedMilliseconds", out var elapsed) && elapsed.TryGetDouble(out var value)
                    && double.IsFinite(value))
                {
                    index.MinimumProcessElapsedMilliseconds = Math.Min(index.MinimumProcessElapsedMilliseconds ?? value, value);
                    index.MaximumProcessElapsedMilliseconds = Math.Max(index.MaximumProcessElapsedMilliseconds ?? value, value);
                }
                if (root.TryGetProperty("event", out var name) && name.GetString() == "diagnostics.loss") index.LossMarkers++;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            { index.MalformedRecords++; }
        }
        // ReadLine also accepts an unterminated last record; mark this as incomplete evidence.
        if (source.Length > 0)
        {
            source.Position = source.Length - 1;
            index.Complete = source.ReadByte() == '\n' && index.MalformedRecords == 0 && index.InvalidTimestampRecords == 0;
        }
        return index;
    }

    private static DateTimeOffset Min(DateTimeOffset? left, DateTimeOffset right) => left is { } value && value < right ? value : right;
    private static DateTimeOffset Max(DateTimeOffset? left, DateTimeOffset right) => left is { } value && value > right ? value : right;

    private sealed class TraceIndex
    {
        public int Version { get; init; } = 1;
        public long RawBytes { get; init; }
        public long CompressedBytes { get; init; }
        public string Sha256 { get; init; } = "";
        public string? InstanceId { get; set; }
        public bool MultipleInstances { get; set; }
        public DateTimeOffset? MinimumTimestampUtc { get; set; }
        public DateTimeOffset? MaximumTimestampUtc { get; set; }
        public double? MinimumProcessElapsedMilliseconds { get; set; }
        public double? MaximumProcessElapsedMilliseconds { get; set; }
        public long Records { get; set; }
        public long MalformedRecords { get; set; }
        public long InvalidTimestampRecords { get; set; }
        public long LossMarkers { get; set; }
        public bool Complete { get; set; }
    }
}
