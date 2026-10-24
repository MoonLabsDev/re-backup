using System.IO;
using System.Text.Json;
using FluentAssertions;
using ReBackup.App.Control;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Control;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Storage;

namespace ReBackup.App.Tests;

/// <summary>
/// The control service's growth comparisons: where the growth between two versions, or between the newest version and
/// what the next run would back up, comes from, by folder and by extension.
/// </summary>
public class ControlCompareTests : IDisposable
{
    private const string Older = "2026_09_29-14_05 Projects";
    private const string Newer = "2026_09_30-14_05 Projects";
    private static readonly DateTime Time = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly MainViewModelFixture _fixture = new();
    private readonly SerialContext _ui = new();
    private readonly string _source;
    private readonly string _target;

    public ControlCompareTests()
    {
        _source = Path.Combine(_fixture.Root, "source");
        _target = Path.Combine(_fixture.Root, "target");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_target);
        _fixture.Settings.DefaultIgnorePatterns = [];
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(_source),
            Target = StorageLocation.FileSystem(_target),
            Ignore = new IgnoreSettings { Patterns = ["logs/"] },
        });
    }

    public void Dispose()
    {
        _ui.Dispose();
        _fixture.Dispose();
    }

    // ------------------------------------------------------------------ compare_versions

    [Fact]
    public async Task Compare_versions_drills_down_by_path()
    {
        WriteFile(_target, Older + "/a.txt", "alpha");
        WriteFile(_target, Older + "/gone.txt", "xx");
        WriteFile(_target, Older + "/sub/keep.md", "keep");
        WriteFile(_target, Newer + "/a.txt", "alpha-longer");
        WriteFile(_target, Newer + "/new.txt", "abc");
        WriteFile(_target, Newer + "/sub/keep.md", "keep");
        WriteFile(_target, Newer + "/sub/deep/big.bin", new string('b', 40));
        WriteFile(_target, Newer + "/sub/Small.BIN", "s");
        var service = await CreateWithIndexedTargetAsync();

        var root = await OkAsync(service, ControlOps.CompareVersions, new { plan = "Projects", a = Older, b = Newer });

        root.GetProperty("from").GetString().Should().Be(Older);
        root.GetProperty("to").GetString().Should().Be(Newer);
        root.GetProperty("path").GetString().Should().Be("");
        Rows(root.GetProperty("byFolder")).Should().Equal(
            ("sub/", 2, 0, 0, 41L, 0L, 41L),
            ("a.txt", 0, 1, 0, 0L, 0L, 7L),
            ("new.txt", 1, 0, 0, 3L, 0L, 3L),
            ("gone.txt", 0, 0, 1, 0L, 2L, -2L));
        Rows(root.GetProperty("byExtension")).Should().Equal(
            (".bin", 2, 0, 0, 41L, 0L, 41L),
            (".txt", 1, 1, 1, 3L, 2L, 8L));
        var total = root.GetProperty("total");
        Row(total).Should().Be(("", 3, 1, 1, 44L, 2L, 49L));
        total.GetProperty("net").GetProperty("text").GetString().Should().Be("49 B");
        root.GetProperty("byFolder")[3].GetProperty("net").GetProperty("text").GetString().Should().Be("-2 B");
        root.GetProperty("truncated").GetBoolean().Should().BeFalse();

        var sub = await OkAsync(service, ControlOps.CompareVersions, new { plan = "Projects", a = Older, b = Newer, path = @"\sub\" });

        sub.GetProperty("path").GetString().Should().Be("sub");
        Rows(sub.GetProperty("byFolder")).Should().Equal(
            ("deep/", 1, 0, 0, 40L, 0L, 40L),
            ("Small.BIN", 1, 0, 0, 1L, 0L, 1L));
        Rows(sub.GetProperty("byExtension")).Should().Equal((".bin", 2, 0, 0, 41L, 0L, 41L));
        Row(sub.GetProperty("total")).Should().Be(("", 2, 0, 0, 41L, 0L, 41L));

        var top1 = await OkAsync(service, ControlOps.CompareVersions, new { plan = "Projects", a = Older, b = Newer, top = 1 });
        Rows(top1.GetProperty("byFolder")).Select(r => r.Key).Should().Equal("sub/");
        top1.GetProperty("folderRows").GetInt32().Should().Be(4);
        top1.GetProperty("truncated").GetBoolean().Should().BeTrue();
        top1.GetProperty("hint").GetString().Should().Contain("'top'").And.Contain("'path'");
        Row(top1.GetProperty("total")).Should().Be(("", 3, 1, 1, 44L, 2L, 49L), "the totals cover every row");

        (await FailAsync(service, ControlOps.CompareVersions, new { plan = "Projects", a = Older, b = Newer, path = "nowhere" }))
            .Key.Should().Be(ControlErrors.BadRequest);
        (await FailAsync(service, ControlOps.CompareVersions, new { plan = "Projects", a = Older, b = "2020_01_01-00_00 Projects" }))
            .Key.Should().Be(ControlErrors.VersionNotFound);
    }

    // ------------------------------------------------------------------ compare_source_with_version

    [Fact]
    public async Task Source_comparison_defaults_to_the_newest_version()
    {
        WriteFile(_target, Older + "/a.txt", "alpha");
        WriteFile(_target, Newer + "/a.txt", "alpha");
        WriteFile(_target, Newer + "/docs/readme.md", "readme");
        WriteFile(_target, Newer + "/gone.txt", "xx");
        WriteFile(_target, Newer + "/logs/x.log", "log");
        WriteFile(_source, "a.txt", "alpha");                     // unchanged (size and time as in the version)
        WriteFile(_source, "docs/readme.md", "readme-longer");    // changed: 6 → 13
        WriteFile(_source, "docs/added.md", "added");             // added
        WriteFile(_source, "cache/c.bin", new string('c', 10));   // added
        WriteFile(_source, "logs/x.log", "log, now ignored");     // ignored: the next run drops it
        var service = await CreateWithIndexedTargetAsync();

        var result = await OkAsync(service, ControlOps.CompareSourceWithVersion, new { plan = "Projects" });

        result.GetProperty("from").GetString().Should().Be(Newer, "the newest version is the default");
        result.GetProperty("to").GetString().Should().Be("source");
        Rows(result.GetProperty("byFolder")).Should().Equal(
            ("docs/", 1, 1, 0, 5L, 0L, 12L),
            ("cache/", 1, 0, 0, 10L, 0L, 10L),
            ("gone.txt", 0, 0, 1, 0L, 2L, -2L),
            ("logs/", 0, 0, 1, 0L, 3L, -3L));
        Rows(result.GetProperty("byExtension")).Should().Equal(
            (".md", 1, 1, 0, 5L, 0L, 12L),
            (".bin", 1, 0, 0, 10L, 0L, 10L),
            (".txt", 0, 0, 1, 0L, 2L, -2L),
            (".log", 0, 0, 1, 0L, 3L, -3L));

        var older = await OkAsync(service, ControlOps.CompareSourceWithVersion, new { plan = "Projects", version = Older, path = "docs" });
        older.GetProperty("from").GetString().Should().Be(Older);
        Rows(older.GetProperty("byFolder")).Should().Equal(
            ("readme.md", 1, 0, 0, 13L, 0L, 13L),
            ("added.md", 1, 0, 0, 5L, 0L, 5L));

        (await FailAsync(service, ControlOps.CompareSourceWithVersion, new { plan = "Projects", path = "nowhere" }))
            .Key.Should().Be(ControlErrors.BadRequest);
    }

    [Fact]
    public async Task Source_comparison_matches_a_folder_renamed_in_case_only()
    {
        WriteFile(_target, Newer + "/Docs/a.txt", "alpha");
        WriteFile(_source, "docs/a.txt", "alpha");   // the same file: unchanged despite the spelling
        WriteFile(_source, "docs/b.txt", "bb");
        var service = await CreateWithIndexedTargetAsync();

        var result = await OkAsync(service, ControlOps.CompareSourceWithVersion, new { plan = "Projects" });

        var rows = Rows(result.GetProperty("byFolder")).ToList();
        rows.Should().ContainSingle();
        rows[0].Key.Should().BeEquivalentTo("docs/");
        rows[0].Should().Be((rows[0].Key, 1, 0, 0, 2L, 0L, 2L));
        Row(result.GetProperty("total")).Should().Be(("", 1, 0, 0, 2L, 0L, 2L));
    }

    [Fact]
    public async Task Source_comparison_picks_the_newest_of_versions_created_out_of_order()
    {
        const string newest = "2026_10_02-09_00 Projects";
        WriteFile(_target, newest + "/a.txt", "alpha");
        WriteFile(_target, Older + "/a.txt", "a");
        WriteFile(_target, Newer + "/a.txt", "al");
        WriteFile(_source, "a.txt", "alpha");
        var service = await CreateWithIndexedTargetAsync();

        var result = await OkAsync(service, ControlOps.CompareSourceWithVersion, new { plan = "Projects" });

        result.GetProperty("from").GetString().Should().Be(newest);
        result.GetProperty("byFolder").GetArrayLength().Should().Be(0, "the source equals the newest version");
    }

    // ------------------------------------------------------------------ the character budget

    [Fact]
    public async Task Huge_comparison_stays_within_the_character_budget()
    {
        const int files = 100_000;
        var vm = _fixture.Create();
        var index = vm.VersionIndexes.For("p1");
        var target = new StorageFactory().Open(StorageLocation.FileSystem(_target));
        BackupManifest Manifest(int size, int hashBase) => new()
        {
            Files = Enumerable.Range(0, files).Select(i => new ManifestFile(
                $"generated-folder-{i % 1000:000}/nested/file-{i:000000}.ext{i % 300:000}", size, Time, $"xxh64:{hashBase + i:x16}")).ToList(),
        };
        index.Add(new VersionInfo(Older, Older, new DateTime(2026, 9, 29, 14, 5, 0), VersionOwnership.Owned, null, null), Manifest(100, 0), target);
        index.Add(new VersionInfo(Newer, Newer, new DateTime(2026, 9, 30, 14, 5, 0), VersionOwnership.Owned, null, null), Manifest(150, files), target);
        var service = new ControlService(vm, _ui);

        var response = await service.HandleAsync(
            Request(ControlOps.CompareVersions, new { plan = "Projects", a = Older, b = Newer, top = 200 }), CancellationToken.None);

        response.Ok.Should().BeTrue(response.Error?.Message);
        JsonSerializer.Serialize(response, ControlJson.Options).Length.Should().BeLessThanOrEqualTo(ControlService.MaxChars);
        var result = response.Result!.Value;
        result.GetProperty("truncated").GetBoolean().Should().BeTrue();
        result.GetProperty("hint").GetString().Should().Contain("'path'");
        result.GetProperty("folderRows").GetInt32().Should().Be(1000);
        result.GetProperty("extensionRows").GetInt32().Should().Be(300);
        result.GetProperty("byFolder").GetArrayLength().Should().BeInRange(1, 199);
        Row(result.GetProperty("total")).Should().Be(("", 0, files, 0, 0L, 0L, 50L * files));
    }

    [Fact]
    public void Result_right_at_the_budget_still_fits_the_response_envelope()
    {
        // 100 rows whose array serializes to exactly MaxChars: the result alone would just fit, the response would not.
        var rows = Enumerable.Repeat(new string('x', 480), 100).ToArray();
        var length = JsonSerializer.Serialize(rows, ControlJson.Options).Length;
        rows[^1] = new string('x', 480 + ControlService.MaxChars - length);
        JsonSerializer.Serialize(rows, ControlJson.Options).Length.Should().Be(ControlService.MaxChars);

        var fitted = ControlService.FitToBudget(rows.Length, count => rows.Take(count).ToArray());

        var response = ControlResponse.Success(long.MaxValue.ToString(), fitted);
        JsonSerializer.Serialize(response, ControlJson.Options).Length.Should().BeLessThanOrEqualTo(ControlService.MaxChars);
        fitted.Should().HaveCount(99);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The service over a view model whose Versions tab has indexed the target's version folders.</summary>
    private async Task<ControlService> CreateWithIndexedTargetAsync()
    {
        var vm = _fixture.Create();
        await vm.Plans.Single(p => p.Id == "p1").Versions.RefreshCommand.ExecuteAsync(null);
        return new ControlService(vm, _ui);
    }

    private static IEnumerable<(string Key, int Added, int Changed, int Deleted, long AddedBytes, long DeletedBytes, long Net)> Rows(
        JsonElement rows) => rows.EnumerateArray().Select(Row).ToList();

    private static (string Key, int Added, int Changed, int Deleted, long AddedBytes, long DeletedBytes, long Net) Row(JsonElement row) =>
        (row.TryGetProperty("key", out var key) ? key.GetString()! : "", row.GetProperty("added").GetInt32(),
            row.GetProperty("changed").GetInt32(), row.GetProperty("deleted").GetInt32(),
            row.GetProperty("addedSize").GetProperty("bytes").GetInt64(), row.GetProperty("deletedSize").GetProperty("bytes").GetInt64(),
            row.GetProperty("net").GetProperty("bytes").GetInt64());

    private static ControlRequest Request(string op, object args) =>
        new("1", op, JsonSerializer.SerializeToElement(args, ControlJson.Options));

    private static async Task<JsonElement> OkAsync(ControlService service, string op, object args)
    {
        var response = await service.HandleAsync(Request(op, args), CancellationToken.None);
        response.Ok.Should().BeTrue(response.Error?.Message);
        return response.Result!.Value;
    }

    private static async Task<ControlError> FailAsync(ControlService service, string op, object args)
    {
        var response = await service.HandleAsync(Request(op, args), CancellationToken.None);
        response.Ok.Should().BeFalse();
        return response.Error!;
    }

    /// <summary>Writes a file with a fixed time, so that equal files in the source and a version compare as unchanged.</summary>
    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, Time);
    }
}
