using System.Globalization;
using FluentAssertions;
using ReBackup.Core.Localization;

namespace ReBackup.Core.Tests.Localization;

public class LabelFormatTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private static IReadOnlyDictionary<string, object?> Args(params (string Name, object? Value)[] args) =>
        Message.Of("x", args).Args;

    [Fact]
    public void Named_placeholders_are_replaced()
    {
        LabelFormat.Format("Backup of \"{plan}\" completed in {duration}.", Args(("plan", "Docs"), ("duration", "3 s")), En)
            .Should().Be("Backup of \"Docs\" completed in 3 s.");
    }

    [Fact]
    public void Numbers_are_formatted_with_the_culture()
    {
        LabelFormat.Format("{count:N0} files", Args(("count", 18690)), De).Should().Be("18.690 files");
        LabelFormat.Format("{count:N0} files", Args(("count", 18690)), En).Should().Be("18,690 files");
        LabelFormat.Format("{value}", Args(("value", 1.5)), De).Should().Be("1,5");
        LabelFormat.Format("{minutes:00}", Args(("minutes", 5)), De).Should().Be("05");
    }

    [Fact]
    public void Double_braces_are_literal_braces()
    {
        LabelFormat.Format("{{plan}} is {plan}}}", Args(("plan", "x")), En).Should().Be("{plan} is x}");
    }

    [Fact]
    public void A_missing_argument_stays_visible()
    {
        LabelFormat.Format("Hello {name} {count:N0}", Args(), En).Should().Be("Hello {name} {count:N0}");
    }

    [Fact]
    public void Braces_around_something_that_is_no_name_are_text()
    {
        LabelFormat.Format("{ } {1a} {a-b} {", Args(("a", "x")), En).Should().Be("{ } {1a} {a-b} {");
    }

    [Fact]
    public void Null_is_empty_and_messages_are_rendered()
    {
        var args = Args(("a", null), ("b", Message.Of("inner")));

        LabelFormat.Format("[{a}] {b}", args, En, message => "<" + message.Key + ">").Should().Be("[] <inner>");
        LabelFormat.Format("[{a}] {b}", args, En).Should().Be("[] inner");
    }

    [Fact]
    public void Placeholders_are_listed_once_in_order_of_appearance()
    {
        LabelFormat.Placeholders("{b} and {a:N0} and {b} {{c}}").Should().Equal("b", "a");
        LabelFormat.Placeholders("no placeholders").Should().BeEmpty();
    }

    [Fact]
    public void Match_reads_the_arguments_back()
    {
        var args = LabelFormat.Match("Source folder \"{source}\" does not exist.", "Source folder \"C:\\Data\" does not exist.");

        args.Should().NotBeNull();
        args!["source"].Should().Be(@"C:\Data");
    }

    [Fact]
    public void Match_is_null_for_other_texts()
    {
        LabelFormat.Match("No target folder is set.", "Something else.").Should().BeNull();
        LabelFormat.Match("No target folder is set.", "No target folder is set. And more").Should().BeNull();
    }

    [Fact]
    public void Match_handles_repeated_placeholders_and_line_breaks()
    {
        var args = LabelFormat.Match("{a}-{a}\n{b}", "x-x\ny\nz");

        args.Should().NotBeNull();
        args!["a"].Should().Be("x");
        args["b"].Should().Be("y\nz");
        LabelFormat.Match("{a}-{a}", "x-y").Should().BeNull();
    }

    [Fact]
    public void LiteralLength_counts_the_text_outside_placeholders()
    {
        LabelFormat.LiteralLength("ab{c}d{{").Should().Be(4);
        LabelFormat.LiteralLength("{text}").Should().Be(0);
    }
}
