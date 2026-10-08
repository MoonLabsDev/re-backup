using System.Reflection;
using FluentAssertions;
using ReBackup.Shared.Localization;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.S3;
using ReBackup.Storage.S3;

namespace ReBackup.Shared.Wpf.Tests;

/// <summary>The keys the dialog takes from code (check results, validation, placeholders) are in both embedded wpf label files.</summary>
public class S3DialogLabelTests
{
    private static LabelSet Embedded(string language)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"ReBackup.Shared.Wpf.Locales.wpf.{language}.json");
        stream.Should().NotBeNull();
        return LabelSet.Parse(stream!);
    }

    private static IEnumerable<string> ConstantsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void Every_key_from_code_has_a_label(string language)
    {
        var keys = ConstantsOf(typeof(S3MessageKeys))
            .Concat(ConstantsOf(typeof(S3ConnectionDialogViewModel)))
            .Concat(["s3.dialog.titleNew", "s3.dialog.titleEdit"])
            .ToList();
        var set = Embedded(language);

        keys.Should().HaveCountGreaterThan(15);
        keys.Where(key => !set.Entries.ContainsKey(key)).Should().BeEmpty();
    }

    [Fact]
    public void Every_check_has_a_name_and_every_state_a_symbol()
    {
        foreach (var check in Enum.GetValues<S3Check>())
            S3DialogTexts.NameOf(check).Should().StartWith("s3.test.check.", "Loc is not configured here, so the key shows");
        Enum.GetValues<S3CheckState>().Select(S3DialogTexts.SymbolOf).Should().Equal("✓", "✗", "!", "–");
    }
}
