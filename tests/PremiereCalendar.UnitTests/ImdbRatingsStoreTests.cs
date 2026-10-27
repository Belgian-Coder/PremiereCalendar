using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

[Collection(SqliteTestCollection.Name)]
public sealed class ImdbRatingsStoreTests
{
    [Fact]
    public async Task SqliteImdbRatingsStore_RoundTripsRatingsByImdbId()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateStore(root);
            var importedAt = DateTimeOffset.Parse("2026-05-09T10:00:00Z");

            await store.ReplaceAllAsync(
                [new ImdbRatingRecord("tt0000001", 7.6, 12345, importedAt)],
                importedAt,
                CancellationToken.None);

            var rating = await store.GetByImdbIdAsync("tt0000001", CancellationToken.None);
            var missing = await store.GetByImdbIdAsync("tt-missing", CancellationToken.None);
            var state = await store.GetStateAsync(CancellationToken.None);

            Assert.NotNull(rating);
            Assert.Equal(7.6, rating.AverageRating);
            Assert.Equal(12345, rating.VoteCount);
            Assert.Null(missing);
            Assert.Equal(importedAt, state.LastImportedUtc);
            Assert.Equal(1, state.RatingCount);
            Assert.Null(state.LastError);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task SqliteImdbRatingsStore_BatchesLookupAcrossSqliteParameterLimits()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateStore(root);
            var importedAt = DateTimeOffset.Parse("2026-05-09T10:00:00Z");
            var records = Enumerable.Range(1, 925)
                .Select(index => new ImdbRatingRecord($"tt{index:0000000}", 7.5, index, importedAt))
                .ToArray();
            await store.ReplaceAllAsync(records, importedAt, CancellationToken.None);

            var ids = records.Select(record => record.ImdbId).Append("tt-missing").ToArray();
            var ratings = await store.GetByImdbIdsAsync(ids, CancellationToken.None);

