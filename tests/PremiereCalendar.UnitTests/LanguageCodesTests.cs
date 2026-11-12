using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class LanguageCodesTests
{
    [Theory]
    [InlineData("nl-BE", "nl")]
    [InlineData("nl_NL", "nl")]
    [InlineData("NL", "nl")]
    [InlineData(" nl ", "nl")]
    [InlineData("vls", "nl")]
    [InlineData("nld", "nl")]
    [InlineData("dut", "nl")]
    [InlineData("Flemish", "nl")]
    [InlineData("Vlaams", "nl")]
    [InlineData("Dutch", "nl")]
    [InlineData("Nederlands", "nl")]
    [InlineData("en-GB", "en")]
    [InlineData("pt-BR", "pt")]
    [InlineData("zh-Hans", "zh")]
    [InlineData("xx", "xx")]
    [InlineData("FR", "fr")]
    public void Normalize_ReturnsTheLowerCasePrimaryLanguageCode(string value, string expected)
    {
        Assert.Equal(expected, LanguageCodes.Normalize(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-BE")]
    public void Normalize_ReturnsNullForBlankValues(string? value)
    {
        Assert.Null(LanguageCodes.Normalize(value));
    }
}
