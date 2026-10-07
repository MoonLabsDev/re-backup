using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Settings;

namespace ReBackup.Core.Tests.Localization;

/// <summary>The App's sources: every label key they use exists, and XAML shows no literal text.</summary>
public class AppSourceTests
{
    private static readonly string[] TextProperties =
        ["Text", "Content", "Header", "ToolTip", "Title", "AutomationProperties.Name", "AutomationProperties.HelpText"];

    /// <summary>Elements whose content is shown as text (others hold colours, font names and the like).</summary>
    private static readonly HashSet<string> TextElements = new(StringComparer.Ordinal)
    {
        "TextBlock", "Run", "Span", "Bold", "Italic", "Underline", "Hyperlink", "Label", "Button", "CheckBox",
        "RadioButton", "ToggleButton", "MenuItem", "ToolTip", "GroupBox", "Expander", "TabItem", "ComboBoxItem",
        "ListBoxItem", "Window", "UserControl",
    };

    /// <summary>Literal texts that are the same in every language.</summary>
    private static readonly HashSet<string> AllowedLiterals = new(StringComparer.Ordinal) { "ReBackup" };

    private const string SystemNamespace = "clr-namespace:System;assembly=mscorlib";

    private static readonly Regex XamlKey = new(@"\{l:Loc\s+([^\s,}]+)\s*\}", RegexOptions.CultureInvariant);
    private static readonly Regex XamlPrefix = new(@"\{l:LocBind\b[^}]*?\bPrefix=([^\s,}]+)", RegexOptions.CultureInvariant);
    private static readonly Regex CodeKey = new(
        @"\b(?:Loc\.T|Loc\.F|LocText\.Of|Message\.Of|CoreTexts\.English)\(\s*""([^""]+)""\s*[,)]", RegexOptions.CultureInvariant);
    private static readonly Regex BindKey = new(@"\bLoc\.Bind\([^;]*?,\s*""([^""]+)""\s*\)", RegexOptions.CultureInvariant);

    private static string Relative(string path) => Path.GetRelativePath(RepoPaths.AppDirectory, path).Replace('\\', '/');

    [Fact]
    public void Xaml_shows_no_literal_texts()
    {
        var literals = RepoPaths.SourceFiles(RepoPaths.AppDirectory, "*.xaml")
            .SelectMany(file => Literals(File.ReadAllText(file)).Select(l => $"{Relative(file)}:{l.Line} {l.Where}=\"{l.Value}\""))
            .ToList();

        literals.Should().BeEmpty();
    }

    [Fact]
    public void Every_label_key_used_in_the_sources_exists_in_the_English_file()
    {
        var english = LocaleFileTests.LoadAll(AppLanguages.English);
        var missing = new List<string>();

        foreach (var file in RepoPaths.SourceFiles(RepoPaths.AppDirectory, "*.xaml"))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in XamlKey.Matches(text))
            {
                if (!english.TryGet(match.Groups[1].Value, out _))
                    missing.Add($"{Relative(file)}: {match.Groups[1].Value}");
            }
            foreach (Match match in XamlPrefix.Matches(text))
            {
                if (!english.Entries.Keys.Any(key => key.StartsWith(match.Groups[1].Value, StringComparison.Ordinal)))
                    missing.Add($"{Relative(file)}: {match.Groups[1].Value}*");
            }
        }

        var code = RepoPaths.SourceFiles(RepoPaths.AppDirectory, "*.cs")
            .Concat(RepoPaths.SourceFiles(RepoPaths.CoreDirectory, "*.cs"));
        foreach (var file in code)
        {
            var text = File.ReadAllText(file);
            foreach (var match in CodeKey.Matches(text).Concat(BindKey.Matches(text)))
            {
                if (!english.TryGet(match.Groups[1].Value, out _))
                    missing.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}");
            }
        }

        missing.Should().BeEmpty();
    }

    /// <summary>Texts written into XAML: text properties (attributes and setters) and the content of text elements.</summary>
    internal static IEnumerable<(int Line, string Where, string Value)> Literals(string xaml)
    {
        var document = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
        foreach (var element in document.Descendants())
        {
            if (element.Name.NamespaceName == SystemNamespace)
                continue;   // resources such as the icon glyphs
            var line = ((IXmlLineInfo)element).LineNumber;

            foreach (var attribute in element.Attributes())
            {
                if (TextProperties.Contains(attribute.Name.LocalName) && IsLiteral(attribute.Value))
                    yield return (line, attribute.Name.LocalName, attribute.Value);
            }

            if (element.Name.LocalName == "Setter" &&
                element.Attribute("Property")?.Value is { } property && TextProperties.Contains(property) &&
                element.Attribute("Value")?.Value is { } value && IsLiteral(value))
                yield return (line, "Setter " + property, value);

            if (!TextElements.Contains(element.Name.LocalName))
                continue;
            foreach (var text in element.Nodes().OfType<XText>())
            {
                var content = text.Value.Trim();
                if (IsLiteral(content))
                    yield return (line, element.Name.LocalName, content);
            }
        }
    }

    private static bool IsLiteral(string value) =>
        value.Length > 0 &&
        (!value.StartsWith('{') || value.StartsWith("{}", StringComparison.Ordinal)) &&
        value.Any(char.IsLetter) &&
        !AllowedLiterals.Contains(value);
}
