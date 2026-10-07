using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Json;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

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
            Name = "Projects", Source = StorageLocation.FileSystem(@"D:\Projects"), Target = StorageLocation.FileSystem(@"F:\Backups"),
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
              "triggers": [ { "type": "Daily", "time": "02:00" } ],
              "retention": [ { "period": "Monthly", "anchor": 0, "keep": 12 } ] }
            """);

        var plan = store.LoadAll().Plans.Single();
        store.Save(plan);

        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        var trigger = saved.RootElement.GetProperty("triggers")[0];
        trigger.GetProperty("type").GetString().Should().Be("Daily");
        trigger.GetProperty("time").GetString().Should().Be("02:00");
        var rule = saved.RootElement.GetProperty("retention")[0];
        rule.GetProperty("period").GetString().Should().Be("Monthly");
        rule.GetProperty("anchor").GetInt32().Should().Be(0);
        rule.GetProperty("keep").GetInt32().Should().Be(12);
    }

    [Fact]
    public void Legacy_plan_round_trip_keeps_extra_sections()
    {
        using var store = NewStore();
        var path = store.PathFor("p1");
        File.WriteAllText(path, """
            { "id": "p1", "name": "Keep", "source": "C:\\x", "target": "D:\\y",
              "futureSection": { "a": 1, "b": [ "x" ] } }
            """);

        var plan = store.LoadAll().Plans.Single();
        plan.Source.Should().Be(StorageLocation.FileSystem(@"C:\x"));
        store.Save(plan);

        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        saved.RootElement.GetProperty("futureSection").GetProperty("a").GetInt32().Should().Be(1);
        saved.RootElement.GetProperty("futureSection").GetProperty("b")[0].GetString().Should().Be("x");
        var source = saved.RootElement.GetProperty("source");
        source.GetProperty("kind").GetString().Should().Be("fs");
        source.GetProperty("path").GetString().Should().Be(@"C:\x");
        saved.RootElement.GetProperty("target").GetProperty("path").GetString().Should().Be(@"D:\y");
    }

    [Fact]
    public void Ignore_section_round_trips_with_spec_property_names()
    {
        using var store = NewStore();
        var plan = new BackupPlan { Name = "Projects" };
        plan.Ignore.UseGlobalDefaults = false;
        plan.Ignore.HonorNestedFiles = false;
        plan.Ignore.Patterns.AddRange(["node_modules/", "*.tmp", "!keep.tmp"]);

        store.Save(plan);

        using var saved = JsonDocument.Parse(File.ReadAllText(store.PathFor(plan.Id)));
        var ignore = saved.RootElement.GetProperty("ignore");
        ignore.GetProperty("useGlobalDefaults").GetBoolean().Should().BeFalse();
        ignore.GetProperty("honorNestedFiles").GetBoolean().Should().BeFalse();
        ignore.GetProperty("patterns").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("node_modules/", "*.tmp", "!keep.tmp");
        store.LoadAll().Plans.Single().Ignore.Should().BeEquivalentTo(plan.Ignore);
    }

    [Fact]
    public void Missing_ignore_section_yields_defaults()
    {
        using var store = NewStore();
        File.WriteAllText(store.PathFor("p1"), """{ "id": "p1", "name": "Old" }""");

        var ignore = store.LoadAll().Plans.Single().Ignore;

        ignore.UseGlobalDefaults.Should().BeTrue();
        ignore.HonorNestedFiles.Should().BeTrue();
        ignore.Patterns.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{ "id": "p1", "name": "X", "ignore": null }""")]
    [InlineData("""{ "id": "p1", "name": "X", "ignore": { "patterns": null } }""")]
    public void Null_ignore_values_are_replaced_by_defaults(string json)
    {
        using var store = NewStore();
        File.WriteAllText(store.PathFor("p1"), json);

        var ignore = store.LoadAll().Plans.Single().Ignore;

        ignore.Should().NotBeNull();
        ignore.Patterns.Should().BeEmpty();
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

    [Theory]
    [InlineData("triggers")]
    [InlineData("retention")]
    public void A_list_with_an_empty_entry_is_reported(string list)
    {
        using var store = NewStore();
        store.Save(new BackupPlan { Name = "Good" });
        File.WriteAllText(store.PathFor("p1"), $$"""{ "id": "p1", "name": "Broken", "{{list}}": [null] }""");

        var result = store.LoadAll();

        result.Plans.Should().ContainSingle().Which.Name.Should().Be("Good");
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("empty entry");
        var act = () => store.TryLoad("p1");
        act.Should().Throw<JsonException>();
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

    [Fact]
    public void TryLoad_returns_the_plan_as_saved()
    {
        using var store = NewStore();
        var plan = new BackupPlan { Name = "Projects", Source = StorageLocation.FileSystem(@"D:\Projects"), Target = StorageLocation.FileSystem(@"F:\Backups") };
        plan.Retention.Add(new RetentionRule { Period = RetentionPeriod.Daily, Keep = 7 });
        store.Save(plan);
        store.Save(new BackupPlan { Name = "Another" });

        var loaded = store.TryLoad(plan.Id);

        loaded.Should().BeEquivalentTo(plan);

        plan.Retention[0].Keep = 30;
        store.Save(plan);
        store.TryLoad(plan.Id)!.Retention.Should().ContainSingle().Which.Keep.Should().Be(30);
    }

    [Fact]
    public void TryLoad_returns_null_for_an_unknown_id_and_after_a_delete()
    {
        using var store = NewStore();
        var plan = new BackupPlan { Name = "Gone" };
        store.Save(plan);
        store.Delete(plan.Id);

        store.TryLoad("does-not-exist").Should().BeNull();
        store.TryLoad(plan.Id).Should().BeNull();
    }

    [Fact]
    public void TryLoad_returns_null_when_the_plans_folder_is_gone()
    {
        using var store = NewStore();
        Directory.Delete(store.PlansDirectory);

        store.TryLoad("p1").Should().BeNull();
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{ "id": "other", "name": "X" }""")]
    public void TryLoad_throws_for_a_damaged_file(string content)
    {
        using var store = NewStore();
        File.WriteAllText(store.PathFor("p1"), content);

        var act = () => store.TryLoad("p1");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void TryLoad_fills_in_a_missing_ignore_section_like_LoadAll()
    {
        using var store = NewStore();
        File.WriteAllText(store.PathFor("p1"), """{ "id": "p1", "name": "X", "ignore": { "patterns": null } }""");

        store.TryLoad("p1")!.Ignore.Patterns.Should().BeEmpty();
    }

    [Fact]
    public void Retention_rules_round_trip()
    {
        using var store = NewStore();
        var plan = new BackupPlan { Name = "Projects" };
        plan.Retention.Add(new RetentionRule { Period = RetentionPeriod.Monthly, Anchor = "0", Keep = 12 });
        plan.Retention.Add(new RetentionRule { Period = RetentionPeriod.Weekly, Anchor = "Sunday", Keep = 4 });
        store.Save(plan);

        var loaded = store.LoadAll().Plans.Single();

        loaded.Retention.Should().HaveCount(2);
        loaded.Retention[0].Period.Should().Be(RetentionPeriod.Monthly);
        loaded.Retention[0].Anchor.Should().Be("0");
        loaded.Retention[0].Keep.Should().Be(12);
        loaded.Retention[1].Anchor.Should().Be("Sunday");
        loaded.Clone().Retention.Should().HaveCount(2);
    }

    [Fact]
    public void A_null_retention_section_loads_as_no_rules()
    {
        var plan = JsonSerializer.Deserialize<BackupPlan>("""{ "id": "p1", "name": "Keep", "retention": null }""",
            JsonDefaults.Options)!;

        plan.Retention.Should().BeEmpty();
    }

    [Fact]
    public void Triggers_round_trip()
    {
        using var store = NewStore();
        var plan = new BackupPlan { Name = "Projects" };
        plan.Triggers.Add(new ScheduleTrigger { Type = TriggerType.Weekly, Days = ["Mon", "Wed"], Time = "18:00" });
        store.Save(plan);

        var loaded = store.LoadAll().Plans.Single();

        loaded.Triggers.Should().ContainSingle();
        loaded.Triggers[0].Days.Should().Equal("Mon", "Wed");
        loaded.Triggers[0].Time.Should().Be("18:00");
        loaded.Clone().Triggers.Should().ContainSingle();
    }

    [Fact]
    public void A_null_triggers_section_loads_as_no_triggers()
    {
        var plan = JsonSerializer.Deserialize<BackupPlan>("""{ "id": "p1", "name": "Keep", "triggers": null }""",
            JsonDefaults.Options)!;

        plan.Triggers.Should().BeEmpty();
    }
}
