using System.Text.RegularExpressions;
using FluentAssertions;
using ReBackup.Core.Localization;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Localization;

public class CoreTextsTests
{
    private static readonly Regex CoreKey = new(
        @"\b(?:Message\.Of|CoreTexts\.English)\(\s*""([^""]+)""\s*[,)]", RegexOptions.CultureInvariant);

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
}
