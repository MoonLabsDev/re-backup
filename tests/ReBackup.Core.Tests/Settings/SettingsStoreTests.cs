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
}