            Assert.Equal(925, ratings.Count);
            Assert.Equal(925, ratings["tt0000925"].VoteCount);
            Assert.False(ratings.ContainsKey("tt-missing"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task SqliteImdbRatingsStore_ReplaceAllRemovesRatingsMissingFromNewDataset()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateStore(root);
            var firstImport = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
            var secondImport = DateTimeOffset.Parse("2026-05-09T10:00:00Z");

            await store.ReplaceAllAsync(
                [
                    new ImdbRatingRecord("tt0000001", 7.6, 12345, firstImport),
                    new ImdbRatingRecord("tt0000002", 8.1, 10, firstImport)
                ],
                firstImport,
                CancellationToken.None);
            await store.ReplaceAllAsync(
                [new ImdbRatingRecord("tt0000002", 8.2, 11, secondImport)],
                secondImport,
                CancellationToken.None);

            Assert.Null(await store.GetByImdbIdAsync("tt0000001", CancellationToken.None));
            var rating = await store.GetByImdbIdAsync("tt0000002", CancellationToken.None);

            Assert.NotNull(rating);
            Assert.Equal(8.2, rating.AverageRating);
            Assert.Equal(11, rating.VoteCount);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task SqliteImdbRatingsStore_ReadsPreviousDatasetWhileStreamingThenPublishesRatingsAndState()
    {
        var root = CreateRoot();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? import = null;
        try
        {
            var store = CreateStore(root);
            var oldImport = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
            var newImport = oldImport.AddDays(1);
            var records = Enumerable.Range(1, 925)
                .Select(index => new ImdbRatingRecord($"tt{index:0000000}", 7.5, index, oldImport)).ToArray();
            await store.ReplaceAllAsync(records, oldImport, CancellationToken.None);
            var oldState = new ImdbDatasetState(oldImport, records.Length, "previous failure");
            await store.SaveStateAsync(oldState, CancellationToken.None);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            import = store.ReplaceAllStreamingAsync(PausedRatings(entered, release, newImport), newImport, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var single = await store.GetByImdbIdAsync("tt0000001", deadline.Token);
            var batch = await store.GetByImdbIdsAsync(records.Select(record => record.ImdbId).ToArray(), deadline.Token);
            var state = await store.GetStateAsync(deadline.Token);
            Assert.Equal(oldImport, single!.ImportedAtUtc);
            Assert.Equal(records.Length, batch.Count);
            Assert.All(batch.Values, rating => Assert.Equal(oldImport, rating.ImportedAtUtc));
            Assert.Equal(oldState, state);
            Assert.False(import.IsCompleted);

            release.TrySetResult();
            Assert.Equal(2, await import.WaitAsync(TimeSpan.FromSeconds(5)));
            var published = await store.GetByImdbIdsAsync(["tt0000001", "tt-new", "tt0000925"], CancellationToken.None);
            Assert.Equal(2, published.Count);
            Assert.All(published.Values, rating => Assert.Equal(newImport, rating.ImportedAtUtc));
            Assert.Equal(new ImdbDatasetState(newImport, 2, null), await store.GetStateAsync(CancellationToken.None));
        }
        finally
        {
            release.TrySetResult();
            if (import is not null) await import;
            TryDelete(root);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SqliteImdbRatingsStore_CancellationOrSourceFailureRollsBackRatingsAndState(bool cancel)
    {
        var root = CreateRoot();
        try
        {
            var store = CreateStore(root);
            var oldImport = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
            var oldRating = new ImdbRatingRecord("tt-original", 7.5, 10, oldImport);
            await store.ReplaceAllAsync([oldRating], oldImport, CancellationToken.None);
            var oldState = new ImdbDatasetState(oldImport, 1, "existing error");
            await store.SaveStateAsync(oldState, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();

            async IAsyncEnumerable<ImdbRatingRecord> FailedRatings()
            {
                yield return new ImdbRatingRecord("tt-partial", 8, 20, oldImport.AddDays(1));
                await Task.Yield();
                if (cancel)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw new InvalidDataException("Interrupted dataset");
            }

            var failure = await Record.ExceptionAsync(() => store.ReplaceAllStreamingAsync(FailedRatings(), oldImport.AddDays(1), cancellation.Token));
            if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure);
            else Assert.IsType<InvalidDataException>(failure);
            Assert.Equal(oldRating, await store.GetByImdbIdAsync(oldRating.ImdbId, CancellationToken.None));
            Assert.Null(await store.GetByImdbIdAsync("tt-partial", CancellationToken.None));
            Assert.Equal(oldState, await store.GetStateAsync(CancellationToken.None));

            // A failed import releases the writer gate and leaves a usable connection.
            await store.ReplaceAllAsync([], oldImport.AddDays(2), CancellationToken.None);
            Assert.Equal(0, (await store.GetStateAsync(CancellationToken.None)).RatingCount);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task SqliteImdbRatingsStore_DuplicatesKeepLastValueAndAcceptedRowCount()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateStore(root);
            var importedAt = DateTimeOffset.Parse("2026-05-09T10:00:00Z");
            await store.ReplaceAllAsync(
            [
                new(" TT0000001 ", 7.5, 10, importedAt.AddDays(-1)),
                new(" ", 1, 1, importedAt),
                new("tt0000001", 8.5, 20, importedAt.AddDays(-1))
            ], importedAt, CancellationToken.None);

            Assert.Equal(new ImdbRatingRecord("tt0000001", 8.5, 20, importedAt),
                await store.GetByImdbIdAsync("TT0000001", CancellationToken.None));
            Assert.Equal(2, (await store.GetStateAsync(CancellationToken.None)).RatingCount);
        }
        finally { TryDelete(root); }
    }

    private static async IAsyncEnumerable<ImdbRatingRecord> PausedRatings(
        TaskCompletionSource entered,
        TaskCompletionSource release,
        DateTimeOffset importedAt)
    {
        yield return new ImdbRatingRecord("tt0000001", 8.5, 100, importedAt);
        entered.TrySetResult();
        await release.Task;
        yield return new ImdbRatingRecord("tt-new", 9, 200, importedAt);
    }

    private static SqliteImdbRatingsStore CreateStore(string root)
    {
        TestSqliteDatabase.Initialize(root, "data/app.db");
        return new SqliteImdbRatingsStore(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/app.db" }),
            new FakeWebHostEnvironment(root));
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"premiere-calendar-imdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
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

    private sealed class FakeWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = contentRootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
