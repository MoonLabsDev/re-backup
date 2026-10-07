using System.Globalization;
using FluentAssertions;
using ReBackup.Shared.Localization;

namespace ReBackup.Core.Tests.Localization;

public class LabelsTests
{
    private static readonly LabelSet English = LabelSet.Parse("""
        { "a": "English A", "b": "English B {n:N0}", "raw": "{text}",
          "outer": "Rule {index}: {problem}", "inner": "inner {x}" }
        """);

    private static readonly LabelSet German = LabelSet.Parse("""
        { "a": "Deutsch A", "raw": "{text}", "outer": "Regel {index}: {problem}", "inner": "innen {x}" }
        """);

    private static Labels De() => new(English, German, CultureInfo.GetCultureInfo("de-DE"));

    [Fact]
    public void The_chosen_language_comes_first()
    {
        De().Get("a").Should().Be("Deutsch A");
    }

    [Fact]
    public void A_label_missing_in_the_chosen_language_falls_back_to_English_with_the_chosen_culture()
    {
        De().Format(Message.Of("b", ("n", 1234))).Should().Be("English B 1.234");
    }

    [Fact]
    public void An_unknown_key_shows_the_key()
    {
        De().Get("no.such.key").Should().Be("no.such.key");
        De().Format(Message.Of("no.such", ("x", 1))).Should().Be("no.such");
    }

    [Fact]
    public void Nested_messages_are_rendered_in_the_same_language()
    {
        De().Format(Message.Of("outer", ("index", 2), ("problem", Message.Of("inner", ("x", "y")))))
            .Should().Be("Regel 2: innen y");
    }

    [Fact]
    public void Raw_shows_the_text_as_it_is()
    {
        De().Format(Message.Raw("Zugriff verweigert.")).Should().Be("Zugriff verweigert.");
        De().Culture.Name.Should().Be("de-DE");
    }

    private static readonly LabelSet PluralEnglish = LabelSet.Parse("""
        { "files": { "one": "{count} file", "other": "{count:N0} files" }, "onlyEnglish": { "one": "one", "other": "many" } }
        """);

    private static readonly LabelSet PluralGerman = LabelSet.Parse("""
        { "files": { "one": "{count} Datei", "other": "{count:N0} Dateien" } }
        """);

    private static Labels PluralDe() => new(PluralEnglish, PluralGerman, CultureInfo.GetCultureInfo("de-DE"));

    [Theory]
    [InlineData(1, "1 Datei")]
    [InlineData(-1, "-1 Datei")]
    [InlineData(0, "0 Dateien")]
    [InlineData(2, "2 Dateien")]
    [InlineData(1234, "1.234 Dateien")]
    public void The_count_chooses_one_or_other(int count, string expected)
    {
        PluralDe().Format(Message.Of("files", ("count", count))).Should().Be(expected);
    }

    [Fact]
    public void Every_number_type_counts_and_a_fraction_is_other()
    {
        PluralDe().Format(Message.Of("files", ("count", 1L))).Should().Be("1 Datei");
        PluralDe().Format(Message.Of("files", ("count", 1.0))).Should().Be("1 Datei");
        PluralDe().Format(Message.Of("files", ("count", (ushort)1))).Should().Be("1 Datei");
        PluralDe().Format(Message.Of("files", ("count", 1.5))).Should().Be("2 Dateien", "1.5 takes the other form; N0 rounds it");
        new Labels(PluralEnglish, PluralEnglish, CultureInfo.GetCultureInfo("en-US"))
            .Format(Message.Of("files", ("count", 2))).Should().Be("2 files");
    }

    [Fact]
    public void Without_a_count_the_other_form_is_used()
    {
        PluralDe().Format(Message.Of("files")).Should().Be("{count:N0} Dateien");
        PluralDe().Format(Message.Of("files", ("count", "1"))).Should().Be("1 Dateien");
        PluralDe().Get("files").Should().Be("{count:N0} Dateien");
    }

    [Fact]
    public void A_plural_missing_in_the_chosen_language_falls_back_to_the_English_plural()
    {
        PluralDe().Format(Message.Of("onlyEnglish", ("count", 1))).Should().Be("one");
        PluralDe().Format(Message.Of("onlyEnglish", ("count", 3))).Should().Be("many");
    }

    [Fact]
    public void A_nested_plural_message_is_rendered_with_its_own_count()
    {
        var english = LabelSet.Parse("""{ "files": { "one": "{count} file", "other": "{count} files" }, "s": "{a} and {b}" }""");
        var labels = new Labels(english, english, CultureInfo.GetCultureInfo("en-US"));

        labels.Format(Message.Of("s", ("a", Message.Of("files", ("count", 1))), ("b", Message.Of("files", ("count", 2)))))
            .Should().Be("1 file and 2 files");
    }
}
