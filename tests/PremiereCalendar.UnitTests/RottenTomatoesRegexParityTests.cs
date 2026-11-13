using System.Reflection;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class RottenTomatoesRegexParityTests
{
    private static readonly MethodInfo AttributeValue = typeof(RottenTomatoesClient)
        .GetMethod("AttributeValue", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ParseSearchResults = typeof(RottenTomatoesClient)
        .GetMethod("ParseSearchResults", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string? Value(string attributes, string name) => (string?)AttributeValue.Invoke(null, [attributes, name]);

    [Fact]
    public void AttributeValueIsCaseInsensitiveBoundaryAwareAndDecodesHtml()
    {
        const string attributes = "Release-Year='2026' data-release-year=\"1999\" tomatometer-score = \"72\" title=\"A &amp; B\"";
        Assert.Equal("2026", Value(attributes, "release-year"));
        Assert.Equal("72", Value(attributes, "tomatometer-score"));
        Assert.Equal("A & B", Value(attributes, "title"));
        Assert.Null(Value(attributes, "start-year"));
        Assert.Null(Value("data-release-year=\"1999\"", "release-year"));
        // Repeated lookups reuse the cached expression and keep returning the same answers.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("72", Value(attributes, "tomatometer-score"));
            Assert.Equal("2026", Value(attributes, "RELEASE-YEAR"));
        }
    }

    [Fact]
    public void AttributeValueEscapesRegexMetacharactersInNames()
    {
        Assert.Equal("x", Value("a.b=\"x\"", "a.b"));
        Assert.Null(Value("aXb=\"x\"", "a.b"));
    }

    [Fact]
    public void SearchRowsAreParsedWithGeneratedExpressions()
    {
        const string html = """
            <search-page-media-row release-year="2026" tomatometer-score="72">
              <a href="https://www.rottentomatoes.com/m/saccharine" data-qa="info-name" slot="title">Saccharine &amp; Co</a>
            </search-page-media-row>
            <SEARCH-PAGE-MEDIA-ROW startyear="2020" tomatometerscore="95">
              <A HREF='https://www.rottentomatoes.com/tv/the_boroughs' DATA-QA='info-name'><b>The</b> Boroughs</A>
            </SEARCH-PAGE-MEDIA-ROW>
            <search-page-media-row><span>no link</span></search-page-media-row>
            """;
        var results = ((System.Collections.IEnumerable)ParseSearchResults.Invoke(null, [html])!).Cast<object>().ToArray();
        Assert.Equal(2, results.Length);
        var titles = results.Select(item => (string)item.GetType().GetProperty("Title")!.GetValue(item)!).ToArray();
        Assert.Equal(["Saccharine & Co", "The Boroughs"], titles);
    }
}
