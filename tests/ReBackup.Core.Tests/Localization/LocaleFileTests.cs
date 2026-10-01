using System.Globalization;
using FluentAssertions;
using ReBackup.Core.Localization;
using ReBackup.Core.Settings;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Localization;

/// <summary>The App's label files (src/ReBackup.App/Locales), read from the repository.</summary>
public class LocaleFileTests
{
    internal static LabelSet Load(string language) => LabelSet.Parse(File.ReadAllText(RepoPaths.LocaleFile(language)));

    [Fact]
    public void There_is_one_file_per_supported_language()
    {
        Directory.GetFiles(Path.Combine(RepoPaths.AppDirectory, "Locales"), "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Should().BeEquivalentTo(AppLanguages.Supported);
        foreach (var language in AppLanguages.Supported)
            Load(language).Entries.Should().NotBeEmpty();
    }

    [Fact]
    public void Both_files_have_the_same_keys()
    {
        var english = Load(AppLanguages.English).Entries.Keys.ToList();
        var german = Load(AppLanguages.German).Entries.Keys.ToList();

        english.Except(german).Should().BeEmpty("every English label needs a German one");
        german.Except(english).Should().BeEmpty("German must not have labels English lacks");
    }

    [Fact]
    public void Both_files_use_the_same_placeholders_per_key()
    {
        var english = Load(AppLanguages.English);
        var german = Load(AppLanguages.German);

        var different = english.Entries
            .Where(pair => german.TryGet(pair.Key, out var text) &&
                           !LabelFormat.Placeholders(pair.Value).Order(StringComparer.Ordinal)
                               .SequenceEqual(LabelFormat.Placeholders(text).Order(StringComparer.Ordinal)))
            .Select(pair => pair.Key)
            .ToList();

        different.Should().BeEmpty();
    }

    [Fact]
    public void No_label_is_blank()
    {
        foreach (var language in AppLanguages.Supported)
        {
            Load(language).Entries.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key)
                .Should().BeEmpty(language);
        }
    }

    [Fact]
    public void Date_formats_format_a_date()
    {
        var date = new DateTime(2026, 10, 2, 14, 5, 9);
        foreach (var language in AppLanguages.Supported)
        {
            var culture = CultureInfo.GetCultureInfo(language);
            foreach (var pair in Load(language).Entries.Where(pair => pair.Key.StartsWith("format.", StringComparison.Ordinal)))
                FluentActions.Invoking(() => date.ToString(pair.Value, culture)).Should().NotThrow(pair.Key);
        }
        date.ToString(Load(AppLanguages.German).Entries["format.dateTime"], CultureInfo.GetCultureInfo("de-DE"))
            .Should().Be("02.10.2026 14:05");
    }


    [Fact]
    public void The_English_file_holds_exactly_the_Core_templates()
    {
        var english = Load(AppLanguages.English);

        CoreTexts.Templates
            .Where(pair => !english.TryGet(pair.Key, out var text) || text != pair.Value)
            .Select(pair => pair.Key)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(AppLanguages.English)]
    [InlineData(AppLanguages.German)]
    public void The_core_keys_of_a_file_are_exactly_the_Core_templates(string language)
    {
        var fileKeys = Load(language).Entries.Keys.Where(key => key.StartsWith("core.", StringComparison.Ordinal));

        fileKeys.Should().BeEquivalentTo(CoreTexts.Templates.Keys);
    }

    [Fact]
    public void A_stored_English_reason_is_shown_in_German()
    {
        var labels = new Labels(Load(AppLanguages.English), Load(AppLanguages.German), CultureInfo.GetCultureInfo("de-DE"));
        var stored = "The plan name \"Projects.\" cannot be used: Name must not end with a dot.";

        labels.Format(CoreTexts.Recognize(stored)!)
            .Should().Be("Der Planname „Projects.“ kann nicht verwendet werden: Der Name darf nicht mit einem Punkt enden.");
    }
}
