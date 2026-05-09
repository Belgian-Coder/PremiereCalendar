using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class RatingMapperTests
{
    private readonly RatingMapper _mapper = new();

    [Theory]
    [InlineData("7.4", 7.4)]
    [InlineData("N/A", null)]
    [InlineData("", null)]
    public void ParseImdbScore_HandlesValidAndMissingValues(string value, double? expected)
    {
        var actual = _mapper.ParseImdbScore(value);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParseRottenTomatoesScore_ParsesPercent()
    {
        var ratings = new[]
        {
            new OmdbRating { Source = "Internet Movie Database", Value = "7.4/10" },
            new OmdbRating { Source = "Rotten Tomatoes", Value = "83%" }
        };

        var actual = _mapper.ParseRottenTomatoesScore(ratings);

        Assert.Equal(83, actual);
    }

    [Fact]
    public void Map_ReturnsNullScoresForFalseResponse()
    {
        var item = new OmdbItem { Response = "False", ImdbRating = "9.9" };

        var actual = _mapper.Map(item);

        Assert.Null(actual.ImdbScore);
        Assert.Null(actual.RottenTomatoesScore);
    }

    [Theory]
    [InlineData("https://example.com/poster.jpg", "https://example.com/poster.jpg")]
    [InlineData("N/A", null)]
    [InlineData("not-a-url", null)]
    public void ParsePosterUrl_OnlyReturnsAbsoluteHttpUrls(string value, string? expected)
    {
        var actual = _mapper.ParsePosterUrl(value);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("1,234", 1234)]
    [InlineData("N/A", null)]
    public void ParseImdbVotes_HandlesCommaSeparatedValues(string value, int? expected)
    {
        var actual = _mapper.ParseImdbVotes(value);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("72", 72)]
    [InlineData("N/A", null)]
    public void ParseMetacriticScore_HandlesValidAndMissingValues(string value, int? expected)
    {
        var actual = _mapper.ParseMetacriticScore(value);

        Assert.Equal(expected, actual);
    }
}
