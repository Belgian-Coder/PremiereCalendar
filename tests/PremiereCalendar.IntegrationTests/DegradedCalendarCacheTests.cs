using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class DegradedCalendarCacheTests : IDisposable
{
    private static readonly DateOnly Start = new(2026, 11, 2);
    private static readonly DateOnly End = Start.AddDays(6);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-degraded-cache-" + Guid.NewGuid().ToString("N"));
    private string DirectoryPath => Path.Combine(_root, "cache");
    private string CachePath => Path.Combine(DirectoryPath, "20261102-20261108-default.json");

    [Fact]
    public async Task DegradedSnapshotPersistsCoverageAfterRestartWithoutBecomingComplete()
    {
        var notice = Notice();
        Assert.True(await CreateCache().SetDegradedWeekAsync(Start, End, "default", [Item(1, "Available discovery")],
            [notice], DateTimeOffset.UtcNow, CancellationToken.None));

        var restarted = CreateCache();
        var snapshot = Assert.IsType<CalendarCacheSnapshot>(await restarted.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None));
        Assert.True(snapshot.IsFresh);
        Assert.False(snapshot.IsComplete);
        Assert.Equal(CalendarCacheCompleteness.Partial, snapshot.Metadata.Completeness);
        Assert.True(snapshot.CanReuseDegraded(DateTimeOffset.UtcNow));
        Assert.Equal(notice, Assert.Single(snapshot.UnavailableSources));
        Assert.Equal("Available discovery", Assert.Single(snapshot.Items).Title);
        Assert.Null(await restarted.GetWeekAsync(Start, End, "default", CancellationToken.None));
        Assert.Single((await restarted.GetWeekAsync(Start, End, "default", CancellationToken.None, allowExpired: true))!);
        Assert.NotNull(snapshot.Generation);
        Assert.Empty(Directory.GetFiles(DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task DegradedPublicationRetainsPriorSourceRowsButUsesValidatedIncomingVersionOnEqualClock()
    {
        var cache = CreateCache();
        var now = DateTimeOffset.UtcNow;
        var latest = Item(1, "Prior metadata") with
        {
            LastUpdatedUtc = now, ImdbScore = 8.5, EnrichmentVersion = new(0, 1, now, now, null)
        };
        var unavailable = Item(2, "Prior unavailable-source discovery") with
        {
            MergeContributions = [new PremiereMergeContribution { Source = "Trakt", Status = "accepted", TmdbId = 2 }]
        };
        await cache.SetWeekAsync(Start, End, "default", [latest, unavailable], CancellationToken.None);
        var original = (await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;

        var incoming = latest with
        {
            Title = "Validated metadata", ImdbScore = 9.1, EnrichmentVersion = new(1, 2, now, now, now.AddMinutes(-1))
        };
        // The caller supplies the fully validated projection, including the
        // unavailable source's retained row, under the captured generation.
        Assert.True(await cache.SetDegradedWeekAsync(Start, End, "default", [incoming, unavailable, Item(3, "New available discovery")],
            [Notice()], DateTimeOffset.UtcNow, CancellationToken.None, original.Generation));

        var snapshot = (await CreateCache().GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;
        Assert.NotEqual(original.Generation, snapshot.Generation);
        Assert.Equal(3, snapshot.Metadata.ItemCount);
        Assert.Equal("Validated metadata", snapshot.Items.Single(item => item.TmdbId == 1).Title);
        Assert.Equal(9.1, snapshot.Items.Single(item => item.TmdbId == 1).ImdbScore);
        Assert.Equal(incoming.EnrichmentVersion, snapshot.Items.Single(item => item.TmdbId == 1).EnrichmentVersion);
        Assert.Equal(latest.LastUpdatedUtc, snapshot.Items.Single(item => item.TmdbId == 1).LastUpdatedUtc);
        Assert.Contains(snapshot.Items, item => item.TmdbId == 2 && item.MergeContributions.Any(contribution => contribution.Source == "Trakt"));
        Assert.Contains(snapshot.Items, item => item.TmdbId == 3);
    }

    [Theory]
    [InlineData(CalendarCacheCompleteness.Complete)]
    [InlineData(CalendarCacheCompleteness.Partial)]
    public async Task OlderDegradedRefreshCannotReplaceNewerGeneration(CalendarCacheCompleteness completeness)
    {
        var refreshStarted = DateTimeOffset.UtcNow.AddMinutes(-1);
        await WriteEnvelopeAsync(completeness, DateTimeOffset.UtcNow, "newer-generation");
        var before = await File.ReadAllBytesAsync(CachePath);

        var saved = await CreateCache().SetDegradedWeekAsync(Start, End, "default", [Item(2, "Old refresh result")],
            [Notice()], refreshStarted, CancellationToken.None, "newer-generation");

        Assert.False(saved);
        Assert.Equal(before, await File.ReadAllBytesAsync(CachePath));
        Assert.Equal("newer-generation", (await CreateCache().GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!.Generation);
        Assert.Empty(Directory.GetFiles(DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task DegradedWriterRechecksNewerCompleteGenerationAfterAcquiringPublicationLock()
    {
        Directory.CreateDirectory(DirectoryPath);
        var refreshStarted = DateTimeOffset.UtcNow.AddMinutes(-1);
        Task<bool> pending;
        byte[] complete;
        await using (var heldLock = new FileStream(Path.Combine(DirectoryPath, ".publish.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            pending = CreateCache().SetDegradedWeekAsync(Start, End, "default", [Item(2, "Delayed degraded refresh")],
                [Notice()], refreshStarted, CancellationToken.None);
            Assert.False(pending.IsCompleted);
            await WriteEnvelopeAsync(CalendarCacheCompleteness.Complete, DateTimeOffset.UtcNow, "complete-winner");
            complete = await File.ReadAllBytesAsync(CachePath);
        }

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(complete, await File.ReadAllBytesAsync(CachePath));
        Assert.True((await CreateCache().GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!.IsComplete);
    }

    [Fact]
    public async Task SuccessfulCompletePublicationClearsDegradedCoverageAndChangesGeneration()
    {
        var cache = CreateCache();
        await cache.SetDegradedWeekAsync(Start, End, "default", [Item(1, "Partial")], [Notice()], DateTimeOffset.UtcNow, CancellationToken.None);
        var old = (await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;

        await cache.SetWeekAsync(Start, End, "default", [Item(2, "Recovered complete discovery")], CancellationToken.None);

        var snapshot = (await CreateCache().GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;
        Assert.True(snapshot.IsComplete);
        Assert.Empty(snapshot.UnavailableSources);
        Assert.False(snapshot.CanReuseDegraded(DateTimeOffset.UtcNow));
        Assert.NotEqual(old.Generation, snapshot.Generation);
        Assert.Equal("Recovered complete discovery", Assert.Single(snapshot.Items).Title);
    }

    [Theory]
    [InlineData("trakt", "authentication_denied", 403, true)]
    [InlineData("simkl", "authentication_denied", 401, true)]
    [InlineData("watchmode", "authentication_denied", 403, true)]
    [InlineData("tmdb", "authentication_denied", 403, false)]
    [InlineData("trakt", "http_failure", 403, false)]
    [InlineData("trakt", "authentication_denied", 500, false)]
    public async Task DegradedPublicationAcceptsOnlyOptionalAuthenticationDenials(string provider, string reason, int status, bool expected)
    {
        var notice = new SourceUnavailableNotice(provider, reason, status, DateTimeOffset.UtcNow.AddMinutes(15));
        var saved = await CreateCache().SetDegradedWeekAsync(Start, End, "default", [Item(1, "Fixture")], [notice],
            DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(expected, saved);
        Assert.Equal(expected, File.Exists(CachePath));
    }

    [Fact]
    public async Task RecoveryDeadlineAndSnapshotFreshnessBothBoundDegradedReuse()
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
        var snapshot = new CalendarCacheSnapshot([Item(1, "Fixture")],
            new CalendarCacheMetadata(DateTimeOffset.UtcNow, 1, 8, CalendarCacheCompleteness.Partial), true, "generation")
        { UnavailableSources = [new SourceUnavailableNotice("trakt", "authentication_denied", 403, deadline)] };
        Assert.True(snapshot.CanReuseDegraded(deadline.AddTicks(-1)));
        Assert.False(snapshot.CanReuseDegraded(deadline));
        Assert.False((snapshot with { IsFresh = false }).CanReuseDegraded(deadline.AddMinutes(-1)));
        Assert.True((snapshot with { Items = [] }).CanReuseDegraded(deadline.AddMinutes(-1)));
        Assert.False((snapshot with { UnavailableSources = [] }).CanReuseDegraded(deadline.AddMinutes(-1)));
        Assert.False((snapshot with { UnavailableSources = [.. snapshot.UnavailableSources,
            new SourceUnavailableNotice("simkl", "authentication_denied", 403, deadline.AddMinutes(-2))] })
            .CanReuseDegraded(deadline.AddMinutes(-1)));
    }

    [Fact]
    public async Task MetadataMaintenanceGenerationChangeFencesDegradedPublicationWithoutChangingDiscoveryClock()
    {
        var cache = CreateCache();
        var item = Item(1, "Existing discovery") with { ImdbId = "tt0000001", ImdbScore = 7.1 };
        await cache.SetWeekAsync(Start, End, "default", [item], CancellationToken.None);
        var baseline = (await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;
        var refreshStarted = DateTimeOffset.UtcNow;
        await cache.UpdateWeekMetadataAsync(Start, End, "default", baseline, [item with { ImdbScore = 8.5 }],
            CalendarCacheMaintenanceKind.Scores, CancellationToken.None);
        var maintained = (await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;
        Assert.NotEqual(baseline.Generation, maintained.Generation);
        Assert.Equal(baseline.Metadata.CachedAtUtc, maintained.Metadata.CachedAtUtc);
        var before = await File.ReadAllBytesAsync(CachePath);

        var saved = await cache.SetDegradedWeekAsync(Start, End, "default", [item], [Notice()], refreshStarted,
            CancellationToken.None, baseline.Generation);

        Assert.False(saved);
        Assert.Equal(before, await File.ReadAllBytesAsync(CachePath));
        Assert.Equal(8.5, Assert.Single((await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!.Items).ImdbScore);
    }

    [Fact]
    public async Task EmptyDegradedWeekPersistsWarningAndRemainsReusableUntilRecoveryDeadline()
    {
        var notice = Notice();
        Assert.True(await CreateCache().SetDegradedWeekAsync(Start, End, "default", [], [notice], DateTimeOffset.UtcNow, CancellationToken.None));
        var snapshot = (await CreateCache().GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;
        Assert.Empty(snapshot.Items);
        Assert.Equal(0, snapshot.Metadata.ItemCount);
        Assert.False(snapshot.IsComplete);
        Assert.Equal(notice, Assert.Single(snapshot.UnavailableSources));
        Assert.True(snapshot.CanReuseDegraded(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task DegradedWriterDoesNotResurrectRowsExcludedFromValidatedCallerProjection()
    {
        var cache = CreateCache();
        await cache.SetWeekAsync(Start, End, "default", [Item(1, "Disproven old occurrence"), Item(2, "Retained discovery")], CancellationToken.None);
        var baseline = (await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;

        Assert.True(await cache.SetDegradedWeekAsync(Start, End, "default", [baseline.Items.Single(item => item.TmdbId == 2)],
            [Notice()], DateTimeOffset.UtcNow, CancellationToken.None, baseline.Generation));

        var snapshot = (await CreateCache().GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;
        Assert.Equal(2, Assert.Single(snapshot.Items).TmdbId);
        Assert.True(snapshot.CanReuseDegraded(DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ValidatedPartialPublishesExactProjectionIncludingEmptyWithoutReusingCoverageNotices(bool priorComplete, bool keepOtherRow)
    {
        var cache = CreateCache();
        var prior = new[] { Item(1, "Disproven occurrence"), Item(2, "Retained discovery") };
        if (priorComplete) await cache.SetWeekAsync(Start, End, "default", prior, CancellationToken.None);
        else await cache.SetPartialWeekAsync(Start, End, "default", prior, CancellationToken.None);
        var baseline = (await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;
        IReadOnlyList<PremiereItem> projection = keepOtherRow ? [prior[1]] : [];

        Assert.True(await cache.SetValidatedPartialWeekAsync(Start, End, "default", projection,
            DateTimeOffset.UtcNow, CancellationToken.None, baseline.Generation));

        var snapshot = (await CreateCache().GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;
        Assert.Equal(projection.Select(item => item.CanonicalId), snapshot.Items.Select(item => item.CanonicalId));
        Assert.False(snapshot.IsComplete);
        Assert.Equal(CalendarCacheCompleteness.Partial, snapshot.Metadata.Completeness);
        Assert.Empty(snapshot.UnavailableSources);
        Assert.False(snapshot.CanReuseDegraded(DateTimeOffset.UtcNow));
        Assert.Null(await cache.GetWeekAsync(Start, End, "default", CancellationToken.None));
        Assert.DoesNotContain(snapshot.Items, item => item.TmdbId == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidatedPartialPreservesNewerCompleteOrMaintainedGenerationByteForByte(bool maintenanceOnly)
    {
        var cache = CreateCache();
        var original = Item(1, "Original") with { ImdbId = "tt0000001", ImdbScore = 7.1 };
        await cache.SetWeekAsync(Start, End, "default", [original], CancellationToken.None);
        var baseline = (await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;
        var refreshStarted = DateTimeOffset.UtcNow;
        if (maintenanceOnly)
            await cache.UpdateWeekMetadataAsync(Start, End, "default", baseline, [original with { ImdbScore = 8.5 }],
                CalendarCacheMaintenanceKind.Scores, CancellationToken.None);
        else await cache.SetWeekAsync(Start, End, "default", [Item(2, "New complete discovery")], CancellationToken.None);
        var winner = (await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;
        if (maintenanceOnly) Assert.Equal(baseline.Metadata.CachedAtUtc, winner.Metadata.CachedAtUtc);
        Assert.NotEqual(baseline.Generation, winner.Generation);
        var before = await File.ReadAllBytesAsync(CachePath);

        Assert.False(await cache.SetValidatedPartialWeekAsync(Start, End, "default", [], refreshStarted,
            CancellationToken.None, baseline.Generation));

        Assert.Equal(before, await File.ReadAllBytesAsync(CachePath));
        Assert.Empty(Directory.GetFiles(DirectoryPath, "*.tmp"));
    }

    private static SourceUnavailableNotice Notice() => new("trakt", "authentication_denied", 403, DateTimeOffset.UtcNow.AddMinutes(15));
    private static PremiereItem Item(int id, string title) => new()
    {
        CanonicalId = "movie:" + id, Type = PremiereItemType.MovieFirstRelease, MediaType = PremiereMediaType.Movie,
        TmdbId = id, Title = title, PremiereDate = Start
    };
    private async Task WriteEnvelopeAsync(CalendarCacheCompleteness completeness, DateTimeOffset cachedAtUtc, string generation)
    {
        Directory.CreateDirectory(DirectoryPath);
        await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(new
        {
            schemaVersion = 8, generation, cachedAtUtc, completeness, itemCount = 1, items = new[] { Item(1, "Newer snapshot") }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    private FileCalendarCache CreateCache() => new(Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions { Directory = "cache" }),
        new DegradedCacheEnvironment(_root), NullLogger<FileCalendarCache>.Instance);
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
    private sealed class DegradedCacheEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PremiereCalendar.IntegrationTests";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
