using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class ProviderWorkSchedulerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReattachingToAdjacentExecutionPreservesItsClaimWithoutLastWatcherCancellation(bool beginBeforeAttach)
    {
        var request = new ProviderWorkRequest(ProviderWorkKind.CalendarForeground, "reattach-background", ProviderWorkPriority.Foreground, "{}");
        var handle = await _scheduler.EnqueueAsync(request);
        await using (var abandoned = _scheduler.WatchAsync(handle).GetAsyncEnumerator())
            Assert.True(await abandoned.MoveNextAsync());
        var job = (await _scheduler.ClaimNextAsync(false, "resumed-worker", CancellationToken.None))!;
        Assert.Equal(ProviderWorkPriority.Adjacent, job.Priority);
        CancellationTokenSource? execution = beginBeforeAttach ? _scheduler.BeginExecution(job, CancellationToken.None) : null;
        try
        {
            using var context = _scheduler.UseProviderExecutionContext(job);
            var priority = ProviderExecutionContext.CapturePriorityAccessor();
            Assert.Equal(ProviderWorkPriority.Adjacent, priority());
            var attached = await _scheduler.EnqueueAsync(request);
            Assert.False(attached.Created);
            Assert.Equal(handle.JobId, attached.JobId);
            Assert.Equal(ProviderWorkPriority.Foreground, priority());
            execution ??= _scheduler.BeginExecution(job, CancellationToken.None);
            Assert.False(execution.IsCancellationRequested);
            await using var first = _scheduler.WatchAsync(attached).GetAsyncEnumerator();
            await using var second = _scheduler.WatchAsync(attached).GetAsyncEnumerator();
            Assert.True(await first.MoveNextAsync());
            Assert.True(await second.MoveNextAsync());
            Assert.False(execution.IsCancellationRequested);
            Assert.Equal(job.LeaseOwner, (await _scheduler.GetAsync(job.JobId))!.LeaseOwner);
            await first.DisposeAsync();
            Assert.False(execution.IsCancellationRequested);
            await second.DisposeAsync();
            Assert.False(execution.IsCancellationRequested);
        }
        finally { if (execution is not null) _scheduler.EndExecution(job, execution); }
        Assert.Equal(ProviderWorkPriority.Foreground, ProviderExecutionContext.CapturePriorityAccessor()());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DifferentForegroundJobPreemptsDemotedCalendarClaimBeforeOrAfterBegin(bool beginBeforeEnqueue, bool attachExistingQueuedJob)
    {
        await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "background-claim", ProviderWorkPriority.Adjacent, "{}"));
        var job = (await _scheduler.ClaimNextAsync(false, "background-worker", CancellationToken.None))!;
        CancellationTokenSource? execution = beginBeforeEnqueue ? _scheduler.BeginExecution(job, CancellationToken.None) : null;
        try
        {
            var request = new ProviderWorkRequest(ProviderWorkKind.CalendarForeground, "different-visible-week", ProviderWorkPriority.Foreground, "{}");
            if (attachExistingQueuedJob) await _store.EnqueueAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);
            var visible = await _scheduler.EnqueueAsync(request);
            Assert.Equal(!attachExistingQueuedJob, visible.Created);
            Assert.NotEqual(job.JobId, visible.JobId);
            execution ??= _scheduler.BeginExecution(job, CancellationToken.None);
            Assert.True(execution.IsCancellationRequested);
        }
        finally { if (execution is not null) _scheduler.EndExecution(job, execution); }
    }

    [Fact]
    public async Task ReenqueuedAdjacentJobIsPromotedBeforeClaimEvenBeforeWatcherStarts()
    {
        var request = new ProviderWorkRequest(ProviderWorkKind.CalendarForeground, "queued-promotion", ProviderWorkPriority.Foreground, "{}");
        var handle = await _scheduler.EnqueueAsync(request);
        await using (var watcher = _scheduler.WatchAsync(handle).GetAsyncEnumerator()) Assert.True(await watcher.MoveNextAsync());
        Assert.Equal(ProviderWorkPriority.Adjacent, (await _scheduler.GetAsync(handle.JobId))!.Priority);
        await _scheduler.EnqueueAsync(request);
        var job = (await _scheduler.ClaimNextAsync(true, "foreground-worker", CancellationToken.None))!;
        Assert.Equal(handle.JobId, job.JobId);
        Assert.Equal(ProviderWorkPriority.Foreground, job.Priority);
        var execution = _scheduler.BeginExecution(job, CancellationToken.None);
        try { Assert.False(execution.IsCancellationRequested); }
        finally { _scheduler.EndExecution(job, execution); }
    }

    [Fact]
    public async Task ReattachingToHostedBackgroundCalendarDoesNotRestartProviderFetch()
    {
        var start = new DateOnly(2026, 10, 5);
        var pipeline = new ForegroundHandoffPipeline(start);
        var request = new ProviderWorkRequest(ProviderWorkKind.CalendarForeground, "hosted-reattach", ProviderWorkPriority.Adjacent,
            JsonSerializer.Serialize(new CalendarProviderWorkPayload(start, start.AddDays(6), false, null)));
        var handle = await _scheduler.EnqueueAsync(request);
        await using var services = new ServiceCollection().AddSingleton<IPremiereLoadPipeline>(pipeline).BuildServiceProvider();
        var recovery = new DatabaseRecoveryState();
        recovery.Set(new(DatabaseSchema.CurrentVersion, DatabaseSchema.CurrentVersion, true, "ready", null, null));
        using var worker = new ProviderWorkSchedulerHostedService(_scheduler, services.GetRequiredService<IServiceScopeFactory>(),
            recovery, Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { LeaseSeconds = 30 }),
            NullLogger<ProviderWorkSchedulerHostedService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await pipeline.Entered(start).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ProviderWorkPriority.Adjacent, pipeline.CurrentPriority(start));
            var before = (await _scheduler.GetAsync(handle.JobId))!;
            var attached = await _scheduler.EnqueueAsync(request with { Priority = ProviderWorkPriority.Foreground });
            await using var watcher = _scheduler.WatchAsync(attached).GetAsyncEnumerator();
            Assert.True(await watcher.MoveNextAsync());
            Assert.Equal(ProviderWorkPriority.Foreground, pipeline.CurrentPriority(start));
            Assert.Equal(before.LeaseOwner, (await _scheduler.GetAsync(handle.JobId))!.LeaseOwner);
            Assert.Equal(0, pipeline.CancellationCount(start));
            pipeline.Release(start);
            await WaitUntilAsync(async () => (await _scheduler.GetAsync(handle.JobId))!.State == ProviderWorkState.Completed);
            Assert.Equal(1, pipeline.SourceFetchCount(start));
            Assert.Equal(0, pipeline.CancellationCount(start));
            Assert.Equal(0, (await _scheduler.GetAsync(handle.JobId))!.AttemptCount);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CompressedCheckpointsKeepTwoSecondCadenceImmediateSourceAndPreemptPersistenceAndLeaseFences()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var job = await ClaimCalendarAsync("compressed-cadence");
        var items = Enumerable.Range(1, 150).Select(index => Item(index) with { Overview = new string('a', 250) }).ToArray();
        ProviderWorkProgress Page(string message) => Progress(job, message, new("TMDb", 150, 150, items) { SourceItems = [] });
        await _scheduler.PublishAsync(job, Page("first"), CancellationToken.None);
        Assert.Equal(4, JsonDocument.Parse((await _scheduler.GetAsync(job.JobId))!.CheckpointJson!).RootElement.GetProperty("Version").GetInt32());
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        await _scheduler.PublishAsync(job, Page("not-yet"), CancellationToken.None);
        Assert.Equal("first", ProviderWorkScheduler.TryReadCheckpoint((await _scheduler.GetAsync(job.JobId))!.CheckpointJson)!.Message);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await _scheduler.PublishAsync(job, Page("two-seconds"), CancellationToken.None);
        Assert.Equal("two-seconds", ProviderWorkScheduler.TryReadCheckpoint((await _scheduler.GetAsync(job.JobId))!.CheckpointJson)!.Message);
        await _scheduler.PublishAsync(job, Page("source") with
        {
            CalendarProgress = new("TMDb", 150, 150, items) { Phase = "complete", CheckpointKey = "tmdb:movies", SourceItems = items }
        }, CancellationToken.None);
        var successful = ProviderWorkScheduler.TryReadCheckpoint((await _scheduler.GetAsync(job.JobId))!.CheckpointJson)!;
        Assert.Equal(150, successful.CompletedSources["tmdb:movies"].Count);
        var completedUtc = successful.SourceCompletedUtc["tmdb:movies"];
        await _scheduler.PublishAsync(job, Page("preempt-latest"), CancellationToken.None);
        await _scheduler.PreemptAsync(job, CancellationToken.None);
        var restarted = NewScheduler(clock);
        var resumed = (await restarted.ClaimNextAsync(false, "restart", CancellationToken.None))!;
        var recovered = restarted.ReadResumeCheckpoint(resumed)!;
        Assert.Equal("preempt-latest", recovered.Message);
        Assert.Equal(150, recovered.CalendarProgress!.Items.Count);
        Assert.Equal(150, recovered.CompletedSources["tmdb:movies"].Count);
        Assert.Equal(completedUtc, recovered.SourceCompletedUtc["tmdb:movies"]);
        await Assert.ThrowsAsync<ProviderWorkLeaseLostException>(() => _scheduler.PublishAsync(job, Page("stale") with
        {
            CalendarProgress = new("Complete", 150, 150, items, IsFinal: true)
        }, CancellationToken.None));
        Assert.Equal("preempt-latest", ProviderWorkScheduler.TryReadCheckpoint((await restarted.GetAsync(job.JobId))!.CheckpointJson)!.Message);
        await restarted.PublishAsync(resumed, Page("final") with
        {
            CalendarProgress = new("Complete", 150, 150, items, IsFinal: true)
        }, CancellationToken.None);
        Assert.True(ProviderWorkScheduler.TryReadCheckpoint((await restarted.GetAsync(job.JobId))!.CheckpointJson)!.CalendarProgress!.IsFinal);
    }
}

