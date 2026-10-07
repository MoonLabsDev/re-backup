using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using ReBackup.Core.Localization;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Localization;
using ReBackup.Shared.Settings;

namespace ReBackup.Core.Tests.Localization;

/// <summary>The label files of the App (src/ReBackup.App/Locales) and of the shared library (src/ReBackup.Shared/Locales), read from the repository.</summary>
public class LocaleFileTests
{
    internal static LabelSet Load(string language) => LabelSet.Parse(File.ReadAllText(RepoPaths.LocaleFile(language)));

    internal static LabelSet LoadShared(string language) =>
        LabelSet.Parse(File.ReadAllText(RepoPaths.SharedLocaleFile(language)));

    internal static LabelSet LoadWpf(string language) =>
        LabelSet.Parse(File.ReadAllText(RepoPaths.SharedWpfLocaleFile(language)));

    /// <summary>The labels as the App sees them: shared, Shared.Wpf and App files merged (the app's last).</summary>
    internal static LabelSet LoadAll(string language) => LoadShared(language).Merge(LoadWpf(language)).Merge(Load(language));

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
            .Where(pair => german.Entries.TryGetValue(pair.Key, out var text) &&
                           !LabelFormat.PlaceholderSpecs(pair.Value).Order(StringComparer.Ordinal)
                               .SequenceEqual(LabelFormat.PlaceholderSpecs(text).Order(StringComparer.Ordinal)))
            .Select(pair => $"{pair.Key}: {string.Join(", ", LabelFormat.PlaceholderSpecs(pair.Value))} / " +
                            string.Join(", ", LabelFormat.PlaceholderSpecs(german.Entries[pair.Key])))
            .ToList();

