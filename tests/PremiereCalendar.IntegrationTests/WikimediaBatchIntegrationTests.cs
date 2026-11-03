using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Options;
using PremiereCalendar.Services;
using Xunit.Abstractions;

namespace PremiereCalendar.IntegrationTests;

public sealed class WikimediaBatchIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(50, 1)]
    [InlineData(125, 3)]
    public async Task ConcurrentIdentifiersUseChunksOfAtMostFiftyEntities(int count, int expectedRequests)
    {
        var firstWarmupStarted = Signal();
        var secondWarmupStarted = Signal();
        var releaseWarmups = Signal();
        var active = 0;
        ConcurrentQueue<int> observedConcurrency = new();
        var handler = new AsyncHandler(async (request, token) =>
        {
            observedConcurrency.Enqueue(Interlocked.Increment(ref active));
            try
            {
                var ids = EntityIds(request.RequestUri!);
                if (ids[0] is "Q9000001" or "Q9000002")
                {
                    (ids[0] == "Q9000001" ? firstWarmupStarted : secondWarmupStarted).TrySetResult();
                    await releaseWarmups.Task.WaitAsync(token);
                }
                else await Task.Delay(100, token);
                return Identifiers(request);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var client = CreateClient(handler);
        var firstWarmup = client.GetRottenTomatoesIdAsync("Q9000001", CancellationToken.None);
        await firstWarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondWarmup = client.GetRottenTomatoesIdAsync("Q9000002", CancellationToken.None);
        await secondWarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var started = Stopwatch.GetTimestamp();
        // Occupied HTTP slots let every lookup join the queue before a batch drains,
        // independently of how long caller creation takes under full-suite CPU load.
        var lookups = Enumerable.Range(1, count)
            .Select(id => client.GetRottenTomatoesIdAsync($"Q{id}", CancellationToken.None)).ToArray();
        releaseWarmups.TrySetResult();
        var values = await Task.WhenAll(lookups);
        await Task.WhenAll(firstWarmup, secondWarmup);
        var requests = handler.Requests.Where(uri => EntityIds(uri)[0] is not ("Q9000001" or "Q9000002")).ToArray();

        Assert.Equal(expectedRequests, requests.Length);
        Assert.Equal(expectedRequests + 2, handler.Requests.Count);
        Assert.Equal(2, observedConcurrency.Max());
        Assert.All(requests, uri =>
        {
            Assert.Equal("/w/api.php", uri.AbsolutePath);
            Assert.InRange(EntityIds(uri).Length, 2, 50);
        });
        Assert.Equal(Enumerable.Range(1, count).Select(id => $"m/Q{id}"), values);
        output.WriteLine($"Entities={count}; scalar_requests={count}; batch_requests={requests.Length}; warmup_requests=2; max_http_concurrency={observedConcurrency.Max()}; fake_http_ms=100; elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
    }

    [Fact]
    public async Task ConcurrentArtworkAndIdentifierForSameEntityShareOneFetch()
    {
        var release = Signal();
        var handler = new AsyncHandler(async (request, token) =>
        {
            await release.Task.WaitAsync(token);
            return Identifiers(request);
        });
        var client = CreateClient(handler);
        var identifier = client.GetRottenTomatoesIdAsync("Q123", CancellationToken.None);
        var image = client.GetReusableImageUrlAsync("Q123", CancellationToken.None);
        var duplicate = client.GetRottenTomatoesIdAsync("Q123", CancellationToken.None);
        // Keep the response pending until every consumer has joined, including
        // when full-suite scheduling pauses between the calls above.
        release.TrySetResult();

        Assert.Equal("m/Q123", await identifier);
        Assert.Null(await image);
        Assert.Equal("m/Q123", await duplicate);
        Assert.EndsWith("/wiki/Special:EntityData/Q123.json", Assert.Single(handler.Requests).AbsolutePath);
    }

    [Fact]
    public async Task CancellingOneCallerDoesNotCancelTheSharedBatch()
    {
        var started = Signal();
        var release = Signal();
        var handler = new AsyncHandler(async (request, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Identifiers(request);
        });
        var client = CreateClient(handler);
        using var cancelled = new CancellationTokenSource();
        var first = client.GetRottenTomatoesIdAsync("Q1", cancelled.Token);
        var second = client.GetRottenTomatoesIdAsync("Q2", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.TrySetResult();

        Assert.Equal("m/Q2", await second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CancellingEveryCallerAbortsHttpAndTheQueueRecovers()
    {
        var started = Signal();
        var aborted = Signal();
        var calls = 0;
        var handler = new AsyncHandler(async (request, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { aborted.TrySetResult(); }
            }
            return Identifiers(request);
        });
        var client = CreateClient(handler);
        using var cancellation = new CancellationTokenSource();
        var first = client.GetRottenTomatoesIdAsync("Q1", cancellation.Token);
        var second = client.GetRottenTomatoesIdAsync("Q2", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("m/Q1", await client.GetRottenTomatoesIdAsync("Q1", CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task NewCallerCannotJoinBatchBetweenLastCancellationDecisionAndHttpAbort()
    {
        var started = Signal();
        var abandoning = Signal();
        var secondCancellationEntered = Signal();
        using var releaseCancellation = new ManualResetEventSlim();
        using var releaseSecondCancellation = new ManualResetEventSlim();
        var calls = 0;
        var batcher = new WikimediaClaimBatcher(async (ids, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return ids.ToDictionary(id => id, id => new WikimediaEntityClaims(new(null, true), new($"m/{id}", true)));
        }, null, beforeAbandonedBatchCancellation: () =>
        {
            abandoning.TrySetResult();
            releaseCancellation.Wait(TimeSpan.FromSeconds(5));
        });
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var first = batcher.GetAsync("Q1", 0, firstCancellation.Token);
        var second = batcher.GetAsync("Q2", 0, secondCancellation.Token);
        // Registered last, this runs before WaitAsync's callback. The token is cancelled,
        // but Q2 cannot remove its mapped waiter until this barrier opens.
        using var registration = secondCancellation.Token.Register(() =>
        {
            secondCancellationEntered.TrySetResult();
            releaseSecondCancellation.Wait(TimeSpan.FromSeconds(5));
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelSecond = Task.Run(secondCancellation.Cancel);
        await secondCancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        firstCancellation.Cancel();
        await abandoning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The old batch has been closed, but its HTTP cancellation has deliberately not run yet.
        var fresh = batcher.GetAsync("Q2", 0, CancellationToken.None);
        releaseCancellation.Set();
        releaseSecondCancellation.Set();

        await cancelSecond;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal("m/Q2", (await fresh.WaitAsync(TimeSpan.FromSeconds(5))).Identifier.Value);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":\"maxlag\"}}")]
    [InlineData("{\"entities\":{}}")]
    [InlineData("{\"entities\":{\"Q1\":{\"missing\":\"\"}}}")]
    [InlineData("{\"entities\":{\"Q1\":{\"claims\":[]}}}")]
    [InlineData("{\"entities\":{\"Q1\":{\"claims\":{\"P1258\":{}}}}}")]
    [InlineData("{\"entities\":{\"Q1\":{\"claims\":{\"P1258\":[{}]}}}}")]
    [InlineData("[]")]
    [InlineData("{")]
    public async Task UnverifiedEntityResponsesAreNotCachedAsAbsent(string payload)
    {
        var calls = 0;
        var handler = new AsyncHandler((request, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? StubHttpMessageHandler.Json(payload) : Identifiers(request)));
        var client = CreateClient(handler);

        Assert.False((await client.GetRottenTomatoesIdResultAsync("Q1", CancellationToken.None)).IsSuccess);
        Assert.Equal("m/Q1", await client.GetRottenTomatoesIdAsync("Q1", CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PartialBatchKeepsVerifiedEntityButRetriesTheOmittedEntity()
    {
        var calls = 0;
        var handler = new AsyncHandler((request, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? StubHttpMessageHandler.Json("{\"entities\":{\"Q1\":{\"claims\":{}}}}") : Identifiers(request)));
        var client = CreateClient(handler);

        var results = await Task.WhenAll(client.GetRottenTomatoesIdResultAsync("Q1", CancellationToken.None),
            client.GetRottenTomatoesIdResultAsync("Q2", CancellationToken.None));

        Assert.True(results[0].IsSuccess);
        Assert.Null(results[0].Value);
        Assert.False(results[1].IsSuccess);
        Assert.True((await client.GetRottenTomatoesIdResultAsync("Q1", CancellationToken.None)).IsSuccess);
        Assert.Equal("m/Q2", await client.GetRottenTomatoesIdAsync("Q2", CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ForcedRequestDoesNotJoinOlderFlightOrAllowItToOverwriteFreshCache()
    {
        var started = Signal();
        var release = Signal();
        var calls = 0;
        var handler = new AsyncHandler(async (request, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return Identifiers(request, "_old");
            }
            return Identifiers(request, "_new");
        });
        var client = CreateClient(handler);
        var original = client.GetRottenTomatoesIdAsync("Q1", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("m/Q1_new", await client.GetRottenTomatoesIdAsync("Q1", CancellationToken.None, forceRefresh: true)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        release.TrySetResult();
        Assert.Equal("m/Q1_old", await original);
        Assert.Equal("m/Q1_new", await client.GetRottenTomatoesIdAsync("Q1", CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CachedAbsenceIsRevalidatedByForceAndQueuedForcesSharePendingFetch()
    {
        var firstWarmupStarted = Signal();
        var secondWarmupStarted = Signal();
        var releaseWarmups = Signal();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var calls = 0;
        var handler = new AsyncHandler(async (request, token) =>
        {
            var id = EntityIds(request.RequestUri!)[0];
            if (id is "Q9000001" or "Q9000002")
            {
                (id == "Q9000001" ? firstWarmupStarted : secondWarmupStarted).TrySetResult();
                await releaseWarmups.Task.WaitAsync(token);
                return Identifiers(request);
            }
            return Interlocked.Increment(ref calls) == 1
                ? StubHttpMessageHandler.Json("{\"entities\":{\"Q1\":{\"claims\":{}}}}") : Identifiers(request);
        });
        var client = CreateClient(handler);
        Assert.Null(await client.GetRottenTomatoesIdAsync("Q1", cancellation.Token));
        Assert.Null(await client.GetRottenTomatoesIdAsync("Q1", cancellation.Token));

        try
        {
            var firstWarmup = client.GetRottenTomatoesIdAsync("Q9000001", cancellation.Token);
            await firstWarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondWarmup = client.GetRottenTomatoesIdAsync("Q9000002", cancellation.Token);
            await secondWarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Both HTTP slots stay occupied until every forced generation is queued.
            // This tests queued coalescing without relying on the 10ms dispatch window.
            var forces = Enumerable.Range(0, 5)
                .Select(_ => client.GetRottenTomatoesIdAsync("Q1", cancellation.Token, forceRefresh: true)).ToArray();
            releaseWarmups.TrySetResult();
            var refreshed = await Task.WhenAll(forces);
            await Task.WhenAll(firstWarmup, secondWarmup);

            Assert.All(refreshed, value => Assert.Equal("m/Q1", value));
            Assert.Equal(2, handler.Requests.Count(uri => EntityIds(uri).Contains("Q1")));
            Assert.Equal(4, handler.Requests.Count);
        }
        finally
        {
            releaseWarmups.TrySetResult();
            cancellation.Cancel();
        }
    }

    [Fact]
    public async Task SharedBatchReadsHighestLiveCallerPriorityIncludingPromotion()
    {
        var started = Signal();
        var inspect = Signal();
        var promoted = false;
        ProviderWorkPriority observed = ProviderWorkPriority.Maintenance;
        var handler = new AsyncHandler(async (request, token) =>
        {
            var priority = ProviderExecutionContext.CapturePriorityAccessor();
            started.TrySetResult();
            await inspect.Task.WaitAsync(token);
            observed = priority();
            return Identifiers(request);
        });
        var client = CreateClient(handler);
        Task<string?> first;
        Task<string?> second;
        using (ProviderExecutionContext.Push(() => promoted ? ProviderWorkPriority.Foreground : ProviderWorkPriority.Warmup))
            first = client.GetRottenTomatoesIdAsync("Q1", CancellationToken.None);
        using (ProviderExecutionContext.Push(() => ProviderWorkPriority.Maintenance))
            second = client.GetRottenTomatoesIdAsync("Q2", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        promoted = true;
        inspect.TrySetResult();

        await Task.WhenAll(first, second);
        Assert.Equal(ProviderWorkPriority.Foreground, observed);
    }

    [Fact]
    public async Task SustainedForegroundDemandAdmitsOldestBackgroundAfterEightUniqueEntities()
    {
        var initialBatchesStarted = Signal();
        var release = Signal();
        var requests = 0;
        var handler = new AsyncHandler(async (request, token) =>
        {
            var number = Interlocked.Increment(ref requests);
            if (number <= 2)
            {
                if (number == 2) initialBatchesStarted.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return Identifiers(request);
        });
        var client = CreateClient(handler);
        var initial = Enumerable.Range(1, 100).Select(id => client.GetRottenTomatoesIdAsync($"Q{id}", CancellationToken.None)).ToArray();
        await initialBatchesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var before = Enumerable.Range(101, 200).Select(id => client.GetRottenTomatoesIdAsync($"Q{id}", CancellationToken.None)).ToArray();
        Task<string?>[] background;
        using (ProviderExecutionContext.Push(() => ProviderWorkPriority.Maintenance))
            background = Enumerable.Range(0, 10).Select(_ => client.GetRottenTomatoesIdAsync("Q9000", CancellationToken.None)).ToArray();
        var after = Enumerable.Range(301, 200).Select(id => client.GetRottenTomatoesIdAsync($"Q{id}", CancellationToken.None)).ToArray();
        release.TrySetResult();

        await Task.WhenAll(initial.Concat(before).Concat(background).Concat(after));

        var firstFollowingBatch = EntityIds(handler.Requests.ElementAt(2));
        Assert.Equal("Q9000", firstFollowingBatch[8]);
        Assert.All(background, task => Assert.Equal("m/Q9000", task.Result));
    }

    [Fact]
    public async Task LookupAndBatchDurationsArePersistedForEveryCallerContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wikimedia-diagnostics-{Guid.NewGuid():N}");
        try
        {
            using var store = new PerformanceDiagnosticStore(new TelemetryOptions(), directory);
            var telemetry = new PremiereTelemetry(store);
            var handler = new AsyncHandler((request, _) => Task.FromResult(Identifiers(request)));
            var client = CreateClient(handler, telemetry);
            var contexts = new[] { telemetry.CreateDiagnosticContext(), telemetry.CreateDiagnosticContext() };
            var calls = contexts.Select((context, index) =>
            {
                using var scope = telemetry.UseDiagnosticContext(context);
                return client.GetRottenTomatoesIdAsync($"Q{index + 1}", CancellationToken.None);
            }).ToArray();
            await Task.WhenAll(calls);
            using (telemetry.UseDiagnosticContext(contexts[0]))
                await client.GetRottenTomatoesIdAsync("Q1", CancellationToken.None);
            telemetry.FlushDiagnostics();
            var events = ReadEvents(directory);

            var batches = events.Where(entry => entry.GetProperty("event").GetString() == "provider.wikimedia.batch.completed").ToArray();
            Assert.Equal(2, batches.Length);
            Assert.Equal(contexts.Select(context => context.LoadId).Order(), batches.Select(entry => entry.GetProperty("loadId").GetString()).Order());
            Assert.Single(batches.Select(entry => entry.GetProperty("requestId").GetString()).Distinct());
            Assert.All(batches, entry =>
            {
                var details = entry.GetProperty("details");
                Assert.Equal(2, details.GetProperty("count").GetInt32());
                Assert.Equal(2, details.GetProperty("total").GetInt32());
                Assert.True(details.GetProperty("durationMilliseconds").GetDouble() >= 0);
            });
            var lookups = events.Where(entry => entry.GetProperty("event").GetString() == "provider.wikimedia.lookup.completed").ToArray();
            Assert.Equal(3, lookups.Length);
            Assert.All(lookups, entry => Assert.True(entry.GetProperty("details").GetProperty("durationMilliseconds").GetDouble() >= 0));
            Assert.Single(lookups, entry => entry.GetProperty("details").GetProperty("fromCache").GetBoolean());
            Assert.Equal(2, events.Count(entry => entry.GetProperty("event").GetString() == "provider.wikimedia.queue.completed"));
            Assert.Single(events, entry => entry.GetProperty("event").GetString() == "provider.wikimedia.fetch.completed");
            Assert.Single(events, entry => entry.GetProperty("event").GetString() == "provider.wikimedia.parse.completed");
            Assert.All(events, entry => Assert.True(entry.GetProperty("details").GetProperty("durationMilliseconds").GetDouble() >= 0));
            var text = string.Join('\n', events.Select(entry => entry.GetRawText()));
            Assert.DoesNotContain("Q1", text);
            Assert.DoesNotContain("wikidata.org", text);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailedAndCancelledLookupsAndBatchesPersistTheirDurations()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wikimedia-failure-diagnostics-{Guid.NewGuid():N}");
        try
        {
            using var store = new PerformanceDiagnosticStore(new TelemetryOptions(), directory);
            var telemetry = new PremiereTelemetry(store);
            var started = Signal();
            var calls = 0;
            var handler = new AsyncHandler(async (_, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    return StubHttpMessageHandler.Json("{\"error\":{\"code\":\"maxlag\"}}");
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("The cancelled request must not complete.");
            });
            var client = CreateClient(handler, telemetry);
            using var context = telemetry.UseDiagnosticContext(telemetry.CreateDiagnosticContext());
            Assert.False((await client.GetRottenTomatoesIdResultAsync("Q1", CancellationToken.None)).IsSuccess);
            using var cancellation = new CancellationTokenSource();
            var cancelled = client.GetRottenTomatoesIdResultAsync("Q2", cancellation.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

            JsonElement[] events = [];
            for (var attempt = 0; attempt < 100; attempt++)
            {
                telemetry.FlushDiagnostics();
                events = ReadEvents(directory);
                if (events.Count(entry => entry.GetProperty("event").GetString() == "provider.wikimedia.batch.completed") == 2) break;
                await Task.Delay(10);
            }
            var lookups = events.Where(entry => entry.GetProperty("event").GetString() == "provider.wikimedia.lookup.completed").ToArray();
            Assert.Equal(new[] { "failed", "cancelled" }, lookups.Select(entry => entry.GetProperty("details").GetProperty("outcome").GetString()));
            var batches = events.Where(entry => entry.GetProperty("event").GetString() == "provider.wikimedia.batch.completed").ToArray();
            Assert.Equal(new[] { "partial_or_failed", "cancelled" }, batches.Select(entry => entry.GetProperty("details").GetProperty("outcome").GetString()));
            Assert.All(events, entry => Assert.True(entry.GetProperty("details").GetProperty("durationMilliseconds").GetDouble() >= 0));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static JsonElement[] ReadEvents(string directory)
        => Directory.EnumerateFiles(directory, "*.ndjson", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

    private static WikimediaClient CreateClient(HttpMessageHandler handler, PremiereTelemetry? telemetry = null)
        => new(new HttpClient(handler) { BaseAddress = new("https://www.wikidata.org/") },
            new MemoryCache(new MemoryCacheOptions()), Microsoft.Extensions.Options.Options.Create(new WikimediaOptions()), telemetry: telemetry);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static HttpResponseMessage Identifiers(HttpRequestMessage request, string suffix = "")
    {
        var entities = EntityIds(request.RequestUri!).ToDictionary(id => id, id => new
        {
            claims = new Dictionary<string, object>
            {
                ["P1258"] = new[] { new { mainsnak = new { datavalue = new { value = $"m/{id}{suffix}" } } } }
            }
        });
        return StubHttpMessageHandler.Json(JsonSerializer.Serialize(new { entities }));
    }

    private static string[] EntityIds(Uri uri)
        => uri.AbsolutePath.Contains("Special:EntityData", StringComparison.Ordinal)
            ? [Path.GetFileNameWithoutExtension(uri.AbsolutePath)]
            : Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('&').Single(part => part.StartsWith("ids=", StringComparison.Ordinal))[4..]).Split('|');

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        internal ConcurrentQueue<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!);
            return responder(request, cancellationToken);
        }
    }
}
