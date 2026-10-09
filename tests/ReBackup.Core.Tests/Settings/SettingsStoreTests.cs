using FluentAssertions;
using ReBackup.Core.Settings;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Settings;

namespace ReBackup.Core.Tests.Settings;

public class SettingsStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Missing_file_yields_defaults()
    {
        var store = new SettingsStore(_tmp.PathOf("settings.json"));

        var settings = store.Load();

        settings.CloseToTray.Should().BeTrue();
        settings.StartWithWindows.Should().BeFalse();
        settings.Theme.Should().Be(ThemeMode.Dark);
        settings.DefaultIgnorePatterns.Should().Equal("Thumbs.db", "desktop.ini", "$RECYCLE.BIN/", "System Volume Information/");
        store.LastLoadError.Should().BeNull();
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var store = new SettingsStore(_tmp.PathOf("settings.json"));
        var settings = new AppSettings
        {
            CloseToTray = false,
            StartWithWindows = true,
            DefaultIgnorePatterns = ["*.bak"],
        };

        store.Save(settings);

        store.Load().Should().BeEquivalentTo(settings);
    }

    [Fact]
    public void Corrupt_file_yields_defaults_and_reports_error()
    {
        var path = _tmp.WriteFile("settings.json", "{ nope");
        var store = new SettingsStore(path);

        var settings = store.Load();

        settings.CloseToTray.Should().BeTrue();
        store.LastLoadError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Locked_file_yields_defaults_and_reports_error()
    {
        var path = _tmp.WriteFile("settings.json", "{}");
        var store = new SettingsStore(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var settings = store.Load();

        settings.CloseToTray.Should().BeTrue();
        store.LastLoadError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Null_ignore_patterns_fall_back_to_built_in_defaults()
    {
        var path = _tmp.WriteFile("settings.json", """{ "defaultIgnorePatterns": null }""");
        var store = new SettingsStore(path);

        store.Load().DefaultIgnorePatterns.Should().Equal(AppSettings.BuiltInIgnoreDefaults);
    }

    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    public void Theme_round_trips_as_text(ThemeMode mode)
    {
        var store = new SettingsStore(_tmp.PathOf("settings.json"));

        store.Save(new AppSettings { Theme = mode });

        File.ReadAllText(store.SettingsFile).Should().Contain($"\"theme\": \"{mode}\"");
        store.Load().Theme.Should().Be(mode);
    }

    [Fact]
    public void Missing_theme_loads_as_dark()
    {
        var path = _tmp.WriteFile("settings.json", """{ "closeToTray": false }""");
        var store = new SettingsStore(path);

        var settings = store.Load();

        settings.Theme.Should().Be(ThemeMode.Dark);
        settings.CloseToTray.Should().BeFalse();
    }

    [Theory]
    [InlineData("\"Sepia\"")]
    [InlineData("\"System\"")]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public void Unknown_theme_loads_as_dark_and_keeps_the_other_settings(string json)
    {
        var path = _tmp.WriteFile("settings.json", $$"""{ "theme": {{json}}, "closeToTray": false }""");
        var store = new SettingsStore(path);

        var settings = store.Load();

        settings.Theme.Should().Be(ThemeMode.Dark);
        settings.CloseToTray.Should().BeFalse();
        store.LastLoadError.Should().BeNull();
    }

    [Fact]
    public void Theme_text_is_read_case_insensitively()
    {
        var path = _tmp.WriteFile("settings.json", """{ "theme": "light" }""");

        new SettingsStore(path).Load().Theme.Should().Be(ThemeMode.Light);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void Language_round_trips_as_text(string language)
    {
        var store = new SettingsStore(_tmp.PathOf("settings.json"));

        store.Save(new AppSettings { Language = language });

        File.ReadAllText(store.SettingsFile).Should().Contain($"\"language\": \"{language}\"");
        store.Load().Language.Should().Be(language);
    }

    [Fact]
    public void No_language_is_not_written_and_loads_as_null()
    {
        var store = new SettingsStore(_tmp.PathOf("settings.json"));

        store.Save(new AppSettings());

        File.ReadAllText(store.SettingsFile).Should().NotContain("language");
        store.Load().Language.Should().BeNull();
    }

    [Theory]
    [InlineData("\"fr-FR\"")]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{ \"a\": 1 }")]
    [InlineData("[\"de-DE\"]")]
    public void Unknown_language_loads_as_null_and_keeps_the_other_settings(string json)
    {
        var path = _tmp.WriteFile("settings.json", $$"""{ "closeToTray": false, "language": {{json}}, "theme": "Light" }""");
        var store = new SettingsStore(path);

        var settings = store.Load();

        settings.Language.Should().BeNull();
        settings.CloseToTray.Should().BeFalse();
        settings.Theme.Should().Be(ThemeMode.Light);
        store.LastLoadError.Should().BeNull();
    }

    [Fact]
    public void Language_is_read_case_insensitively()
    {
        var path = _tmp.WriteFile("settings.json", """{ "language": "de-de" }""");

        new SettingsStore(path).Load().Language.Should().Be("de-DE");
    }

    [Fact]
    public void Plan_order_round_trips_as_planOrder()
    {
        var store = new SettingsStore(_tmp.PathOf("settings.json"));

        store.Save(new AppSettings { PlanOrder = ["b", "a", "c"] });

        File.ReadAllText(store.SettingsFile).Should().Contain("\"planOrder\"");
        store.Load().PlanOrder.Should().Equal("b", "a", "c");
    }

    [Fact]
    public void Null_plan_order_loads_as_empty()
    {
        var path = _tmp.WriteFile("settings.json", """{ "planOrder": null }""");

        new SettingsStore(path).Load().PlanOrder.Should().BeEmpty();
    }

    [Fact]
    public void A_successful_save_clears_the_load_error()
    {
        var path = _tmp.WriteFile("settings.json", "{ nope");
        var store = new SettingsStore(path);
        store.Load();
        store.LastLoadError.Should().NotBeNull();

        store.Save(new AppSettings());

        store.LastLoadError.Should().BeNull();
    }
}
