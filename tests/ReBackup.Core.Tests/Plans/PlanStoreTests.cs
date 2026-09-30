using FluentAssertions;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Plans;

public class PlanStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private PlanStore NewStore() => new(_tmp.PathOf("plans"));

    [Fact]
    public void Constructor_creates_the_directory()
    {
        using var store = NewStore();

        Directory.Exists(store.PlansDirectory).Should().BeTrue();
    }

    [Fact]
    public void Save_then_LoadAll_round_trips_all_fields()
    {
        using var store = NewStore();
        var plan = new BackupPlan
        {
            Name = "Projects", Source = @"D:\Projects", Target = @"F:\Backups",
            Enabled = false, FreeSpaceByRetention = true,
        };

        store.Save(plan);
        var loaded = store.LoadAll();

        loaded.Errors.Should().BeEmpty();
        loaded.Plans.Should().ContainSingle().Which.Should().BeEquivalentTo(plan);
    }

    [Fact]
    public void Save_writes_id_named_camelCase_file_without_temp_leftovers()
    {
        using var store = NewStore();
        var plan = new BackupPlan { Name = "Projects" };

        store.Save(plan);

        var path = Path.Combine(store.PlansDirectory, plan.Id + ".json");
        store.PathFor(plan.Id).Should().Be(path);
        File.ReadAllText(path).Should().Contain("\"name\": \"Projects\"");
        Directory.GetFiles(store.PlansDirectory).Should().ContainSingle();
    }

    [Fact]
    public void Unknown_properties_survive_load_and_save()
    {
        using var store = NewStore();
        var path = store.PathFor("p1");
        File.WriteAllText(path, """
            { "id": "p1", "name": "Keep", "source": "C:\\x", "target": "D:\\y",
              "triggers": [ { "type": "Daily", "time": "02:00" } ] }
            """);

        var plan = store.LoadAll().Plans.Single();
        store.Save(plan);

        File.ReadAllText(path).Should().Contain("\"triggers\"").And.Contain("02:00");
    }

    [Fact]
    public void Unreadable_file_is_reported_and_other_plans_still_load()
    {
        using var store = NewStore();
        store.Save(new BackupPlan { Name = "Good" });
        File.WriteAllText(Path.Combine(store.PlansDirectory, "bad.json"), "{ not json");

        var result = store.LoadAll();

        result.Plans.Should().ContainSingle().Which.Name.Should().Be("Good");
        result.Errors.Should().ContainSingle().Which.FilePath.Should().EndWith("bad.json");
    }

    [Fact]
    public void Id_not_matching_file_name_is_reported()
    {
        using var store = NewStore();
        File.WriteAllText(Path.Combine(store.PlansDirectory, "other.json"), """{ "id": "p1", "name": "X" }""");

        var result = store.LoadAll();

        result.Plans.Should().BeEmpty();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("does not match");
    }

    [Fact]
    public void Non_json_files_are_ignored()
    {
        using var store = NewStore();
        File.WriteAllText(Path.Combine(store.PlansDirectory, "p1.json.tmp"), "garbage");
        File.WriteAllText(Path.Combine(store.PlansDirectory, "readme.txt"), "garbage");

        var result = store.LoadAll();

        result.Plans.Should().BeEmpty();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void LoadAll_sorts_by_name_case_insensitively()
    {
        using var store = NewStore();
        store.Save(new BackupPlan { Name = "beta" });
        store.Save(new BackupPlan { Name = "Alpha" });
        store.Save(new BackupPlan { Name = "gamma" });

        store.LoadAll().Plans.Select(p => p.Name).Should().Equal("Alpha", "beta", "gamma");
    }

    [Fact]
    public void Delete_removes_the_file_and_ignores_missing_files()
    {
        using var store = NewStore();
        var plan = new BackupPlan { Name = "Gone" };
        store.Save(plan);

        store.Delete(plan.Id);
        store.Delete("does-not-exist");

        store.LoadAll().Plans.Should().BeEmpty();
    }
}
