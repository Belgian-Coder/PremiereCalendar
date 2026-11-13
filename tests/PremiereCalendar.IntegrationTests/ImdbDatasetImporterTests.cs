using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class ImdbDatasetImporterTests : IDisposable
{
    private readonly PostgresTestDatabase _database = PostgresTestDatabase.Create();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task ImportRatingsAsync_ReadsGzippedTitleRatingsDataset()
    {
        var handler = new GzipHandler(
            "tconst\taverageRating\tnumVotes\n" +
            "tt0000001\t7.6\t12345\n" +
            "tt0000002\t8.1\t23456\n");
        var store = new ImdbRatingsStore(_database.Options);
        var importer = new ImdbDatasetImporter(
            new HttpClient(handler) { BaseAddress = new Uri("https://datasets.imdbws.com/") },
            store,
            Microsoft.Extensions.Options.Options.Create(new ImdbDatasetOptions { RatingsUrl = "title.ratings.tsv.gz" }),
            TimeProvider.System,
            NullLogger<ImdbDatasetImporter>.Instance);

        var imported = await importer.ImportRatingsAsync(CancellationToken.None);

        Assert.Equal(2, imported);
        var rating = await store.GetByImdbIdAsync("tt0000002", CancellationToken.None);
        Assert.NotNull(rating);
        // ImdbRatings.AverageRating is a PostgreSQL REAL (4-byte float), so a stored 8.1 reads back as 8.1000003...
        Assert.Equal(8.1, rating.AverageRating, precision: 5);
        Assert.Equal(23456, rating.VoteCount);
    }

    [Fact]
    public async Task ImportRatingsAsync_SkipsHeaderMalformedAndMissingRowsAndTrimsIds()
    {
        var handler = new GzipHandler(
            "tconst\taverageRating\tnumVotes\r\n" +
            "tt0000001\t7.6\t12345\r\n" +
            "\r\n" +
            "tt0000002\t\\N\t5\r\n" +
            "tt0000003\t8.1\r\n" +
            "   \t8.1\t5\r\n" +
            "  TT0000004 \t 6.5 \t 77 \textra\r\n" +
            "tt0000005\t9.0\tabc\r\n" +
            "tt0000006\t5.5\t99");
        var store = new ImdbRatingsStore(_database.Options);
        var importer = new ImdbDatasetImporter(
            new HttpClient(handler) { BaseAddress = new Uri("https://datasets.imdbws.com/") },
            store,
            Microsoft.Extensions.Options.Options.Create(new ImdbDatasetOptions { RatingsUrl = "title.ratings.tsv.gz" }),
            TimeProvider.System,
            NullLogger<ImdbDatasetImporter>.Instance);

        Assert.Equal(3, await importer.ImportRatingsAsync(CancellationToken.None));

        Assert.Equal(12345, (await store.GetByImdbIdAsync("tt0000001", CancellationToken.None))!.VoteCount);
        var trimmed = await store.GetByImdbIdAsync("tt0000004", CancellationToken.None);
        Assert.Equal(6.5, trimmed!.AverageRating);
        Assert.Equal(77, trimmed.VoteCount);
        Assert.Equal(99, (await store.GetByImdbIdAsync("tt0000006", CancellationToken.None))!.VoteCount);
        foreach (var skipped in new[] { "tt0000002", "tt0000003", "tt0000005", "tconst" })
            Assert.Null(await store.GetByImdbIdAsync(skipped, CancellationToken.None));
    }

    private sealed class GzipHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            using (var writer = new StreamWriter(gzip))
            {
                writer.Write(content);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(buffer.ToArray())
            });
        }
    }
}
