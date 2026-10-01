using System.Text.RegularExpressions;
using FluentAssertions;
using ReBackup.Core.Localization;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Localization;

public class CoreTextsTests
{
    private static readonly Regex CoreKey = new(
        @"""(core\.[A-Za-z0-9.]+)""", RegexOptions.CultureInvariant);

    [Fact]
    public void English_renders_a_message_and_its_nested_messages()
    {
        CoreTexts.English(Message.Of("core.plan.retentionRule", ("index", 2),
                ("problem", Message.Of("core.retention.keep", ("max", 9999)))))
            .Should().Be("Retention rule 2: keep must be a number from 1 to 9999.");
        CoreTexts.English("core.plan.nameTaken", ("name", "Docs")).Should().Be("Another plan is already named \"Docs\".");
    }

    [Fact]
    public void ToEnglish_of_no_message_is_null_and_an_unknown_key_renders_as_the_key()
    {
        ((Message?)null).ToEnglish().Should().BeNull();
        CoreTexts.English(Message.Of("core.none")).Should().Be("core.none");
    }

    [Fact]
    public void Every_template_is_a_core_key()
    {
        CoreTexts.Templates.Keys.Should().OnlyContain(key => key.StartsWith("core.", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_key_Core_uses_has_an_English_template()
    {
        var missing = RepoPaths.SourceFiles(RepoPaths.CoreDirectory, "*.cs")
            .SelectMany(file => CoreKey.Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value))
            .Where(key => !CoreTexts.Templates.ContainsKey(key))
            .Distinct()
            .ToList();

        missing.Should().BeEmpty();
    }

    public static TheoryData<string> AllKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in CoreTexts.Templates.Keys)
            data.Add(key);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void Every_English_text_is_recognized_again(string key)
    {
        var args = LabelFormat.Placeholders(CoreTexts.Templates[key])
            .Select((name, i) => (name, (object?)$"x{i}"))
            .ToArray();
        var message = Message.Of(key, args);

        CoreTexts.Recognize(CoreTexts.English(message)).Should().Be(message);
    }

    [Fact]
    public void Nested_texts_are_recognized_too()
    {
        CoreTexts.Recognize("The plan name \"Projects.\" cannot be used: Name must not end with a dot.")
            .Should().Be(Message.Of("core.run.nameUnusable", ("name", "Projects."), ("problem", Message.Of("core.plan.nameDot"))));
    }

    [Fact]
    public void The_most_specific_template_wins_and_windows_messages_stay_text()
    {
        CoreTexts.Recognize("Retention was skipped: the plan no longer exists.")
            .Should().Be(Message.Of("core.run.retentionPlanGone"));
        CoreTexts.Recognize("Retention could not delete \"2026_10_01-11_34 Docs\": Access to the path is denied.")
            .Should().Be(Message.Of("core.run.retentionDeleteFailed",
                ("version", "2026_10_01-11_34 Docs"), ("error", "Access to the path is denied.")));
    }

    [Fact]
    public void The_parameter_suffix_of_an_argument_exception_is_ignored()
    {
        CoreTexts.Recognize("The destination must be an absolute path. (Parameter 'destinationRoot')")
            .Should().Be(Message.Of("core.restore.destinationNotAbsolute"));
    }

    [Fact]
    public void Unknown_texts_are_not_recognized()
    {
        CoreTexts.Recognize("device not ready").Should().BeNull();
        CoreTexts.Recognize("").Should().BeNull();
        CoreTexts.Recognize(null).Should().BeNull();
    }
}
