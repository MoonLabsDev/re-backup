using System.Text;
using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Pool;

public class StoreGuardTests
{
    private static BackupPlan Plan(bool incremental) =>
        new() { Id = "plan1", Name = "Projects", Incremental = incremental };

    private static void AddStore(InMemoryStorage target, string json) =>
        target.AddFile(PoolLayout.StoreFile, Encoding.UTF8.GetBytes(json));

    private static string StoreJson(string planId, string format = "pool", string hash = "xxh128", int version = 1) =>
        $$"""{"format":"{{format}}","hash":"{{hash}}","version":{{version}},"planId":"{{planId}}"}""";

    private static void AddFullVersion(InMemoryStorage target) =>
        target.AddFile("2026_01_01-10_00 Projects/re-manifest.json", Encoding.UTF8.GetBytes(
            """{"formatVersion":2,"planId":"plan1","planName":"Projects","createdUtc":"2026-01-01T10:00:00Z","files":[]}"""));

    private static string[] Snapshot(InMemoryStorage target) => [.. target.Files.Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Empty_target_gets_store()
    {
        var target = new InMemoryStorage();

        var outcome = await StoreGuard.CheckAsync(target, Plan(incremental: true), createIfMissing: true);

        outcome.Should().Be(StoreGuardOutcome.Created);
        target.Files.Should().BeEquivalentTo([PoolLayout.StoreFile]);
        using var json = JsonDocument.Parse(target.ReadAllBytes(PoolLayout.StoreFile));
        json.RootElement.GetProperty("format").GetString().Should().Be("pool");
        json.RootElement.GetProperty("hash").GetString().Should().Be("xxh128");
        json.RootElement.GetProperty("version").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("planId").GetString().Should().Be("plan1");
        (await StoreGuard.ReadAsync(target, default)).Should().Be(new StoreDescriptor("pool", "xxh128", 1, "plan1"));
        (await StoreGuard.CheckAsync(target, Plan(true), createIfMissing: true)).Should().Be(StoreGuardOutcome.Ok);
    }

    [Fact]
    public async Task Same_plan_store_is_ok()
    {
        var target = new InMemoryStorage();
        AddStore(target, StoreJson("plan1"));
        var before = Snapshot(target);

        var outcome = await StoreGuard.CheckAsync(target, Plan(true), createIfMissing: true);

        outcome.Should().Be(StoreGuardOutcome.Ok);
        Snapshot(target).Should().Equal(before);
    }

    [Fact]
    public async Task Store_of_another_plan_aborts()
    {
        var target = new InMemoryStorage();
        AddStore(target, StoreJson("other"));
        var before = Snapshot(target);

        var outcome = await StoreGuard.CheckAsync(target, Plan(true), createIfMissing: true);

        outcome.Should().Be(StoreGuardOutcome.OtherPlan);
        Snapshot(target).Should().Equal(before);
    }

    [Fact]
    public async Task Full_plan_on_pool_target_aborts()
    {
        var target = new InMemoryStorage();
        AddStore(target, StoreJson("plan1"));
        var before = Snapshot(target);

        var outcome = await StoreGuard.CheckAsync(target, Plan(incremental: false), createIfMissing: true);

        outcome.Should().Be(StoreGuardOutcome.FullPlanOnPoolTarget);
        Snapshot(target).Should().Equal(before);
    }

    [Fact]
    public async Task Full_plan_on_target_without_store_is_ok_and_writes_nothing()
    {
        var target = new InMemoryStorage();
        AddFullVersion(target);
        var before = Snapshot(target);

        var outcome = await StoreGuard.CheckAsync(target, Plan(incremental: false), createIfMissing: true);

        outcome.Should().Be(StoreGuardOutcome.Ok);
        Snapshot(target).Should().Equal(before);
    }

    [Fact]
    public async Task Pool_plan_on_target_with_full_versions_aborts()
    {
        var target = new InMemoryStorage();
        AddFullVersion(target);
        var before = Snapshot(target);

        var outcome = await StoreGuard.CheckAsync(target, Plan(true), createIfMissing: true);

        outcome.Should().Be(StoreGuardOutcome.PoolPlanOnFullTarget);
        Snapshot(target).Should().Equal(before);
    }

    [Theory]
    [InlineData("pool", "xxh128", 2)]
    [InlineData("pool", "xxh64", 1)]
    [InlineData("tar", "xxh128", 1)]
    public async Task Unknown_version_is_unsupported(string format, string hash, int version)
    {
        var target = new InMemoryStorage();
        AddStore(target, StoreJson("plan1", format, hash, version));
        var before = Snapshot(target);

        var outcome = await StoreGuard.CheckAsync(target, Plan(true), createIfMissing: true);

        outcome.Should().Be(StoreGuardOutcome.UnsupportedFormat);
        Snapshot(target).Should().Equal(before);
    }

    [Fact]
    public async Task Unreadable_store_is_unsupported()
    {
        var target = new InMemoryStorage();
        AddStore(target, "{ not json");
        var before = Snapshot(target);

        var outcome = await StoreGuard.CheckAsync(target, Plan(true), createIfMissing: true);

        outcome.Should().Be(StoreGuardOutcome.UnsupportedFormat);
        Snapshot(target).Should().Equal(before);
    }

    [Fact]
    public async Task Pool_folder_without_store_is_unsupported()
    {
        var target = new InMemoryStorage();
        target.AddFile("re-pool/ab/cd/abcdef0123456789abcdef0123456789", [1]);
        var before = Snapshot(target);

        var outcome = await StoreGuard.CheckAsync(target, Plan(true), createIfMissing: true);

        outcome.Should().Be(StoreGuardOutcome.UnsupportedFormat);
        Snapshot(target).Should().Equal(before);
    }

    [Theory]
    [InlineData("2026_01_01-10_00 Other/re-manifest.json")]
    [InlineData("2026_01_01-10_00 Other.partial/a.txt")]
    [InlineData("2026_01_01-10_00 Other.deleting/a.txt")]
    [InlineData("renamed by hand/re-manifest.json")]
    public async Task Pool_plan_on_target_with_another_plans_backups_aborts(string file)
    {
        // Another plan's full versions: a store file here would stop that plan's every run.
        var target = new InMemoryStorage();
        target.AddFile(file, Encoding.UTF8.GetBytes(
            """{"formatVersion":2,"planId":"other","planName":"Other","createdUtc":"2026-01-01T10:00:00Z","files":[]}"""));
        var before = Snapshot(target);

        (await StoreGuard.CheckAsync(target, Plan(true), createIfMissing: false))
            .Should().Be(StoreGuardOutcome.TargetNotEmpty);
        var outcome = await StoreGuard.CheckAsync(target, Plan(true), createIfMissing: true);

        outcome.Should().Be(StoreGuardOutcome.TargetNotEmpty);
        Snapshot(target).Should().Equal(before);
    }

    [Fact]
    public async Task Pool_plan_on_target_with_unrelated_files_gets_store()
    {
        var target = new InMemoryStorage();
        target.AddFile("notes/readme.txt", [1]);
        target.AddFile("photo.jpg", [2]);

        var outcome = await StoreGuard.CheckAsync(target, Plan(true), createIfMissing: true);

        outcome.Should().Be(StoreGuardOutcome.Created);
        target.Files.Should().Contain(PoolLayout.StoreFile);
    }

    [Fact]
    public async Task CheckAsync_without_create_writes_nothing()
    {
        var target = new InMemoryStorage();

        var outcome = await StoreGuard.CheckAsync(target, Plan(true), createIfMissing: false);

        outcome.Should().Be(StoreGuardOutcome.Created);
        target.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_is_null_without_store()
    {
        (await StoreGuard.ReadAsync(new InMemoryStorage(), default)).Should().BeNull();
    }

    [Theory]
    [InlineData(StoreGuardOutcome.FullPlanOnPoolTarget, "core.run.storeFullOnPool")]
    [InlineData(StoreGuardOutcome.PoolPlanOnFullTarget, "core.run.storePoolOnFull")]
    [InlineData(StoreGuardOutcome.OtherPlan, "core.run.storeOtherPlan")]
    [InlineData(StoreGuardOutcome.UnsupportedFormat, "core.run.storeUnsupported")]
    [InlineData(StoreGuardOutcome.TargetNotEmpty, "core.run.storeNotEmpty")]
    public void MessageKey_names_the_locale_key(StoreGuardOutcome outcome, string key)
    {
        StoreGuard.MessageKey(outcome).Should().Be(key);
    }
}
