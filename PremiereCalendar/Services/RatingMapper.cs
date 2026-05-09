using System.Globalization;
using System.Text.RegularExpressions;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class RatingMapper
{
    public ExternalRatings Map(OmdbItem? item)
    {
        if (item is null || string.Equals(item.Response, "False", StringComparison.OrdinalIgnoreCase))
        {
            return new ExternalRatings(null, null);
        }

        return new ExternalRatings(
            ParseImdbScore(item.ImdbRating),
            ParseRottenTomatoesScore(item.Ratings),
            ParsePosterUrl(item.Poster),
            ParseImdbVotes(item.ImdbVotes),
            ParseMetacriticScore(item.Metascore),
            ParsePlot(item.Plot));
    }

    public double? ParseImdbScore(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var score)
            ? score
            : null;
    }

    public int? ParseRottenTomatoesScore(IEnumerable<OmdbRating>? ratings)
    {
        var value = ratings?
            .FirstOrDefault(x => string.Equals(x.Source, "Rotten Tomatoes", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var percent = value.Trim().TrimEnd('%');
        return int.TryParse(percent, NumberStyles.Integer, CultureInfo.InvariantCulture, out var score)
            ? score
            : null;
    }

    public string? ParsePosterUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }

    public int? ParseImdbVotes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = value.Replace(",", "", StringComparison.Ordinal);
        return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var votes)
            ? votes
            : null;
    }

    public int? ParseMetacriticScore(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var score)
            ? score
            : null;
    }

    private static string? ParsePlot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Regex.Replace(value.Trim(), "\\s+", " ");
    }
}