        different.Should().BeEmpty("name and format of every placeholder must match");
    }

    [Fact]
    public void Both_files_have_the_same_plural_labels()
    {
        Load(AppLanguages.German).Plurals.Should().BeEquivalentTo(Load(AppLanguages.English).Plurals);
        LoadAll(AppLanguages.English).Plurals.Should().Contain("common.fileCount");
    }

    [Theory]
    [InlineData(AppLanguages.English)]
    [InlineData(AppLanguages.German)]
    public void The_forms_of_a_plural_label_use_the_same_placeholder_names(string language)
    {
        var set = LoadAll(language);

        set.Plurals
            .Where(key => !LabelFormat.Placeholders(set.Entries[key + ".one"]).Order(StringComparer.Ordinal)
                .SequenceEqual(LabelFormat.Placeholders(set.Entries[key + ".other"]).Order(StringComparer.Ordinal)))
            .Should().BeEmpty();
        set.Plurals.Where(key => !LabelFormat.Placeholders(set.Entries[key + ".other"]).Contains("count"))
            .Should().BeEmpty("the count argument chooses the form");
    }

    [Theory]
    [InlineData(AppLanguages.English)]
    [InlineData(AppLanguages.German)]
    public void No_label_writes_a_plural_in_parentheses(string language)
    {
        var pattern = new Regex(@"\p{L}\((s|es|en|e|n)\)", RegexOptions.CultureInvariant);

        LoadAll(language).Entries.Where(pair => pattern.IsMatch(pair.Value)).Select(pair => pair.Key)
            .Should().BeEmpty("a count text is a plural label with one and other forms");
    }

    private static Labels LabelsFor(string language) =>
        new(LoadAll(AppLanguages.English), LoadAll(language), CultureInfo.GetCultureInfo(language));

    [Theory]
    [InlineData(AppLanguages.English, 1, "1 file")]
    [InlineData(AppLanguages.English, 2, "2 files")]
    [InlineData(AppLanguages.English, 1234, "1,234 files")]
    [InlineData(AppLanguages.German, 1, "1 Datei")]
    [InlineData(AppLanguages.German, 2, "2 Dateien")]
    [InlineData(AppLanguages.German, 0, "0 Dateien")]
    [InlineData(AppLanguages.German, 1234, "1.234 Dateien")]
    public void The_file_count_has_a_singular(string language, int count, string expected)
    {
        LabelsFor(language).Format(Message.Of("common.fileCount", ("count", count))).Should().Be(expected);
    }

    [Theory]
    [InlineData(AppLanguages.English, "shell.queue.queued", 1, "1 backup queued")]
    [InlineData(AppLanguages.English, "shell.queue.queued", 2, "2 backups queued")]
    [InlineData(AppLanguages.German, "shell.queue.queued", 1, "1 Backup eingereiht")]
    [InlineData(AppLanguages.German, "shell.queue.queued", 2, "2 Backups eingereiht")]
    [InlineData(AppLanguages.English, "versions.search.count", 1, "1 match")]
    [InlineData(AppLanguages.German, "versions.search.count", 1, "1 Treffer")]
    [InlineData(AppLanguages.English, "restore.unreadableNote", 1, " 1 part of the version could not be read.")]
    [InlineData(AppLanguages.German, "restore.unreadableNote", 1, " 1 Teil der Version konnte nicht gelesen werden.")]
    [InlineData(AppLanguages.German, "restore.unreadableNote", 3, " 3 Teile der Version konnten nicht gelesen werden.")]
    public void Count_texts_read_naturally_for_one_and_more(string language, string key, int count, string expected)
    {
        LabelsFor(language).Format(Message.Of(key, ("count", count))).Should().Be(expected);
    }

    [Fact]
    public void Texts_with_several_counts_use_the_count_labels()
    {
        var files = (int count) => Message.Of("common.fileCount", ("count", count));
        var folders = (int count) => Message.Of("common.folderCount", ("count", count));

        LabelsFor(AppLanguages.German)
            .Format(Message.Of("ignore.pill.inBackup", ("prefix", ""), ("size", "4 KB"), ("files", files(1))))
            .Should().Be("im Backup 4 KB · 1 Datei");
        LabelsFor(AppLanguages.English)
            .Format(Message.Of("ignore.pill.ignored", ("prefix", ""), ("size", "4 KB"), ("files", files(1))))
            .Should().Be("ignored 4 KB · 1 file");
        LabelsFor(AppLanguages.German)
            .Format(Message.Of("ignore.progress.indexed", ("files", files(1)), ("folders", folders(1))))
            .Should().Be("Indiziert: 1 Datei, 1 Ordner.");
        LabelsFor(AppLanguages.English)
            .Format(Message.Of("ignore.progress.indexed", ("files", files(2)), ("folders", folders(1))))
            .Should().Be("Indexed 2 files in 1 folder.");
        LabelsFor(AppLanguages.German)
            .Format(Message.Of("restore.conflictsFolder", ("count", 1), ("folder", @"C:\x"), ("examples", "a"), ("more", "")))
            .Should().StartWith(@"1 Datei existiert bereits in C:\x:");
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
            foreach (var pair in LoadWpf(language).Entries.Where(pair => pair.Key.StartsWith("format.", StringComparison.Ordinal)))
                FluentActions.Invoking(() => date.ToString(pair.Value, culture)).Should().NotThrow(pair.Key);
        }
        date.ToString(LoadWpf(AppLanguages.German).Entries["format.dateTime"], CultureInfo.GetCultureInfo("de-DE"))
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
    public void The_shared_library_has_one_file_per_supported_language()
    {
        Directory.GetFiles(Path.Combine(RepoPaths.SharedDirectory, "Locales"), "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Should().BeEquivalentTo(AppLanguages.Supported.Select(language => "shared." + language));
        foreach (var language in AppLanguages.Supported)
            LoadShared(language).Entries.Should().NotBeEmpty();
    }

    [Fact]
    public void Both_shared_files_have_the_same_keys()
    {
        var english = LoadShared(AppLanguages.English).Entries.Keys.ToList();
        var german = LoadShared(AppLanguages.German).Entries.Keys.ToList();

        english.Except(german).Should().BeEmpty("every English label needs a German one");
        german.Except(english).Should().BeEmpty("German must not have labels English lacks");
    }

    [Fact]
    public void Both_shared_files_use_the_same_placeholders_per_key()
    {
        var english = LoadShared(AppLanguages.English);
        var german = LoadShared(AppLanguages.German);

        english.Entries
            .Where(pair => german.Entries.TryGetValue(pair.Key, out var text) &&
                           !LabelFormat.PlaceholderSpecs(pair.Value).Order(StringComparer.Ordinal)
                               .SequenceEqual(LabelFormat.PlaceholderSpecs(text).Order(StringComparer.Ordinal)))
            .Select(pair => pair.Key)
            .Should().BeEmpty("name and format of every placeholder must match");
    }

    [Fact]
    public void Both_shared_files_have_the_same_plural_labels() =>
        LoadShared(AppLanguages.German).Plurals.Should().BeEquivalentTo(LoadShared(AppLanguages.English).Plurals);

    [Fact]
    public void Shared_english_file_holds_exactly_the_shared_texts() =>
        LoadShared(AppLanguages.English).Entries.Should().Equal(SharedTexts.Templates);

    [Theory]
    [InlineData(AppLanguages.English)]
    [InlineData(AppLanguages.German)]
    public void The_shared_keys_are_not_in_the_App_files(string language)
    {
        var shared = LoadShared(language).Entries.Keys;

        Load(language).Entries.Keys.Intersect(shared).Should().BeEmpty();
        shared.Should().OnlyContain(key => key.StartsWith("shared.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stored_English_retention_text_is_shown_in_German_through_the_shared_file()
    {
        var german = LoadShared(AppLanguages.German).Merge(Load(AppLanguages.German));
        var labels = new Labels(LoadShared(AppLanguages.English).Merge(Load(AppLanguages.English)), german,
            CultureInfo.GetCultureInfo("de-DE"));

        labels.Format(SharedTexts.Recognize("keep must be a number from 1 to 9999.")!)
            .Should().Be("die Anzahl muss eine Zahl von 1 bis 9999 sein.");
    }

    [Fact]
    public void A_stored_English_reason_is_shown_in_German()
    {
        var labels = new Labels(Load(AppLanguages.English), Load(AppLanguages.German), CultureInfo.GetCultureInfo("de-DE"));
        var stored = "The plan name \"Projects.\" cannot be used: Name must not end with a dot.";

        labels.Format(CoreTexts.Recognize(stored)!)
            .Should().Be("Der Planname „Projects.“ kann nicht verwendet werden: Der Name darf nicht mit einem Punkt enden.");
    }

    /// <summary>The culture in the file names must not turn them into satellite assemblies (WithCulture="false").</summary>
    [Fact]
    public void The_shared_library_embeds_its_label_files_under_the_name_Loc_reads()
    {
        typeof(SharedTexts).Assembly.GetManifestResourceNames()
            .Should().BeEquivalentTo(AppLanguages.Supported.Select(language => $"ReBackup.Shared.Locales.shared.{language}.json"));
    }

    /// <summary>The project file, not a built App: the build in its bin folder may be older than the sources.</summary>
    [Fact]
    public void The_App_embeds_both_label_files_under_the_name_it_registers_with_Loc()
    {
        var project = XDocument.Load(Path.Combine(RepoPaths.AppDirectory, "ReBackup.App.csproj"));
        project.Descendants("EmbeddedResource")
            .Select(item => ((string?)item.Attribute("Include"), (string?)item.Attribute("LogicalName")))
            .Should().Contain((@"Locales\*.json", "ReBackup.App.Locales.%(Filename)%(Extension)"));
        File.ReadAllText(Path.Combine(RepoPaths.AppDirectory, "App.xaml.cs"))
            .Should().Contain("new LabelSource(typeof(App).Assembly, \"ReBackup.App.Locales.\")");
    }

    [Fact]
    public void The_Shared_Wpf_library_has_one_file_per_supported_language()
    {
        Directory.GetFiles(Path.Combine(RepoPaths.SharedWpfDirectory, "Locales"), "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Should().BeEquivalentTo(AppLanguages.Supported.Select(language => "wpf." + language));
        foreach (var language in AppLanguages.Supported)
            LoadWpf(language).Entries.Should().NotBeEmpty();
    }

    [Fact]
    public void Both_wpf_files_have_the_same_keys()
    {
        var english = LoadWpf(AppLanguages.English).Entries.Keys.ToList();
        var german = LoadWpf(AppLanguages.German).Entries.Keys.ToList();

        english.Except(german).Should().BeEmpty("every English label needs a German one");
        german.Except(english).Should().BeEmpty("German must not have labels English lacks");
    }

    [Fact]
    public void Both_wpf_files_use_the_same_placeholders_per_key()
    {
        var english = LoadWpf(AppLanguages.English);
        var german = LoadWpf(AppLanguages.German);

        english.Entries
            .Where(pair => german.Entries.TryGetValue(pair.Key, out var text) &&
                           !LabelFormat.PlaceholderSpecs(pair.Value).Order(StringComparer.Ordinal)
                               .SequenceEqual(LabelFormat.PlaceholderSpecs(text).Order(StringComparer.Ordinal)))
            .Select(pair => pair.Key)
            .Should().BeEmpty("name and format of every placeholder must match");
    }

    [Fact]
    public void Both_wpf_files_have_the_same_plural_labels() =>
        LoadWpf(AppLanguages.German).Plurals.Should().BeEquivalentTo(LoadWpf(AppLanguages.English).Plurals);

    [Fact]
    public void No_wpf_label_is_blank()
    {
        foreach (var language in AppLanguages.Supported)
        {
            LoadWpf(language).Entries.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key)
                .Should().BeEmpty(language);
        }
    }

    [Theory]
    [InlineData(AppLanguages.English)]
    [InlineData(AppLanguages.German)]
    public void No_key_is_in_more_than_one_of_the_three_files(string language)
    {
        var keys = new[]
        {
            LoadShared(language).Entries.Keys, LoadWpf(language).Entries.Keys, Load(language).Entries.Keys,
        };

        keys.SelectMany(k => k).GroupBy(key => key).Where(group => group.Count() > 1).Select(group => group.Key)
            .Should().BeEmpty("a later source overrides an earlier one, so a key belongs to one file only");
    }

    /// <summary>Static keys: <c>Loc.T("…")</c>, <c>Loc.F("…")</c>, <c>Message.Of("…")</c>, <c>{l:Loc …}</c>, <c>{l:LocBind …}</c>.</summary>
    [Fact]
    public void Every_label_the_Shared_Wpf_sources_use_is_in_the_wpf_files()
    {
        var code = new Regex(@"(?:Loc\.(?:T|F)|Message\.Of)\(\s*""([^""]+)""", RegexOptions.CultureInvariant);
        var markup = new Regex(@"\{\w+:Loc(?:Bind)?\s+([A-Za-z][\w.]*)", RegexOptions.CultureInvariant);
        var used = RepoPaths.SourceFiles(RepoPaths.SharedWpfDirectory, "*.cs")
            .SelectMany(path => code.Matches(File.ReadAllText(path)).Select(match => (Path: path, Key: match.Groups[1].Value)))
            .Concat(RepoPaths.SourceFiles(RepoPaths.SharedWpfDirectory, "*.xaml")
                .SelectMany(path => markup.Matches(File.ReadAllText(path)).Select(match => (Path: path, Key: match.Groups[1].Value))))
            .ToList();

        used.Should().NotBeEmpty("the scan must find the keys the moved controls use");
        foreach (var language in AppLanguages.Supported)
        {
            var set = LoadWpf(language);
            used.Where(use => !set.Entries.ContainsKey(use.Key) && !set.Plurals.Contains(use.Key))
                .Select(use => Path.GetFileName(use.Path) + ": " + use.Key)
                .Should().BeEmpty(language);
        }
    }

    /// <summary>The culture in the file names must not turn them into satellite assemblies (WithCulture="false").</summary>
    [Fact]
    public void The_Shared_Wpf_library_embeds_its_label_files_under_the_name_the_App_registers()
    {
        var project = XDocument.Load(Path.Combine(RepoPaths.SharedWpfDirectory, "ReBackup.Shared.Wpf.csproj"));
        var item = project.Descendants("EmbeddedResource").Single();

        ((string?)item.Attribute("Include")).Should().Be(@"Locales\*.json");
        ((string?)item.Attribute("WithCulture")).Should().Be("false");
        ((string?)item.Attribute("LogicalName")).Should().Be("ReBackup.Shared.Wpf.Locales.%(Filename)%(Extension)");
        File.ReadAllText(Path.Combine(RepoPaths.AppDirectory, "App.xaml.cs"))
            .Should().Contain("\"ReBackup.Shared.Wpf.Locales.wpf.\"");
    }
}
