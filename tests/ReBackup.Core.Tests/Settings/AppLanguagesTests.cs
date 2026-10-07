using System.Globalization;
using FluentAssertions;
using ReBackup.Shared.Settings;

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

    [Fact]
    public void The_default_is_English()
    {
        AppLanguages.Default.Should().Be("en-US");
    }

    [Fact]
    public void Resolve_prefers_the_stored_choice_and_falls_back_to_English()
    {
        AppLanguages.Resolve("de-DE").Should().Be("de-DE");
        AppLanguages.Resolve("en-US").Should().Be("en-US");
        AppLanguages.Resolve(null).Should().Be("en-US");
        AppLanguages.Resolve("xx-XX").Should().Be("en-US");
    }
}
