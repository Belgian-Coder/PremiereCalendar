using System.Globalization;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ImdbRatingParserTests
{
    private static readonly DateTimeOffset ImportedAt = DateTimeOffset.Parse("2026-05-09T10:00:00Z");

    // The Split based row parser the span implementation replaced; behaviour must match it exactly.
    private static ImdbRatingRecord? Reference(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length < 3
            || string.IsNullOrWhiteSpace(parts[0])
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var averageRating)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var voteCount))
        {
            return null;
        }

        return new ImdbRatingRecord(parts[0].Trim(), averageRating, voteCount, ImportedAt);
    }

    public static TheoryData<string> Lines() => new()
    {
        "tt0000001\t7.6\t12345",
        "tt0000001\t7.6\t12345\textra\tfields",
        "tt0000001\t7.6\t12345\t",
        "tconst\taverageRating\tnumVotes",
        "",
        "\t",
        "\t\t",
        "tt0000001",
        "tt0000001\t7.6",
        "tt0000001\t7.6\t",
        "tt0000001\t\t5",
        "\t7.6\t5",
        "   \t7.6\t5",
        " \u00a0 \t7.6\t5",
        "  tt0000001 \t7.6\t5",
        "\u00a0tt0000001\u2003\t7.6\t5",
        "tt0000001\t 7.6 \t 5 ",
        "tt0000001\t7.6\t5\r",
        "tt0000001\t\t7.6\t5",
        "tt0000001\t\\N\t5",
        "tt0000001\t7.6\t\\N",
        "\\N\t\\N\t\\N",
        "tt0000001\t7,6\t5",
        "tt0000001\t7.6.1\t5",
        "tt0000001\t1e1\t5",
        "tt0000001\t1E-2\t5",
        "tt0000001\t+7.6\t+5",
        "tt0000001\t-7.6\t-5",
        "tt0000001\tNaN\t5",
        "tt0000001\tInfinity\t5",
        "tt0000001\t-Infinity\t5",
        "tt0000001\t1e999\t5",
        "tt0000001\t.5\t5",
        "tt0000001\t5.\t5",
        "tt0000001\t7.6\t5.0",
        "tt0000001\t7.6\t2147483647",
        "tt0000001\t7.6\t2147483648",
        "tt0000001\t7.6\t-2147483648",
        "tt0000001\t7.6\t1,000",
        "tt0000001\t7.6\t0x10",
        "tt0000001\t\u0667.6\t5",
        "tt0000001\t7.6\t\u0665",
        "t\u00e9\u00e9\t7.6\t5",
        "tt0000001\t7.6\t5\t\t\t",
        "\ttt0000001\t7.6\t5",
        "tt0000001\t\u0009 7.6\t5",
    };

    [Theory]
    [MemberData(nameof(Lines))]
    public void ParsesAndRejectsRowsExactlyLikeTheSplitImplementation(string line)
    {
        var expected = Reference(line);
        var accepted = ImdbDatasetImporter.TryParseRating(line, ImportedAt, out var actual);

        Assert.Equal(expected is not null, accepted);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("tt0000001\t7.6\t12345", "tt0000001", 7.6, 12345)]
    [InlineData("  tt0000002 \t 8.1 \t 23456 ", "tt0000002", 8.1, 23456)]
    [InlineData("tt0000003\t10\t0\textra", "tt0000003", 10d, 0)]
    public void AcceptedRowsHaveTrimmedIdAndParsedNumbers(string line, string id, double rating, int votes)
    {
        Assert.True(ImdbDatasetImporter.TryParseRating(line, ImportedAt, out var record));
        Assert.Equal(new ImdbRatingRecord(id, rating, votes, ImportedAt), record);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tconst\taverageRating\tnumVotes")]
    [InlineData("tt0000001\t\\N\t5")]
    [InlineData("tt0000001\t7.6")]
    [InlineData("   \t7.6\t5")]
    public void RejectedRowsLeaveNoRecord(string line)
    {
        Assert.False(ImdbDatasetImporter.TryParseRating(line, ImportedAt, out var record));
        Assert.Null(record);
    }

    [Fact]
    public void MatchesReferenceForGeneratedFieldCombinations()
    {
        string[] ids = ["tt0000001", " tt1 ", "", "  ", "\u00a0", "T\u00e9", "tt\t"];
        string[] ratings = ["7.6", " 7.6", "7.6 ", "", "x", "\\N", "1e2", "-0", "7,6", "NaN", "\u0661"];
        string[] votes = ["5", " 5 ", "", "x", "\\N", "-5", "99999999999", "5.5", "+5", "\u0665"];
        string[] tails = ["", "\tz", "\t", "\t\t"];
        var compared = 0;
        foreach (var id in ids)
        foreach (var rating in ratings)
        foreach (var vote in votes)
        foreach (var tail in tails)
        {
            var line = $"{id}\t{rating}\t{vote}{tail}";
            var expected = Reference(line);
            Assert.Equal(expected is not null, ImdbDatasetImporter.TryParseRating(line, ImportedAt, out var actual));
            Assert.Equal(expected, actual);
            compared++;
        }

        Assert.Equal(ids.Length * ratings.Length * votes.Length * tails.Length, compared);
    }
}
