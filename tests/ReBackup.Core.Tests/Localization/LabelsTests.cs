using System.Globalization;
using FluentAssertions;
using ReBackup.Core.Localization;

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
}
