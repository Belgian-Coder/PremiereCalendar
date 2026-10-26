using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class ImdbDatasetImporterReliabilityTests
{
    private const string ProviderName = "imdb-import-reliability";
    private static readonly Uri DatasetBaseUri = new("https://datasets.imdbws.com/");
    private static readonly string DatasetText =
        "tconst\taverageRating\tnumVotes\n" +
        "tt0000001\t7.6\t12345\n" +
        "tt0000002\t8.1\t23456\n";

    [Fact]
    public async Task ImportRatingsAsync_ReleasesProviderDeadlineBeforeStreamingDatabaseRows()
    {
        var root = CreateRoot();
        try
        {
            TestSqliteDatabase.Initialize(root, "data/app.db");
            var time = new ManualTimeProvider();
            var providerStore = CreateProviderStore(root);
            var policy = new AdaptiveProviderPolicy(providerStore, time, new PremiereTelemetry());
            var ratingsStore = new FakeImdbRatingsStore();
            var downloadDirectory = Path.Combine(root, "App_Data", "tmp", "imdb");
            Directory.CreateDirectory(downloadDirectory);
            var staleOrphan = Path.Combine(downloadDirectory, $"imdb-ratings-{Guid.NewGuid():N}.tmp");
            var freshOwnedFile = Path.Combine(downloadDirectory, $"imdb-ratings-{Guid.NewGuid():N}.tmp");
            var unrelatedFile = Path.Combine(downloadDirectory, "other-cache-entry.tmp");
            File.WriteAllText(staleOrphan, "stale scratch");
            File.WriteAllText(freshOwnedFile, "recent scratch");
            File.WriteAllText(unrelatedFile, "unrelated");
            File.SetLastWriteTimeUtc(staleOrphan, time.GetUtcNow().UtcDateTime.AddHours(-25));
            File.SetLastWriteTimeUtc(freshOwnedFile, time.GetUtcNow().UtcDateTime.AddHours(-1));
            File.SetLastWriteTimeUtc(unrelatedFile, time.GetUtcNow().UtcDateTime.AddDays(-30));
            ratingsStore.AfterFirstImportedRating = () =>
            {
                var snapshot = Assert.Single(policy.GetSnapshots(), item => item.Provider == ProviderName);
                Assert.Equal(0, snapshot.ActiveRequests);
                Assert.Equal(1, snapshot.ConsecutiveSuccesses);
                time.Advance(TimeSpan.FromSeconds(121));
            };

            using var client = CreateAdaptiveClient(policy, new GzipResponseHandler(DatasetText));
            var importer = CreateImporter(client, ratingsStore, time, root);

            var imported = await importer.ImportRatingsAsync(CancellationToken.None);

            Assert.Equal(2, imported);
            Assert.Equal(2, ratingsStore.ImportedRatings.Count);
            Assert.Equal(1, policy.GetSnapshots().Single(item => item.Provider == ProviderName).ConsecutiveSuccesses);
            Assert.Equal(0, policy.GetSnapshots().Single(item => item.Provider == ProviderName).ActiveRequests);
            Assert.False(File.Exists(staleOrphan));
            Assert.True(File.Exists(freshOwnedFile));
            Assert.True(File.Exists(unrelatedFile));
            Assert.Equal(2, GetDownloadFiles(root).Length);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ImportRatingsAsync_BodyDeadlinePersistsErrorAndDeletesTemporaryFile()
    {
        var root = CreateRoot();
        try
        {
            TestSqliteDatabase.Initialize(root, "data/app.db");
            var time = new ManualTimeProvider();
            var policy = new AdaptiveProviderPolicy(CreateProviderStore(root), time, new PremiereTelemetry());
            var ratingsStore = new FakeImdbRatingsStore();
            var body = new BlockingReadStream();
            using var client = CreateAdaptiveClient(policy, new StreamResponseHandler(body));
            var importer = CreateImporter(client, ratingsStore, time, root);

            var operation = importer.ImportRatingsAsync(CancellationToken.None);
            await body.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(120));

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

            Assert.NotNull(exception);
            Assert.False(string.IsNullOrWhiteSpace(ratingsStore.State.LastError));
            Assert.Equal(1, policy.GetSnapshots().Single(item => item.Provider == ProviderName).ConsecutiveFailures);
            Assert.Equal(0, policy.GetSnapshots().Single(item => item.Provider == ProviderName).ActiveRequests);
            Assert.Empty(GetDownloadFiles(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ImportRatingsAsync_CallerCancellationDoesNotPersistFailureAndDeletesTemporaryFile()
    {
        var root = CreateRoot();
        try
        {
            TestSqliteDatabase.Initialize(root, "data/app.db");
            var time = new ManualTimeProvider();
            var policy = new AdaptiveProviderPolicy(CreateProviderStore(root), time, new PremiereTelemetry());
            var ratingsStore = new FakeImdbRatingsStore();
            var body = new BlockingReadStream();
            using var client = CreateAdaptiveClient(policy, new StreamResponseHandler(body));
            var importer = CreateImporter(client, ratingsStore, time, root);
            using var cancellation = new CancellationTokenSource();

            var operation = importer.ImportRatingsAsync(cancellation.Token);
            await body.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

            Assert.Null(ratingsStore.State.LastError);
            Assert.Equal(0, ratingsStore.StateSaveCount);
            var snapshot = policy.GetSnapshots().Single(item => item.Provider == ProviderName);
            Assert.Equal(0, snapshot.ConsecutiveFailures);
            Assert.Equal(0, snapshot.ActiveRequests);
            Assert.Empty(GetDownloadFiles(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ImportRatingsAsync_EnforcesCompressedDownloadLimitAndDeletesTemporaryFile()
    {
        var root = CreateRoot();
        try
        {
            TestSqliteDatabase.Initialize(root, "data/app.db");
            var time = new ManualTimeProvider();
            var policy = new AdaptiveProviderPolicy(CreateProviderStore(root), time, new PremiereTelemetry());
            var ratingsStore = new FakeImdbRatingsStore();
            using var client = CreateAdaptiveClient(policy, new GzipResponseHandler(DatasetText, omitContentLength: true));
            var importer = CreateImporter(client, ratingsStore, time, root, maxDownloadBytes: 8);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => importer.ImportRatingsAsync(CancellationToken.None));

            Assert.Contains("byte limit", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(ratingsStore.State.LastError));
            Assert.Empty(GetDownloadFiles(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ImportRatingsAsync_PreservesDownloadErrorWhenFailureStateCannotBeRead()
    {
        var root = CreateRoot();
        try
        {
            TestSqliteDatabase.Initialize(root, "data/app.db");
            var time = new ManualTimeProvider();
            var policy = new AdaptiveProviderPolicy(CreateProviderStore(root), time, new PremiereTelemetry());
            var ratingsStore = new FakeImdbRatingsStore
            {
                StateReadFailure = new IOException("state database unavailable")
            };
            using var client = CreateAdaptiveClient(policy, new GzipResponseHandler(DatasetText, omitContentLength: true));
            var importer = CreateImporter(client, ratingsStore, time, root, maxDownloadBytes: 8);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => importer.ImportRatingsAsync(CancellationToken.None));

            Assert.Contains("byte limit", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(GetDownloadFiles(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static ImdbDatasetImporter CreateImporter(
        HttpClient client,
        FakeImdbRatingsStore ratingsStore,
        TimeProvider time,
        string root,
        long maxDownloadBytes = 512L * 1024 * 1024)
    {
        return new ImdbDatasetImporter(
            client,
            ratingsStore,
            Microsoft.Extensions.Options.Options.Create(new ImdbDatasetOptions
            {
                RatingsUrl = "title.ratings.tsv.gz",
                DownloadDirectory = "App_Data/tmp/imdb",
                MaxDownloadBytes = maxDownloadBytes
            }),
            time,
            NullLogger<ImdbDatasetImporter>.Instance,
            new TestEnvironment(root));
    }

    private static HttpClient CreateAdaptiveClient(AdaptiveProviderPolicy policy, HttpMessageHandler innerHandler)
    {
        var handler = new AdaptiveProviderHandlerFactory(policy).Create(
            ProviderName,
            maximumConcurrency: 1,
            requestTimeout: TimeSpan.FromSeconds(120));
        handler.InnerHandler = innerHandler;
        return new HttpClient(handler)
        {
            BaseAddress = DatasetBaseUri,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static ProviderAdaptiveStateStore CreateProviderStore(string root)
    {
        return new ProviderAdaptiveStateStore(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/app.db" }),
            new TestEnvironment(root));
    }

    private static string[] GetDownloadFiles(string root)
    {
        var directory = Path.Combine(root, "App_Data", "tmp", "imdb");
        return Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"premiere-calendar-imdb-import-reliability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static byte[] Compress(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new StreamWriter(gzip))
        {
            writer.Write(text);
        }

        return buffer.ToArray();
    }

    private sealed class FakeImdbRatingsStore : IImdbRatingsStore
    {
        public ImdbDatasetState State { get; private set; } = new(null, 0, null);
        public int StateSaveCount { get; private set; }
        public List<ImdbRatingRecord> ImportedRatings { get; } = [];
        public Action? AfterFirstImportedRating { get; set; }
        public Exception? StateReadFailure { get; init; }

        public Task<ImdbRatingRecord?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken)
            => Task.FromResult<ImdbRatingRecord?>(null);

        public Task<IReadOnlyDictionary<string, ImdbRatingRecord>> GetByImdbIdsAsync(
            IReadOnlyCollection<string> imdbIds,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, ImdbRatingRecord>>(
                new Dictionary<string, ImdbRatingRecord>(StringComparer.OrdinalIgnoreCase));

        public Task ReplaceAllAsync(
            IEnumerable<ImdbRatingRecord> ratings,
            DateTimeOffset importedAtUtc,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public async Task<int> ReplaceAllStreamingAsync(
            IAsyncEnumerable<ImdbRatingRecord> ratings,
            DateTimeOffset importedAtUtc,
            CancellationToken cancellationToken)
        {
            await foreach (var rating in ratings.WithCancellation(cancellationToken))
            {
                ImportedRatings.Add(rating);
                if (ImportedRatings.Count == 1)
                {
                    AfterFirstImportedRating?.Invoke();
                }
            }

            return ImportedRatings.Count;
        }

        public Task<ImdbDatasetState> GetStateAsync(CancellationToken cancellationToken)
            => StateReadFailure is null ? Task.FromResult(State) : Task.FromException<ImdbDatasetState>(StateReadFailure);

        public Task SaveStateAsync(ImdbDatasetState state, CancellationToken cancellationToken)
        {
            State = state;
            StateSaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class GzipResponseHandler(string text, bool omitContentLength = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = Compress(text);
            HttpContent content = omitContentLength
                ? new StreamContent(new NonSeekableReadStream(body))
                : new ByteArrayContent(body);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class StreamResponseHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body)
            });
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource<bool> _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class TestEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar.IntegrationTests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
        public string WebRootPath { get; set; } = contentRootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _utcNow;
        }

        public override long GetTimestamp()
        {
            lock (_gate) return _timestamp;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                timer.ChangeUnderLock(dueTime, period);
                return timer;
            }
        }

        public void Advance(TimeSpan amount)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_gate)
            {
                _utcNow += amount;
                _timestamp += amount.Ticks;
                foreach (var timer in _timers)
                {
                    if (timer.TryFireUnderLock(_timestamp, out var callback, out var state))
                    {
                        callbacks.Add((callback, state));
                    }
                }
            }

            foreach (var (callback, state) in callbacks)
            {
                callback(state);
            }
        }

        private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate) return timer.ChangeUnderLock(dueTime, period);
        }

        private bool Dispose(ManualTimer timer)
        {
            lock (_gate) return timer.DisposeUnderLock();
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long _dueTimestamp = long.MaxValue;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => owner.Change(this, dueTime, period);
            public void Dispose() => owner.Dispose(this);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool ChangeUnderLock(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _dueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : owner._timestamp + Math.Max(0, dueTime.Ticks);
                return true;
            }

            public bool TryFireUnderLock(long timestamp, out TimerCallback timerCallback, out object? timerState)
            {
                if (_disposed || _dueTimestamp > timestamp)
                {
                    timerCallback = null!;
                    timerState = null;
                    return false;
                }

                _dueTimestamp = long.MaxValue;
                timerCallback = callback;
                timerState = state;
                return true;
            }

            public bool DisposeUnderLock()
            {
                _disposed = true;
                return true;
            }
        }
    }
}