public sealed class ProviderWorkCheckpointCodecTests
{
    [Fact]
    public void CompressedRoundTripKeepsCompleteMetadataCanonicalItemsSummariesAndCompletionTimes()
    {
        var completedUtc = DateTimeOffset.Parse("2026-11-02T12:00:00Z");
        var item = new PremiereItem
        {
            TmdbId = 42, MediaType = PremiereMediaType.Series, Type = PremiereItemType.SeriesEpisode, Title = "A saved title",
            PremiereDate = new(2026, 11, 2), CanonicalId = "tv:42:2:3", ImdbId = "tt0000042", TvdbId = 70,
            Overview = new string('x', 6000), SeasonNumber = 2, EpisodeNumber = 3, EpisodeTitle = "Saved episode",
            PosterUrl = "https://example.test/poster", Keywords = ["first", "second"], GenreIds = [18], Genres = ["Drama"],
            Sources = [new() { Name = "TMDb", Id = 42, Kind = "primary" }], SourceNames = ["TMDb"],
            TmdbScore = 8.3, ImdbScore = 7.6, OriginCountries = ["BE"], LastUpdatedUtc = completedUtc,
            WikidataId = "Q42", VerificationState = PremiereVerificationState.Unverified, VerificationNote = "Saved verification",
            ExternalProviderId = "external:42", ExternalUrl = "https://example.test/item/42", ExternalCandidateKey = "external-key",
            OriginalTitle = "Saved original title", BackdropUrl = "https://example.test/backdrop", ImageSource = "TMDb",
            TrailerUrl = "https://example.test/trailer", TmdbUrl = "https://example.test/tmdb", ImdbUrl = "https://example.test/imdb",
            OriginalLanguage = "nl", MovieReleaseTypes = [2, 3], Certifications = ["12"], TvStatus = "Returning Series", TvType = "Scripted",
            EpisodeSource = "TMDb", RuntimeMinutes = 51, TmdbVoteCount = 230, ImdbVoteCount = 890,
            RottenTomatoesScore = 85, RottenTomatoesAudienceScore = 80, MetacriticScore = 77,
            DateSemantics = new(new(2026, 11, 2), PremiereDateSourceKind.TmdbEpisodeAirDate, PremiereDataConfidence.High, "Saved date"),
            MergeContributions = [new() { Source = "TVmaze", MatchMethod = "imdb", Reason = "Saved match", TmdbId = 42,
                ImdbId = "tt0000042", TvdbId = 70, CandidateDate = new(2026, 11, 2), ExternalProviderId = "external:42" }],
            MissingDataIssues = [new() { Kind = "saved", Message = "Saved issue", Severity = "warning" }],
            NetworkName = "Saved network", WebChannelName = "Saved channel", TvmazeAverageRuntimeMinutes = 51, TvmazeRating = 8.2,
            OfficialSiteUrl = "https://example.test/official", TvmazeUrl = "https://example.test/tvmaze"
        };
        var progress = new PremiereLoadProgress("TMDb", 1, 1, [item], CompletedWork: 14, TotalWork: 20, ProgressText: "Saved progress")
        {
            ProviderKey = "tmdb", Phase = "enriching", SourceItems = [], CheckpointKey = "tmdb:tv", AcceptedCount = 1,
            TotalCandidateCount = 20, ProcessedCount = 14, FilteredCount = 3, UnmappedCount = 2, NetNewCount = 1,
            IsBackground = true, IsStaleCache = true, HasSourceErrors = true, FailedSourceNames = ["TVmaze"], RetryAfterUtc = completedUtc.AddMinutes(2)
        };
        var summary = new ProviderWorkProgress("opaque-job", ProviderWorkState.Running, "saved source", 14, 20,
            progress with { Items = [], SourceItems = [], Phase = "complete", HasSourceErrors = false });
        var checkpoint = new ProviderWorkCheckpoint("saved checkpoint", 14, 20, progress,
            new Dictionary<string, IReadOnlyList<PremiereItem>> { ["tmdb:tv"] = [item] })
        {
            SourceCompletedUtc = new Dictionary<string, DateTimeOffset> { ["tmdb:tv"] = completedUtc },
            SourceSummaries = new Dictionary<string, ProviderWorkProgress> { ["tmdb:tv:complete"] = summary }
        };
        var encoded = ProviderWorkCheckpointCodec.Encode(checkpoint);
        Assert.Equal("br-base64", encoded.Encoding);
        Assert.True(encoded.StoredBytes < encoded.RawBytes);
        var decoded = ProviderWorkScheduler.TryReadCheckpoint(encoded.Json)!;
        Assert.Equal(3, decoded.Version);
        Assert.Equal(checkpoint.Message, decoded.Message);
        Assert.Equal(checkpoint.CompletedWork, decoded.CompletedWork);
        Assert.Equal(checkpoint.TotalWork, decoded.TotalWork);
        Assert.Equal(completedUtc, decoded.SourceCompletedUtc["tmdb:tv"]);
        Assert.Equal(JsonSerializer.Serialize(item), JsonSerializer.Serialize(Assert.Single(decoded.CompletedSources["tmdb:tv"])));
        Assert.Equal(JsonSerializer.Serialize(progress with { SourceItems = [item] }), JsonSerializer.Serialize(decoded.CalendarProgress));
        Assert.Equal(JsonSerializer.Serialize(summary with { CalendarProgress = summary.CalendarProgress! with { Items = [item], SourceItems = [item] } }),
            JsonSerializer.Serialize(decoded.SourceSummaries["tmdb:tv:complete"]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LegacyVersionsRemainReadableAndSmallPayloadsKeepVersionThree(int version)
    {
        var checkpoint = new ProviderWorkCheckpoint("saved", 1, 2, new("source", 0, 0, []),
            new Dictionary<string, IReadOnlyList<PremiereItem>>()) { Version = version };
        Assert.Equal("saved", ProviderWorkScheduler.TryReadCheckpoint(JsonSerializer.Serialize(checkpoint))!.Message);
        var small = ProviderWorkCheckpointCodec.Encode(checkpoint with { Version = 3 });
        Assert.Equal("json", small.Encoding);
        Assert.Equal(3, JsonDocument.Parse(small.Json).RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public void IncompressiblePayloadUsesTheCompleteUncompressedVersionThreeCheckpoint()
    {
        var bytes = new byte[24_000];
        new Random(731).NextBytes(bytes);
        var message = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
        var checkpoint = new ProviderWorkCheckpoint(message, 1, 2, null,
            new Dictionary<string, IReadOnlyList<PremiereItem>>());
        var encoded = ProviderWorkCheckpointCodec.Encode(checkpoint);
        Assert.Equal("json", encoded.Encoding);
        Assert.Equal(encoded.RawBytes, encoded.StoredBytes);
        Assert.Equal(message, ProviderWorkScheduler.TryReadCheckpoint(encoded.Json)!.Message);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"Version\":\"4\"}")]
    [InlineData("{\"Version\":5}")]
    [InlineData("{\"Version\":4,\"Encoding\":\"gzip\",\"UncompressedBytes\":20,\"Payload\":\"aaaa\"}")]
    [InlineData("{\"Version\":4,\"Encoding\":\"br-base64\",\"UncompressedBytes\":0,\"Payload\":\"aaaa\"}")]
    [InlineData("{\"Version\":4,\"Encoding\":\"br-base64\",\"UncompressedBytes\":67108865,\"Payload\":\"aaaa\"}")]
    [InlineData("{\"Version\":4,\"Encoding\":\"br-base64\",\"UncompressedBytes\":20,\"Payload\":\"!invalid!\"}")]
    [InlineData("{\"Version\":4,\"Encoding\":\"br-base64\",\"UncompressedBytes\":20,\"Payload\":\"aaaa\"}")]
    [InlineData("{\"Version\":3,\"Message\":\"saved\",\"CompletedSources\":null}")]
    public void CorruptOrUnsupportedCheckpointsFailSafely(string json)
        => Assert.Null(ProviderWorkScheduler.TryReadCheckpoint(json));

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void CompressedPayloadRejectsWrongDeclaredLengthAndTrailingCompressedData(int lengthAdjustment)
    {
        var checkpoint = new ProviderWorkCheckpoint(new string('x', 6000), null, null, null,
            new Dictionary<string, IReadOnlyList<PremiereItem>>());
        var encoded = ProviderWorkCheckpointCodec.Encode(checkpoint);
        var envelope = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(encoded.Json)!;
        var changedLength = JsonSerializer.Serialize(new
        {
            Version = 4, Encoding = "br-base64", UncompressedBytes = encoded.RawBytes + lengthAdjustment,
            Payload = envelope["Payload"].GetString()
        });
        Assert.Null(ProviderWorkScheduler.TryReadCheckpoint(changedLength));
        var bytes = Convert.FromBase64String(envelope["Payload"].GetString()!);
        var trailing = JsonSerializer.Serialize(new
        {
            Version = 4, Encoding = "br-base64", UncompressedBytes = encoded.RawBytes,
            Payload = Convert.ToBase64String(bytes.Concat(new byte[] { 0 }).ToArray())
        });
        Assert.Null(ProviderWorkScheduler.TryReadCheckpoint(trailing));
    }
}
