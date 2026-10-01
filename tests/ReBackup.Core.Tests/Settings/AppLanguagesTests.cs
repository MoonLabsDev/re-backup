using System.Globalization;
using FluentAssertions;
using ReBackup.Core.Settings;

namespace ReBackup.Core.Tests.Settings;

public class AppLanguagesTests
{
    [Fact]
    public void German_and_English_are_supported()
    {
        AppLanguages.Supported.Should().Equal("de-DE", "en-US");
    }

    [Theory]
    [InlineData("de-DE", "de-DE")]
    [InlineData("DE-de", "de-DE")]
    [InlineData(" en-US ", "en-US")]
    [InlineData("en-GB", null)]
    [InlineData("de", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_accepts_the_supported_tags_only(string? tag, string? expected)
    {
        AppLanguages.Normalize(tag).Should().Be(expected);
    }

    [Theory]
    [InlineData("de-DE", "de-DE")]
    [InlineData("de-AT", "de-DE")]
    [InlineData("de-CH", "de-DE")]
    [InlineData("en-US", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("", "en-US")]
    public void The_default_is_German_for_a_German_Windows_and_English_otherwise(string uiCulture, string expected)
    {
        AppLanguages.DefaultFor(CultureInfo.GetCultureInfo(uiCulture)).Should().Be(expected);
    }

    [Fact]
    public void Resolve_prefers_the_stored_choice()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");
        var french = CultureInfo.GetCultureInfo("fr-FR");

        AppLanguages.Resolve("en-US", german).Should().Be("en-US");
        AppLanguages.Resolve(null, german).Should().Be("de-DE");
        AppLanguages.Resolve("xx-XX", french).Should().Be("en-US");
    }
}
