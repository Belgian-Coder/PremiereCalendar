using System.Security.Cryptography;
using System.Text;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class DatabaseMigrationChecksumTests
{
    public static TheoryData<int, string, string> HistoricalChecksums => new()
    {
        { 1, "09bf8d563bf3afdf18c79cbf1931e22268a3b16e215202e420d7c971e3edb6e5", "31dadbc76ca4ddb7e49ce001f87402bd43250e8e1e6441909e8a520072f0d758" },
        { 2, "d296a5952fccd5272d6ae79a837299ff5cff79c82bb40d5128220f9c9ba166d2", "2ef1c4c3ff4c98102165b51ea201e9fe3aa43e4f38ea67c839c022ef27354131" },
        { 3, "ce2f893674ad83eb5f89d1d9eee95642dee7238fe2368bf23d3735b926910a63", "c99b9a092adff746c5969927b58de50a710d536af8d616ba47a9104612529f4c" },
        { 4, "0e19b8ff266839912986b0e1c4f65c5a70ae973227fb1fa0032ac117a8088458", "f55d950663157a8a11ccabac4ea25e685bb1db6170c82f0ddeca3874136fa3fd" }
    };

    [Theory]
    [MemberData(nameof(HistoricalChecksums))]
    public void NewChecksumsAreCanonicalAndKnownHistoryRemainsReadable(int version, string lf, string crlf)
    {
        var authoritative = Assert.Single(DatabaseSchema.Migrations, migration => migration.Version == version);
        var lfSql = authoritative.Sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        var crlfSql = lfSql.Replace("\n", "\r\n", StringComparison.Ordinal);
        foreach (var sql in new[] { lfSql, crlfSql })
        {
            var migration = new DatabaseMigration(version, authoritative.Name, sql);
            Assert.Equal(lf, migration.Checksum);
            Assert.True(migration.MatchesChecksum(lf));
            Assert.True(migration.MatchesChecksum(crlf));
            Assert.Equal(sql, migration.Sql);
        }
    }

    [Fact]
    public void HistoricalAllowanceDoesNotAcceptChangedSqlNamesVersionsOrPrefixSeparators()
    {
        foreach (var migration in DatabaseSchema.Migrations)
        {
            var sql = migration.Sql.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.False(migration.MatchesChecksum(Digest($"{migration.Version}\n{migration.Name}\n{sql} ")));
            Assert.False(migration.MatchesChecksum(Digest($"{migration.Version}\n{migration.Name}-changed\n{sql}")));
            Assert.False(migration.MatchesChecksum(Digest($"{migration.Version + 1}\n{migration.Name}\n{sql}")));
            Assert.False(migration.MatchesChecksum(Digest($"{migration.Version}\r\n{migration.Name}\r\n{sql.Replace("\n", "\r\n", StringComparison.Ordinal)}")));
        }
        var future = new DatabaseMigration(5, "future-migration", "SELECT 1;\nSELECT 2;");
        Assert.True(future.MatchesChecksum(future.Checksum));
        Assert.False(future.MatchesChecksum(Digest("5\nfuture-migration\nSELECT 1;\r\nSELECT 2;")));
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
