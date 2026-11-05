using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class ImageBackgroundRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-image-background-" + Guid.NewGuid().ToString("N"));
    private const string Url = "https://image.tmdb.org/t/p/w342/background.jpg";

    [Fact]
    public async Task StaleImageIsImmediateAndDeduplicatedWhileForcedRefreshWaitsAndPublishesLast()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var handler = new AsyncHandler(async token =>
        {
            var request = Interlocked.Increment(ref requests);
            if (request == 2) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return Image([(byte)request]);
        });
        var options = CacheOptions();
        using var queue = new ImageRefreshQueue(options, NullLogger<ImageRefreshQueue>.Instance);
        var cache = Create(handler, options, queue);
        var original = await cache.GetOrAddAsync(Url, false, CancellationToken.None);
        Expire(original);
        try
        {
            var stale = await cache.GetOrAddAsync(Url, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(stale.IsStale);
            Assert.Equal(original.FilePath, stale.FilePath);
            Assert.Equal(TimeSpan.FromMinutes(1), stale.BrowserMaxAge);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var forced = cache.GetOrAddAsync(Url, true, CancellationToken.None);
            for (var index = 0; index < 20; index++)
                Assert.True((await cache.GetOrAddAsync(Url, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1))).IsStale);
            Assert.Equal(2, Volatile.Read(ref requests));
            Assert.False(forced.IsCompleted);
            release.TrySetResult();
            var forcedResult = await forced.WaitAsync(TimeSpan.FromSeconds(3));
            await queue.StopAsync(CancellationToken.None);
            var final = await cache.GetOrAddAsync(Url, false, CancellationToken.None);
            Assert.Equal(3, requests);
            Assert.False(final.IsStale);
            Assert.Equal(forcedResult.FilePath, final.FilePath);
            Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(original.FilePath));
            Assert.Equal(new byte[] { 3 }, await File.ReadAllBytesAsync(final.FilePath));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ShutdownCancelsAndJoinsRefreshWithoutReplacingStaleMetadataOrLeavingStagingFiles()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var handler = new AsyncHandler(async token =>
        {
            if (Interlocked.Increment(ref requests) > 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Image([1]);
        });
        var options = CacheOptions();
        using var queue = new ImageRefreshQueue(options, NullLogger<ImageRefreshQueue>.Instance);
        var cache = Create(handler, options, queue);
        var original = await cache.GetOrAddAsync(Url, false, CancellationToken.None);
        Expire(original);
        await cache.GetOrAddAsync(Url, false, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await queue.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        var stale = await cache.GetOrAddAsync(Url, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(stale.IsStale);
        Assert.Equal(original.FilePath, stale.FilePath);
        Assert.Equal(2, requests);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "images"), "*.tmp"));
    }

    [Fact]
    public async Task QueueBoundsDistinctWorkAndRejectsDuplicatesAndNewWorkAfterShutdown()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ImageCacheOptions { BackgroundRefreshConcurrency = 1, BackgroundRefreshQueueCapacity = 2 });
        using var queue = new ImageRefreshQueue(options, NullLogger<ImageRefreshQueue>.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        async Task Work(CancellationToken token)
        {
            Interlocked.Increment(ref started);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        Assert.True(queue.TryEnqueue("running", Work));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(queue.TryEnqueue("running", Work));
        Assert.True(queue.TryEnqueue("queued1", Work));
        Assert.True(queue.TryEnqueue("queued2", Work));
        Assert.False(queue.TryEnqueue("overflow", Work));
        await queue.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, started);
        Assert.False(queue.TryEnqueue("after.shutdown", Work));
    }

    private IOptions<ImageCacheOptions> CacheOptions() => Microsoft.Extensions.Options.Options.Create(new ImageCacheOptions
    {
        Directory = "images", CacheDays = 1, AllowedHosts = ["image.tmdb.org"], BackgroundRefreshConcurrency = 1
    });

    private FileImageCache Create(HttpMessageHandler handler, IOptions<ImageCacheOptions> options, ImageRefreshQueue queue)
        => new(new HttpClient(handler), options, new Environment(_root), NullLogger<FileImageCache>.Instance, refreshQueue: queue);

    private static void Expire(CachedImage image)
    {
        var path = Path.Combine(Path.GetDirectoryName(image.FilePath)!, image.CacheKey + ".json");
        var metadata = JsonNode.Parse(File.ReadAllText(path))!;
        metadata["fetchedUtc"] = DateTimeOffset.UtcNow.AddDays(-2);
        File.WriteAllText(path, metadata.ToJsonString());
    }

    private static HttpResponseMessage Image(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return response;
    }

    private sealed class AsyncHandler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(cancellationToken);
    }

    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
