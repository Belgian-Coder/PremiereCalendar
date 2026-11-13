using Npgsql;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class PostgresImdbRatingsStoreTests : IDisposable
{
    private readonly PostgresTestDatabase _database = PostgresTestDatabase.Create();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task PostgreSql_ImdbCopyKeepsOldRatingsReadableUntilRatingsAndStateCommit()
    {
        var store = new ImdbRatingsStore(_database.Options);
        var oldImport = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var newImport = oldImport.AddDays(1);
        await store.ReplaceAllAsync([new("tt-original", 7.5, 10, oldImport)], oldImport, CancellationToken.None);
        var oldState = new ImdbDatasetState(oldImport, 1, "previous failure");
        await store.SaveStateAsync(oldState, CancellationToken.None);
        await using var observer = await _database.OpenAsync();
        await BlockStatePublicationAsync(observer);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async IAsyncEnumerable<ImdbRatingRecord> Ratings()
        {
            yield return new("TT-NEW", 7.5, 1, oldImport);
            entered.TrySetResult();
            await release.Task.WaitAsync(deadline.Token);
            for (var index = 0; index < 5000; index++)
                yield return new($"tt{index:0000000}", 8.5, index, oldImport);
            yield return new(" ", 1, 1, oldImport);
            yield return new(" tt-new ", 9.5, 999, oldImport);
        }

        var import = store.ReplaceAllStreamingAsync(Ratings(), newImport, deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            await using (var copying = observer.CreateCommand())
            {
                copying.CommandText = "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND query LIKE 'COPY ImdbRatingsImport%'";
                Assert.Equal(1L, await copying.ExecuteScalarAsync(deadline.Token));
            }
            await AssertPreviousDatasetAsync(store, oldImport, oldState, deadline.Token);
            release.TrySetResult();
            await WaitForBlockedStatePublicationAsync(observer, deadline.Token);
            await AssertPreviousDatasetAsync(store, oldImport, oldState, deadline.Token);
            Assert.False(import.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await using var unlock = observer.CreateCommand();
            unlock.CommandText = "SELECT pg_advisory_unlock(193684)";
            await unlock.ExecuteNonQueryAsync();
            await import.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.Equal(5002, await import);
        Assert.Null(await store.GetByImdbIdAsync("tt-original", CancellationToken.None));
        Assert.Equal(new ImdbRatingRecord("tt-new", 9.5, 999, newImport),
            await store.GetByImdbIdAsync("tt-new", CancellationToken.None));
        var ids = Enumerable.Range(0, 5000).Select(index => $"tt{index:0000000}").ToArray();
        var batch = await store.GetByImdbIdsAsync(ids, CancellationToken.None);
        Assert.Equal(5000, batch.Count);
        Assert.All(batch.Values, rating => Assert.Equal(newImport, rating.ImportedAtUtc));
        Assert.Equal(new ImdbDatasetState(newImport, 5002, null), await store.GetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PostgreSql_ImdbCopyCancellationAndSourceFailurePreservePreviousDatasetAndAllowRetry()
    {
        var store = new ImdbRatingsStore(_database.Options);
        var oldImport = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        await store.ReplaceAllAsync([new("tt-original", 7.5, 10, oldImport)], oldImport, CancellationToken.None);
        var oldState = new ImdbDatasetState(oldImport, 1, "existing error");
        await store.SaveStateAsync(oldState, CancellationToken.None);

        foreach (var cancel in new[] { true, false })
        {
            using var cancellation = new CancellationTokenSource();
            async IAsyncEnumerable<ImdbRatingRecord> FailedRatings()
            {
                // More than one COPY buffer has reached PostgreSQL before
                // failure, exercising rollback after actual streamed writes.
                for (var index = 0; index < 5000; index++)
                    yield return new($"tt{index:0000000}", 8.5, index, oldImport.AddDays(1));
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
            await AssertPreviousDatasetAsync(store, oldImport, oldState, CancellationToken.None);
        }

        await store.ReplaceAllAsync([], oldImport.AddDays(2), CancellationToken.None);
        Assert.Null(await store.GetByImdbIdAsync("tt-original", CancellationToken.None));
        Assert.Equal(new ImdbDatasetState(oldImport.AddDays(2), 0, null), await store.GetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PostgreSql_ImdbCancellationDuringStatePublicationRollsBackRatingsAndState()
    {
        var store = new ImdbRatingsStore(_database.Options);
        var oldImport = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        await store.ReplaceAllAsync([new("tt-original", 7.5, 10, oldImport)], oldImport, CancellationToken.None);
        var oldState = new ImdbDatasetState(oldImport, 1, "existing error");
        await store.SaveStateAsync(oldState, CancellationToken.None);
        await using var observer = await _database.OpenAsync();
        await BlockStatePublicationAsync(observer);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var import = store.ReplaceAllAsync([new("tt-new", 9.5, 20, oldImport.AddDays(1))], oldImport.AddDays(1), cancellation.Token);
        try
        {
            await WaitForBlockedStatePublicationAsync(observer, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
            await AssertPreviousDatasetAsync(store, oldImport, oldState, CancellationToken.None);
        }
        finally
        {
            cancellation.Cancel();
            await using var unlock = observer.CreateCommand();
            unlock.CommandText = "SELECT pg_advisory_unlock(193684)";
            await unlock.ExecuteNonQueryAsync();
            await Record.ExceptionAsync(() => import);
        }
    }

    private static async Task BlockStatePublicationAsync(NpgsqlConnection observer)
    {
        await using var pause = observer.CreateCommand();
        // Stop publication after the ratings write but before state is complete.
        // The lock exists only inside this randomized test database.
        pause.CommandText = """
            CREATE FUNCTION pause_imdb_state() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock(193684);
                RETURN NEW;
            END $$;
            CREATE TRIGGER pause_imdb_state BEFORE INSERT OR UPDATE ON ImdbDatasetState
            FOR EACH ROW EXECUTE FUNCTION pause_imdb_state();
            SELECT pg_advisory_lock(193684)
            """;
        await pause.ExecuteNonQueryAsync();
    }

    private static async Task AssertPreviousDatasetAsync(ImdbRatingsStore store,
        DateTimeOffset oldImport, ImdbDatasetState oldState, CancellationToken cancellationToken)
    {
        Assert.Equal(new ImdbRatingRecord("tt-original", 7.5, 10, oldImport),
            await store.GetByImdbIdAsync("tt-original", cancellationToken));
        var batch = await store.GetByImdbIdsAsync(["tt-original", "tt-new", "tt0000001"], cancellationToken);
        Assert.Single(batch);
        Assert.Equal(oldState, await store.GetStateAsync(cancellationToken));
    }

    private static async Task WaitForBlockedStatePublicationAsync(NpgsqlConnection observer, CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var blocked = observer.CreateCommand();
            blocked.CommandText = "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND query LIKE '%INSERT INTO ImdbDatasetState%' AND cardinality(pg_blocking_pids(pid)) > 0";
            if (Convert.ToInt64(await blocked.ExecuteScalarAsync(cancellationToken)) > 0) return;
            await Task.Delay(10, cancellationToken);
        }
    }
}
