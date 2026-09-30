using FluentAssertions;
using ReBackup.Core.Config;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Config;

public class ConfigLocationTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private string AppData => _tmp.CreateDir("appdata");

    [Fact]
    public void ConfigPaths_builds_expected_layout()
    {
        var paths = new ConfigPaths(@"C:\cfg");

        paths.PlansDirectory.Should().Be(@"C:\cfg\plans");
        paths.LogsDirectory.Should().Be(@"C:\cfg\logs");
        paths.SettingsFile.Should().Be(@"C:\cfg\settings.json");
        paths.LogFileFor("abc").Should().Be(@"C:\cfg\logs\abc.jsonl");
    }

    [Fact]
    public void Resolve_without_pointer_uses_app_data_root()
    {
        ConfigLocation.Resolve(AppData).Root.Should().Be(AppData);
    }

    [Fact]
    public void Resolve_with_locked_pointer_falls_back_to_app_data_root()
    {
        var appData = AppData;
        var pointer = Path.Combine(appData, "location.json");
        File.WriteAllText(pointer, """{ "configFolder": "E:\cfg" }""");
        using var held = new FileStream(pointer, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        ConfigLocation.Resolve(appData).Root.Should().Be(appData);
    }

    [Fact]
    public void Resolve_follows_pointer_file()
    {
        var appData = AppData;
        File.WriteAllText(Path.Combine(appData, "location.json"), """{ "configFolder": "E:\\cfg" }""");

        ConfigLocation.Resolve(appData).Root.Should().Be(@"E:\cfg");
    }

    [Fact]
    public void Resolve_ignores_corrupt_pointer_file()
    {
        var appData = AppData;
        File.WriteAllText(Path.Combine(appData, "location.json"), "garbage");

        ConfigLocation.Resolve(appData).Root.Should().Be(appData);
    }

    [Fact]
    public void ContainsConfiguration_detects_settings_or_plans()
    {
        var empty = _tmp.CreateDir("empty");
        var withSettings = _tmp.CreateDir("s");
        File.WriteAllText(Path.Combine(withSettings, "settings.json"), "{}");
        var withPlans = _tmp.CreateDir("p");
        _tmp.WriteFile(@"p\plans\x.json", "{}");

        ConfigLocation.ContainsConfiguration(empty).Should().BeFalse();
        ConfigLocation.ContainsConfiguration(withSettings).Should().BeTrue();
        ConfigLocation.ContainsConfiguration(withPlans).Should().BeTrue();
    }

    [Fact]
    public void Move_with_CopyCurrent_copies_files_and_writes_pointer()
    {
        var appData = AppData;
        var current = new ConfigPaths(appData);
        _tmp.WriteFile(@"appdata\settings.json", "{}");
        _tmp.WriteFile(@"appdata\plans\p1.json", "plan");
        _tmp.WriteFile(@"appdata\logs\p1.jsonl", "log");
        var newRoot = _tmp.PathOf("newcfg");

        var moved = ConfigLocation.Move(appData, current, newRoot, ConfigMoveMode.CopyCurrent);

        moved.Root.Should().Be(newRoot);
        File.ReadAllText(moved.SettingsFile).Should().Be("{}");
        File.ReadAllText(Path.Combine(moved.PlansDirectory, "p1.json")).Should().Be("plan");
        File.ReadAllText(moved.LogFileFor("p1")).Should().Be("log");
        File.Exists(Path.Combine(appData, "plans", "p1.json")).Should().BeTrue("old files are kept");
        ConfigLocation.Resolve(appData).Root.Should().Be(newRoot);
    }

    [Fact]
    public void Move_with_CopyCurrent_refuses_folder_that_already_has_configuration()
    {
        var appData = AppData;
        _tmp.WriteFile(@"existing\settings.json", "{}");

        var act = () => ConfigLocation.Move(appData, new ConfigPaths(appData), _tmp.PathOf("existing"), ConfigMoveMode.CopyCurrent);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Move_with_UseExisting_only_repoints()
    {
        var appData = AppData;
        _tmp.WriteFile(@"appdata\plans\mine.json", "mine");
        _tmp.WriteFile(@"existing\settings.json", "theirs");

        var moved = ConfigLocation.Move(appData, new ConfigPaths(appData), _tmp.PathOf("existing"), ConfigMoveMode.UseExisting);

        File.ReadAllText(moved.SettingsFile).Should().Be("theirs");
        File.Exists(Path.Combine(moved.PlansDirectory, "mine.json")).Should().BeFalse();
        ConfigLocation.Resolve(appData).Root.Should().Be(moved.Root);
    }

    [Fact]
    public void Moving_back_to_app_data_root_removes_pointer()
    {
        var appData = AppData;
        _tmp.WriteFile(@"appdata\settings.json", "{}");
        var elsewhere = ConfigLocation.Move(appData, new ConfigPaths(appData), _tmp.PathOf("other"), ConfigMoveMode.CopyCurrent);

        var back = ConfigLocation.Move(appData, elsewhere, appData, ConfigMoveMode.UseExisting);

        back.Root.Should().Be(appData);
        File.Exists(Path.Combine(appData, "location.json")).Should().BeFalse();
    }

    [Fact]
    public void Move_to_the_same_folder_is_a_no_op()
    {
        var appData = AppData;
        var current = new ConfigPaths(appData);

        ConfigLocation.Move(appData, current, appData + @"\", ConfigMoveMode.CopyCurrent).Should().Be(current);
    }
}
