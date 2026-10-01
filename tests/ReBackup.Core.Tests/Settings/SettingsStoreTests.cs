using FluentAssertions;
using ReBackup.Core.Settings;
using ReBackup.Core.Tests.TestSupport;

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
        settings.Theme.Should().Be(ThemeMode.System);
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
    [InlineData(ThemeMode.System)]
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
    public void Missing_theme_loads_as_system()
    {
        var path = _tmp.WriteFile("settings.json", """{ "closeToTray": false }""");
        var store = new SettingsStore(path);

        var settings = store.Load();

        settings.Theme.Should().Be(ThemeMode.System);
        settings.CloseToTray.Should().BeFalse();
    }

    [Theory]
    [InlineData("\"Sepia\"")]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public void Unknown_theme_loads_as_system_and_keeps_the_other_settings(string json)
    {
        var path = _tmp.WriteFile("settings.json", $$"""{ "theme": {{json}}, "closeToTray": false }""");
        var store = new SettingsStore(path);

        var settings = store.Load();

        settings.Theme.Should().Be(ThemeMode.System);
        settings.CloseToTray.Should().BeFalse();
        store.LastLoadError.Should().BeNull();
    }

    [Fact]
    public void Theme_text_is_read_case_insensitively()
    {
        var path = _tmp.WriteFile("settings.json", """{ "theme": "light" }""");

        new SettingsStore(path).Load().Theme.Should().Be(ThemeMode.Light);
    }
}
