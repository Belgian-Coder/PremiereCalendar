using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

// Tests of this class run in parallel with each other (ParallelMode.All ignores [Collection]). Batch membership never
// depends on the 10 ms coalescing window: lookups are queued behind occupied HTTP slots (see Warmups) or held by gates.
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
        await firstWarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var secondWarmup = client.GetRottenTomatoesIdAsync("Q9000002", CancellationToken.None);
        await secondWarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

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
        var warmups = new Warmups();
        var started = Signal();
        var release = Signal();
        var handler = new AsyncHandler(async (request, token) =>
        {
            if (await warmups.HoldAsync(request, token)) return Identifiers(request);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Identifiers(request);
        });
        var client = CreateClient(handler);
        var occupied = await warmups.OccupyAsync(client);
        using var cancelled = new CancellationTokenSource();
        // Both lookups are queued while the HTTP slots are busy, so they share one batch however slow caller creation is.
        var first = client.GetRottenTomatoesIdAsync("Q1", cancelled.Token);
        var second = client.GetRottenTomatoesIdAsync("Q2", CancellationToken.None);
        warmups.Release();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.TrySetResult();

        Assert.Equal("m/Q2", await second);
        await occupied;
        Assert.Single(Warmups.Exclude(handler.Requests));
    }

    [Fact]
    public async Task CancellingEveryCallerAbortsHttpAndTheQueueRecovers()
    {
        var warmups = new Warmups();
        var started = Signal();
        var aborted = Signal();
        var calls = 0;
        var handler = new AsyncHandler(async (request, token) =>
        {
            if (await warmups.HoldAsync(request, token)) return Identifiers(request);
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { aborted.TrySetResult(); }
            }
            return Identifiers(request);
        });
        var client = CreateClient(handler);
        var occupied = await warmups.OccupyAsync(client);
        using var cancellation = new CancellationTokenSource();
        // Queued behind busy HTTP slots: one batch, so cancelling both callers must abort that single request.
        var first = client.GetRottenTomatoesIdAsync("Q1", cancellation.Token);
        var second = client.GetRottenTomatoesIdAsync("Q2", cancellation.Token);
        warmups.Release();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal("m/Q1", await client.GetRottenTomatoesIdAsync("Q1", CancellationToken.None));
        await occupied;
        Assert.Equal(2, Warmups.Exclude(handler.Requests).Count());
    }

    // The barrier hooks block thread-pool threads on purpose, so this test runs alone in the non-parallel phase.
    [Fact(DisableParallelization = true)]
    public async Task NewCallerCannotJoinBatchBetweenLastCancellationDecisionAndHttpAbort()
    {
        var started = Signal();
        var abandoning = Signal();
        var firstWarmupStarted = Signal();
        var secondWarmupStarted = Signal();
        var releaseWarmups = Signal();
        var secondCancellationEntered = Signal();
        using var releaseCancellation = new ManualResetEventSlim();
        using var releaseSecondCancellation = new ManualResetEventSlim();
        var calls = 0;
        var batcher = new WikimediaClaimBatcher(async (ids, token) =>
        {
            if (ids[0] is Warmups.FirstId or Warmups.SecondId)
            {
                (ids[0] == Warmups.FirstId ? firstWarmupStarted : secondWarmupStarted).TrySetResult();
                await releaseWarmups.Task.WaitAsync(token);
            }
            else if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return ids.ToDictionary(id => id, id => new WikimediaEntityClaims(new(null, true), new($"m/{id}", true)));
        }, null, beforeAbandonedBatchCancellation: () =>
        {
            abandoning.TrySetResult();
            releaseCancellation.Wait(TimeSpan.FromSeconds(30));
        });
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        // Occupy both batch slots so Q1 and Q2 are queued together and form one batch once the slots free up,
        // independently of how long the calls below take under CPU load (the 10 ms window alone could split them).
        var firstWarmup = batcher.GetAsync(Warmups.FirstId, 0, CancellationToken.None);
        await firstWarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var secondWarmup = batcher.GetAsync(Warmups.SecondId, 0, CancellationToken.None);
        await secondWarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var first = batcher.GetAsync("Q1", 0, firstCancellation.Token);
        var second = batcher.GetAsync("Q2", 0, secondCancellation.Token);
        releaseWarmups.TrySetResult();
        // Registered last, this runs before WaitAsync's callback. The token is cancelled,
        // but Q2 cannot remove its mapped waiter until this barrier opens.
        using var registration = secondCancellation.Token.Register(() =>
        {
            secondCancellationEntered.TrySetResult();
            releaseSecondCancellation.Wait(TimeSpan.FromSeconds(30));
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var cancelSecond = Task.Run(secondCancellation.Cancel, TestContext.Current.CancellationToken);
        await secondCancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        // Cancel on another thread: the abandon hook blocks inside Cancel until released below, which previously
        // stalled this thread for the full 5 s barrier timeout and made the interleaving timing-dependent.
        var cancelFirst = Task.Run(firstCancellation.Cancel, TestContext.Current.CancellationToken);
        await abandoning.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        // The old batch has been closed, but its HTTP cancellation has deliberately not run yet.
        var fresh = batcher.GetAsync("Q2", 0, CancellationToken.None);
        // Let Q2's cancellation finish before the HTTP abort runs. Otherwise the aborted batch may complete Q2's old work
        // first and its already-cancelled wait would return that result instead of throwing.
        releaseSecondCancellation.Set();
        await cancelSecond;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        releaseCancellation.Set();

        await cancelFirst;
        await Task.WhenAll(firstWarmup, secondWarmup);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("m/Q2", (await fresh.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Identifier.Value);
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
        var firstWarmupStarted = Signal();
        var secondWarmupStarted = Signal();
        var releaseWarmups = Signal();
        var calls = 0;
        var handler = new AsyncHandler(async (request, token) =>
        {
            var ids = EntityIds(request.RequestUri!);
            if (ids[0] is "Q9000001" or "Q9000002")
            {
                (ids[0] == "Q9000001" ? firstWarmupStarted : secondWarmupStarted).TrySetResult();
                await releaseWarmups.Task.WaitAsync(token);
                return Identifiers(request);
            }
            return Interlocked.Increment(ref calls) == 1
                ? StubHttpMessageHandler.Json("{\"entities\":{\"Q1\":{\"claims\":{}}}}") : Identifiers(request);
        });
        var client = CreateClient(handler);
        // Occupy both HTTP slots so Q1 and Q2 deterministically join one batch, independently of how long caller
        // creation takes under full-suite CPU load (the batch window alone made this flaky on a loaded host).
        var firstWarmup = client.GetRottenTomatoesIdAsync("Q9000001", CancellationToken.None);
        await firstWarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var secondWarmup = client.GetRottenTomatoesIdAsync("Q9000002", CancellationToken.None);
        await secondWarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var pending = new[] { client.GetRottenTomatoesIdResultAsync("Q1", CancellationToken.None),
            client.GetRottenTomatoesIdResultAsync("Q2", CancellationToken.None) };
        releaseWarmups.TrySetResult();
        var results = await Task.WhenAll(pending);
        await Task.WhenAll(firstWarmup, secondWarmup);

        Assert.True(results[0].IsSuccess);
        Assert.Null(results[0].Value);
        Assert.False(results[1].IsSuccess);
        Assert.True((await client.GetRottenTomatoesIdResultAsync("Q1", CancellationToken.None)).IsSuccess);
        Assert.Equal("m/Q2", await client.GetRottenTomatoesIdAsync("Q2", CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count(uri => EntityIds(uri)[0] is not ("Q9000001" or "Q9000002")));
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
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal("m/Q1_new", await client.GetRottenTomatoesIdAsync("Q1", CancellationToken.None, forceRefresh: true)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
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
            await firstWarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var secondWarmup = client.GetRottenTomatoesIdAsync("Q9000002", cancellation.Token);
            await secondWarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

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
        var warmups = new Warmups();
        var started = Signal();
        var inspect = Signal();
        var promoted = false;
        ProviderWorkPriority observed = ProviderWorkPriority.Maintenance;
        var handler = new AsyncHandler(async (request, token) =>
        {
            if (await warmups.HoldAsync(request, token)) return Identifiers(request);
            var priority = ProviderExecutionContext.CapturePriorityAccessor();
            started.TrySetResult();
            await inspect.Task.WaitAsync(token);
            observed = priority();
            return Identifiers(request);
        });
        var client = CreateClient(handler);
        var occupied = await warmups.OccupyAsync(client);
        Task<string?> first;
        Task<string?> second;
        using (ProviderExecutionContext.Push(() => promoted ? ProviderWorkPriority.Foreground : ProviderWorkPriority.Warmup))
            first = client.GetRottenTomatoesIdAsync("Q1", CancellationToken.None);
        using (ProviderExecutionContext.Push(() => ProviderWorkPriority.Maintenance))
            second = client.GetRottenTomatoesIdAsync("Q2", CancellationToken.None);
        // Both callers are queued behind busy HTTP slots, so they are guaranteed to share one batch.
        warmups.Release();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        promoted = true;
        inspect.TrySetResult();

        await Task.WhenAll(first, second, occupied);
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
        await initialBatchesStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
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
            var warmups = new Warmups();
            var handler = new AsyncHandler(async (request, token) =>
            {
                await warmups.HoldAsync(request, token);
                return Identifiers(request);
            });
            var client = CreateClient(handler, telemetry);
            // The warmups run outside any diagnostic context and keep both HTTP slots busy, so the two
            // contexts below share one batch however long the calls take; their events are filtered out by load id.
            var occupied = await warmups.OccupyAsync(client);
            var contexts = new[] { telemetry.CreateDiagnosticContext(), telemetry.CreateDiagnosticContext() };
            var calls = contexts.Select((context, index) =>
            {
                using var scope = telemetry.UseDiagnosticContext(context);
                return client.GetRottenTomatoesIdAsync($"Q{index + 1}", CancellationToken.None);
            }).ToArray();
            warmups.Release();
            await Task.WhenAll(calls);
            await occupied;
            using (telemetry.UseDiagnosticContext(contexts[0]))
                await client.GetRottenTomatoesIdAsync("Q1", CancellationToken.None);
            telemetry.FlushDiagnostics();
            var loadIds = contexts.Select(context => context.LoadId).ToHashSet();
            var events = ReadEvents(directory)
                .Where(entry => entry.TryGetProperty("loadId", out var load) && load.GetString() is { } id && loadIds.Contains(id)).ToArray();

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
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

            JsonElement[] events = [];
            // Event-driven in spirit: poll until the cancelled batch has been recorded, with a deadline that load cannot outrun.
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                telemetry.FlushDiagnostics();
                events = ReadEvents(directory);
                if (events.Count(entry => entry.GetProperty("event").GetString() == "provider.wikimedia.batch.completed") == 2) break;
                await Task.Delay(10, TestContext.Current.CancellationToken);
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

    /// <summary>Keeps both HTTP slots busy so lookups issued afterwards queue together and form one batch.</summary>
    private sealed class Warmups
    {
        internal const string FirstId = "Q9000001";
        internal const string SecondId = "Q9000002";
        private readonly TaskCompletionSource _first = Signal();
        private readonly TaskCompletionSource _second = Signal();
        private readonly TaskCompletionSource _release = Signal();

        internal static IEnumerable<Uri> Exclude(IEnumerable<Uri> requests)
            => requests.Where(uri => EntityIds(uri)[0] is not (FirstId or SecondId));

        /// <summary>Holds warmup requests until <see cref="Release"/>; returns false for every other request.</summary>
        internal async Task<bool> HoldAsync(HttpRequestMessage request, CancellationToken token)
        {
            var id = EntityIds(request.RequestUri!)[0];
            if (id is not (FirstId or SecondId)) return false;
            (id == FirstId ? _first : _second).TrySetResult();
            await _release.Task.WaitAsync(token);
            return true;
        }

        /// <summary>Starts both warmups and returns once each holds an HTTP slot; await the result after <see cref="Release"/>.</summary>
        internal async Task<Task> OccupyAsync(WikimediaClient client)
        {
            var first = client.GetRottenTomatoesIdAsync(FirstId, CancellationToken.None);
            await Started(_first.Task, first, "first");
            var second = client.GetRottenTomatoesIdAsync(SecondId, CancellationToken.None);
            await Started(_second.Task, second, "second");
            return Task.WhenAll(first, second);
        }

        private static async Task Started(Task signal, Task lookup, string name)
        {
            var timer = Stopwatch.StartNew();
            try { await signal.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException($"The {name} warmup not started after {timer.ElapsedMilliseconds} ms; lookup status={lookup.Status}; {lookup.Exception}", exception);
            }
        }

        internal void Release() => _release.TrySetResult();
    }

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
