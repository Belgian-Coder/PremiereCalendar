using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ImdbRatingsStoreTests : IDisposable
{
    private readonly PostgresTestDatabase _database = PostgresTestDatabase.Create();

    [Fact]
    public async Task ImdbRatingsStore_RoundTripsRatingsByImdbId()
    {
        var store = CreateStore();
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

    [Fact]
    public async Task ImdbRatingsStore_BatchesLookupAcrossLookupBatchBoundaries()
    {
        var store = CreateStore();
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

    [Fact]
    public async Task ImdbRatingsStore_ReplaceAllRemovesRatingsMissingFromNewDataset()
    {
        var store = CreateStore();
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

    [Fact]
    public async Task ImdbRatingsStore_ReadsPreviousDatasetWhileStreamingThenPublishesRatingsAndState()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? import = null;
        try
        {
            var store = CreateStore();
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
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImdbRatingsStore_CancellationOrSourceFailureRollsBackRatingsAndState(bool cancel)
    {
        var store = CreateStore();
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

    [Fact]
    public async Task ImdbRatingsStore_DuplicatesKeepLastValueAndAcceptedRowCount()
    {
        var store = CreateStore();
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

    [Fact]
    public async Task ImdbRatingsStore_ReusedInsertCommandBindsEveryRowAndUpsertsRepeats()
    {
        var store = CreateStore();
        var importedAt = DateTimeOffset.Parse("2026-05-09T10:00:00Z");
        const int rows = 3000;
        async IAsyncEnumerable<ImdbRatingRecord> Many()
        {
            for (var i = 0; i < rows; i++)
            {
                if (i % 500 == 0) await Task.Yield();
                yield return new($"tt{i:D7}", i / 10d, i, importedAt.AddDays(-1));
            }

            // A repeat of an earlier id must replace it through the same prepared statement.
            yield return new("TT0000007", 99.5, 777, importedAt.AddDays(-1));
        }

        Assert.Equal(rows + 1, await store.ReplaceAllStreamingAsync(Many(), importedAt, CancellationToken.None));

        Assert.Equal(rows - 1, (await store.GetByImdbIdAsync("tt0002999", CancellationToken.None))!.VoteCount);
        Assert.Equal(new ImdbRatingRecord("tt0000007", 99.5, 777, importedAt),
            await store.GetByImdbIdAsync("tt0000007", CancellationToken.None));
        var fractional = await store.GetByImdbIdAsync("tt0001234", CancellationToken.None);
        Assert.NotNull(fractional);
        Assert.Equal("tt0001234", fractional.ImdbId);
        Assert.Equal(123.4, fractional.AverageRating);
        Assert.Equal(1234, fractional.VoteCount);
        Assert.Equal(importedAt, fractional.ImportedAtUtc);
        Assert.Equal(rows + 1, (await store.GetStateAsync(CancellationToken.None)).RatingCount);
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

    [Fact]
    public async Task ImdbRatingsStore_ReadsExactlyTheOneDecimalRatingThatWasStored()
    {
        // AverageRating is a PostgreSQL REAL; widening the float directly would
        // read 7.6 back as 7.599999904632568 and miss a ">= 7.6" filter.
        var store = CreateStore();
        var importedAt = DateTimeOffset.Parse("2026-05-09T10:00:00Z");
        double[] values = [0.0, 0.1, 1.0, 5.5, 7.5, 7.6, 7.7, 8.1, 8.2, 9.9, 10.0];
        var records = values
            .Select((value, index) => new ImdbRatingRecord($"tt{index + 1:D7}", value, index + 1, importedAt))
            .ToArray();
        await store.ReplaceAllAsync(records, importedAt, CancellationToken.None);

        var single = await store.GetByImdbIdAsync("tt0000006", CancellationToken.None);
        var batch = await store.GetByImdbIdsAsync(records.Select(record => record.ImdbId).ToArray(), CancellationToken.None);

        Assert.NotNull(single);
        Assert.Equal(7.6, single.AverageRating);
        Assert.Equal(new ImdbRatingRecord("tt0000006", 7.6, 6, importedAt), single);
        foreach (var record in records)
        {
            Assert.Equal(record, batch[record.ImdbId]);
        }

        var atLeast76 = new CalendarFilters { ScoreSource = ScoreSource.Imdb, MinScore = 7.6, MaxScore = 10, IncludeUnknownScores = false };
        var atMost76 = new CalendarFilters { ScoreSource = ScoreSource.Imdb, MinScore = 0, MaxScore = 7.6, IncludeUnknownScores = false };
        var exactly76 = new CalendarFilters { ScoreSource = ScoreSource.Imdb, MinScore = 7.6, MaxScore = 7.6, IncludeUnknownScores = false };
        Assert.Equal([7.6, 7.7, 8.1, 8.2, 9.9, 10.0], PassingScores(batch.Values, atLeast76));
        Assert.Equal([0.0, 0.1, 1.0, 5.5, 7.5, 7.6], PassingScores(batch.Values, atMost76));
        Assert.Equal([7.6], PassingScores(batch.Values, exactly76));
    }

    [Fact]
    public void WidenStoredRating_ReturnsEveryOneDecimalRatingExactly()
    {
        for (var tenths = 0; tenths <= 100; tenths++)
        {
            var rating = tenths / 10d;
            Assert.Equal(rating, ImdbRatingsStore.WidenStoredRating((float)rating));
        }

        Assert.Equal(7.25, ImdbRatingsStore.WidenStoredRating(7.25f));
        Assert.Equal(123.4, ImdbRatingsStore.WidenStoredRating(123.4f));
        Assert.Equal(99.5, ImdbRatingsStore.WidenStoredRating(99.5f));
        Assert.Equal(7.6, ImdbRatingsStore.WidenStoredRating((float)7.599999904632568));
    }

    private static double[] PassingScores(IEnumerable<ImdbRatingRecord> ratings, CalendarFilters filters)
    {
        return ratings
            .Select(rating => new PremiereItem
            {
                CanonicalId = rating.ImdbId,
                Title = rating.ImdbId,
                MediaType = PremiereMediaType.Movie,
                TmdbId = 1,
                PremiereDate = new DateOnly(2026, 5, 4),
                ImdbScore = rating.AverageRating
            })
            .Where(item => PremiereFilter.PassesScoreFilter(item, filters))
            .Select(item => item.ImdbScore!.Value)
            .Order()
            .ToArray();
    }

    public void Dispose() => _database.Dispose();

    private ImdbRatingsStore CreateStore() => new(_database.Options);
}
