using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>
/// The Retention tab of an incremental plan: what the rules free and what the plan needs at full extension come from the
/// pool (each content once), read from the plan's version index; full-copy numbers are never shown as pool numbers.
/// All state is read on the serial "UI" context the view model runs on.
/// </summary>
public class RetentionPoolTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const string X = "2026_09_27-14_05 Projects";
    private const string A = "2026_09_28-14_05 Projects";
    private const string B = "2026_09_29-14_05 Projects";
    private const string C = "2026_09_30-14_05 Projects";
    private const string D = "2026_10_01-14_05 Projects";
    private const string Calculating = "calculating…";
    private const string Unknown = "unknown";

    private readonly MainViewModelFixture _fixture = new();
    private readonly InMemoryStorage _target = new();
    private readonly VersionIndexSet _indexes;
    private readonly SerialContext _ui = new();
    private readonly string _folderTarget;
    private readonly string _otherFolder;
    private int _syncRequests;
    private int _indexOpens;

    public RetentionPoolTests()
    {
        _indexes = new VersionIndexSet(Path.Combine(_fixture.Root, "index"));
        _folderTarget = Path.Combine(_fixture.Root, "dst");
        _otherFolder = Path.Combine(_fixture.Root, "other");
        PoolTargets.WriteStore(_target, "p1");
    }

    public void Dispose()
    {
        _ui.Dispose();
        _fixture.Dispose();
    }

    private static RetentionRule Daily(int keep) => new() { Period = RetentionPeriod.Daily, Keep = keep };

    private BackupPlan Plan(bool incremental, params RetentionRule[] rules) => new()
    {
        Id = "p1",
        Name = "Projects",
        Source = StorageLocation.FileSystem(Path.Combine(_fixture.Root, "source")),
        Target = new StorageLocation("s3", "", "conn-1"),
        Incremental = incremental,
        Retention = [.. rules],
    };

    /// <summary>The s3 target and the two folders <see cref="_folderTarget"/> and <see cref="_otherFolder"/> are the in-memory target.</summary>
    private LambdaStorageFactory Storages() => new(location =>
        location.Kind == "s3" || location.Path == _folderTarget || location.Path == _otherFolder
            ? _target
            : new StorageFactory().Open(location));

    /// <param name="hasIndex">False: the plan has no index (e.g. it is new).</param>
    /// <param name="gate">When set, the first opening of the index waits for it.</param>
    private RetentionPreviewViewModel Create(Func<BackupPlan> plan, bool hasIndex = true, ManualResetEventSlim? gate = null) =>
        new(plan, () => 1_000_000, new FakeFolderOpener(), Storages(), () => [],
            poolIndex: p => hasIndex
                ? () =>
                {
                    if (Interlocked.Increment(ref _indexOpens) == 1)
                        gate?.Wait(Timeout);
                    return _indexes.For(p.Id);
                }
                : null,
            requestIndexSync: () => Interlocked.Increment(ref _syncRequests));

    private RetentionPreviewViewModel Create(BackupPlan plan, bool hasIndex = true) => Create(() => plan, hasIndex);

    /// <summary>
    /// A and B share 1000 bytes, B and C share 5000; each has content of its own (100, 10, 1 bytes).
    /// Full copies: 1100 + 6010 + 5001 = 12111 bytes; the pool: 6111 bytes.
    /// </summary>
    private void WriteThreeVersions()
    {
        PoolTargets.WriteVersion(_target, A, "p1", "Projects", new PoolFile("ab.bin", "ab", 1000), new PoolFile("a.bin", "a", 100));
        PoolTargets.WriteVersion(_target, B, "p1", "Projects", new PoolFile("ab.bin", "ab", 1000), new PoolFile("b.bin", "b", 10),
            new PoolFile("bc.bin", "bc", 5000));
        WriteC();
    }

    private void WriteC() =>
        PoolTargets.WriteVersion(_target, C, "p1", "Projects", new PoolFile("bc.bin", "bc", 5000), new PoolFile("c.bin", "c", 1));

    /// <summary>A version of another plan, named like this plan's, that holds A and B's shared 1000 bytes.</summary>
    private void WriteForeign() =>
        PoolTargets.WriteVersion(_target, X, "other", "Projects", new PoolFile("ab.bin", "ab", 1000));

    private async Task SyncIndexAsync()
    {
        var folders = await VersionCatalog.ListAsync(_target, "p1", "Projects");
        _indexes.For("p1").Sync(folders, _target, scanFallback: false);
    }

    /// <summary>Reads <paramref name="read"/> on the serial "UI" context.</summary>
    private async Task<T> OnUi<T>(Func<T> read)
    {
        var value = default(T)!;
        await _ui.RunAsync(() =>
        {
            value = read();
            return Task.CompletedTask;
        }).WaitAsync(Timeout);
        return value;
    }

    private Task OnUi(Action act) => OnUi(() =>
    {
        act();
        return 0;
    });

    private async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await OnUi(condition))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting until " + because);
            await Task.Delay(10);
        }
    }

    /// <summary>Reads the target on the "UI" context and waits until the forecast is there.</summary>
    private async Task LoadAsync(RetentionPreviewViewModel preview)
    {
        await _ui.RunAsync(() => preview.RefreshCommand.ExecuteAsync(null)).WaitAsync(Timeout);
        await WaitUntilAsync(() => preview.HasForecast, "the forecast is calculated");
    }

    private Task SettledAsync(RetentionPreviewViewModel preview) =>
        WaitUntilAsync(() => !preview.NowSummary.Contains(Calculating) && !preview.ForecastSpaceText.Contains(Calculating),
            "the pool numbers are read");

    private static string Summary(int count, string pool, long full, int keep, int delete, string freed) =>
        $"{count} versions, pool {pool} ({Formats.Bytes(full)} as full copies)  →  " +
        $"the rules keep {keep} and delete {delete} (frees {freed})";

    private static string Summary(int count, long pool, long full, int keep, int delete, long freed) =>
        Summary(count, Formats.Bytes(pool), full, keep, delete, Formats.Bytes(freed));

    private static string PoolSpace(long bytes) => $"≈ {Formats.Bytes(bytes)} (pool, estimated)";

    // ------------------------------------------------------------------ what the rules free

    [Fact]
    public async Task The_rules_free_exactly_the_pool_content_that_only_deleted_versions_hold()
    {
        WriteThreeVersions();
        await SyncIndexAsync();
        var preview = Create(Plan(incremental: true, Daily(1)));

        await LoadAsync(preview);
        await SettledAsync(preview);

        // A and B are deleted: their shared 1000 bytes count once and are freed, as are their own 100 and 10 bytes;
        // the 5000 bytes B shares with the kept C stay.
        (await OnUi(() => preview.NowSummary)).Should().Be(Summary(3, pool: 6111, full: 12111, keep: 1, delete: 2, freed: 1110));
        _syncRequests.Should().Be(0);
    }

    [Fact]
    public async Task Rules_that_delete_nothing_free_nothing_in_the_pool()
    {
        WriteThreeVersions();
        await SyncIndexAsync();
        var preview = Create(Plan(incremental: true));

        await LoadAsync(preview);
        await SettledAsync(preview);

        (await OnUi(() => preview.NowSummary)).Should().Be(Summary(3, pool: 6111, full: 12111, keep: 3, delete: 0, freed: 0));
    }

    [Fact]
    public async Task A_version_that_is_not_managed_keeps_the_content_it_shares()
    {
        WriteThreeVersions();
        WriteForeign();
        await SyncIndexAsync();
        var preview = Create(Plan(incremental: true, Daily(1)));

        await LoadAsync(preview);
        await SettledAsync(preview);

        // The foreign version is not deleted: the 1000 bytes it shares with A and B stay; it is not in the plan's pool.
        (await OnUi(() => preview.NowSummary)).Should().Be(
            Summary(3, pool: 6111, full: 12111, keep: 1, delete: 2, freed: 110) + "  ·  1 not managed");
    }

    // ------------------------------------------------------------------ forecast

    [Fact]
    public async Task The_forecast_grows_from_the_newest_version_by_the_average_new_content()
    {
        WriteThreeVersions();
        await SyncIndexAsync();
        var preview = Create(Plan(incremental: true, Daily(3)));

        await LoadAsync(preview);
        await SettledAsync(preview);

        // Base: the newest version (5001 bytes); growth: (6111 - 5001) / 2 = 555 bytes per further version.
        var (steady, bytes, space, full) = await OnUi(() =>
            (preview.ForecastVersions!.Value, preview.ForecastBytes, preview.ForecastSpaceText, preview.FullSummary));
        steady.Should().BeGreaterThan(1);
        var expected = 5001 + 555L * (steady - 1);
        bytes.Should().Be(expected);
        space.Should().Be(PoolSpace(expected));
        full.Should().Contain($"about {Formats.Bytes(expected)} in the pool");
    }

    [Fact]
    public async Task With_one_version_the_forecast_is_that_version_alone()
    {
        WriteC();
        await SyncIndexAsync();
        var preview = Create(Plan(incremental: true, Daily(7)));

        await LoadAsync(preview);
        await SettledAsync(preview);

        (await OnUi(() => preview.ForecastVersions)).Should().BeGreaterThan(1);
        (await OnUi(() => preview.ForecastBytes)).Should().Be(5001, "without a second version there is no growth to go by");
        (await OnUi(() => preview.ForecastSpaceText)).Should().Be(PoolSpace(5001));
    }

    [Fact]
    public async Task Without_owned_versions_the_pool_forecast_is_unknown()
    {
        WriteForeign();
        await SyncIndexAsync();
        var preview = Create(Plan(incremental: true, Daily(7)));

        await LoadAsync(preview);
        await SettledAsync(preview);

        (await OnUi(() => preview.ForecastBytes)).Should().BeNull("no version tells the size, and the full-copy fallback is no pool size");
        (await OnUi(() => preview.ForecastSpaceText)).Should().Be(Unknown);
    }

    // ------------------------------------------------------------------ index not ready

    [Fact]
    public async Task Until_the_index_is_synced_the_pool_numbers_are_being_calculated()
    {
        WriteThreeVersions();   // the index has not seen them yet
        var preview = Create(Plan(incremental: true, Daily(1)));

        await LoadAsync(preview);
        await WaitUntilAsync(() => _syncRequests == 1, "a sync of the index is asked for");

        (await OnUi(() => preview.NowSummary)).Should().Be(Summary(3, Calculating, 12111, 1, 2, Calculating));
        (await OnUi(() => preview.ForecastSpaceText)).Should().Be(Calculating);
        (await OnUi(() => preview.ForecastBytes)).Should().BeNull("a full-copy estimate is never shown as the pool's");

        await SyncIndexAsync();
        await OnUi(preview.OnIndexSyncEnded);
        await SettledAsync(preview);

        (await OnUi(() => preview.NowSummary)).Should().Be(Summary(3, pool: 6111, full: 12111, keep: 1, delete: 2, freed: 1110));
        (await OnUi(() => preview.ForecastSpaceText)).Should().Be(PoolSpace((await OnUi(() => preview.ForecastBytes))!.Value));
        _syncRequests.Should().Be(1, "the sync was asked for once");
    }

    [Fact]
    public async Task After_a_sync_that_left_out_a_deleted_version_the_numbers_are_lower_bounds()
    {
        WriteThreeVersions();
        await SyncIndexAsync();
        PoolTargets.WriteVersion(_target, X, "p1", "Projects", new PoolFile("x.bin", "x", 3));   // the index lacks it
        var preview = Create(Plan(incremental: true, Daily(1)));

        await LoadAsync(preview);
        await WaitUntilAsync(() => _syncRequests == 1, "a sync of the index is asked for");
        (await OnUi(() => preview.NowSummary)).Should().Contain($"pool {Calculating}").And.Contain($"frees {Calculating}");

        await OnUi(preview.OnIndexSyncEnded);   // the sync ended without importing it (e.g. it failed)
        await SettledAsync(preview);

        (await OnUi(() => preview.NowSummary)).Should().Be(
            Summary(4, $"≥ {Formats.Bytes(6111)}", 12114, 1, 3, $"≥ {Formats.Bytes(1110)}") + "  ·  1 not indexed");
        var steady = (await OnUi(() => preview.ForecastVersions))!.Value;
        (await OnUi(() => preview.ForecastSpaceText)).Should().Be(PoolSpace(5001 + 555L * (steady - 1)),
            "the newest version is indexed; the growth comes from the indexed ones");
        _syncRequests.Should().Be(1);
    }

    [Fact]
    public async Task After_a_sync_that_left_out_a_kept_version_frees_and_forecast_are_unknown()
    {
        WriteThreeVersions();
        await SyncIndexAsync();
        PoolTargets.WriteVersion(_target, D, "p1", "Projects", new PoolFile("bc.bin", "bc", 5000));   // newest, not indexed
        var preview = Create(Plan(incremental: true, Daily(1)));

        await LoadAsync(preview);
        await WaitUntilAsync(() => _syncRequests == 1, "a sync of the index is asked for");
        await OnUi(preview.OnIndexSyncEnded);
        await SettledAsync(preview);

        (await OnUi(() => preview.NowSummary)).Should().Be(
            Summary(4, $"≥ {Formats.Bytes(6111)}", 17111, 1, 3, Unknown) + "  ·  1 not indexed",
            "the kept version may hold what the deleted ones do");
        (await OnUi(() => preview.ForecastSpaceText)).Should().Be(Unknown, "the newest version is not indexed");
    }

    [Fact]
    public async Task Without_an_index_the_pool_numbers_are_unknown_and_no_sync_is_asked_for()
    {
        WriteThreeVersions();
        var preview = Create(Plan(incremental: true, Daily(1)), hasIndex: false);

        await LoadAsync(preview);
        await SettledAsync(preview);

        (await OnUi(() => preview.NowSummary)).Should().Be(Summary(3, Unknown, 12111, 1, 2, Unknown));
        (await OnUi(() => preview.ForecastSpaceText)).Should().Be(Unknown);
        _syncRequests.Should().Be(0);
    }

    // ------------------------------------------------------------------ reads that overlap

    [Fact]
    public async Task A_read_overtaken_by_a_later_one_is_dropped()
    {
        WriteThreeVersions();
        await SyncIndexAsync();
        var plan = Plan(incremental: true, Daily(1));
        using var gate = new ManualResetEventSlim();
        var preview = Create(() => plan, gate: gate);

        await LoadAsync(preview);   // its read of the pool waits at the gate
        await OnUi(() =>
        {
            plan.Retention = [];   // the rules change: the next read takes over
            preview.RefreshTexts();
        });
        await SettledAsync(preview);
        (await OnUi(() => preview.NowSummary)).Should().Be(Summary(3, pool: 6111, full: 12111, keep: 3, delete: 0, freed: 0));

        gate.Set();
        await Task.Delay(200);
        await OnUi(() => 0);
        await OnUi(() => 0);

        (await OnUi(() => preview.NowSummary)).Should().Be(Summary(3, pool: 6111, full: 12111, keep: 3, delete: 0, freed: 0),
            "the first read belongs to the old rules");
    }

    [Fact]
    public async Task A_sync_that_ends_while_the_pool_is_read_reads_it_again()
    {
        WriteThreeVersions();   // not indexed yet
        using var gate = new ManualResetEventSlim();
        var preview = Create(() => Plan(incremental: true, Daily(1)), gate: gate);

        await LoadAsync(preview);   // its read waits at the gate
        await SyncIndexAsync();
        await OnUi(preview.OnIndexSyncEnded);   // a sync from the Versions tab ends meanwhile
        await SettledAsync(preview);
        gate.Set();
        await Task.Delay(200);
        await OnUi(() => 0);

        (await OnUi(() => preview.NowSummary)).Should().Be(Summary(3, pool: 6111, full: 12111, keep: 1, delete: 2, freed: 1110));
        _syncRequests.Should().Be(0, "the read after the sync found every version");
    }

    // ------------------------------------------------------------------ full plans

    [Fact]
    public async Task A_full_plan_shows_full_copy_numbers_and_never_reads_the_index()
    {
        WriteThreeVersions();
        var preview = Create(Plan(incremental: false, Daily(1)));

        await LoadAsync(preview);

        (await OnUi(() => preview.NowSummary)).Should().Be(
            $"3 versions, {Formats.Bytes(12111)}  →  the rules keep 1 and delete 2 (frees {Formats.Bytes(1100 + 6010)})");
        var (bytes, count, space) = await OnUi(() => (preview.ForecastBytes, preview.ForecastVersions, preview.ForecastSpaceText));
        bytes.Should().Be((12111 / 3) * count!.Value);
        space.Should().Be("≈ " + Formats.Bytes(bytes!.Value));
        _indexOpens.Should().Be(0);
        _syncRequests.Should().Be(0);
    }

    // ------------------------------------------------------------------ the Versions tab's sync

    private VersionsViewModel CreateVersions(BackupPlan plan, IStorageFactory storages)
    {
        var context = new VersionsContext(new VersionIndexWorker(_indexes), _fixture.Dialogs, _ => { }, storages, MachineName: "THIS-PC");
        return new VersionsViewModel(() => plan, () => false, new FakeFolderOpener(), context);
    }

    [Fact]
    public async Task The_versions_tab_reports_the_end_of_each_sync()
    {
        WriteThreeVersions();
        var versions = CreateVersions(Plan(incremental: true), Storages());
        var ended = 0;
        versions.IndexSyncEnded += () => ended++;

        await versions.RefreshCommand.ExecuteAsync(null);

        ended.Should().Be(1);
        _indexes.For("p1").Versions().Select(v => v.Name).Should().Equal(A, B, C);
    }

    [Fact]
    public async Task The_versions_tab_reports_the_end_of_a_failed_sync_too()
    {
        var versions = CreateVersions(Plan(incremental: true), new LambdaStorageFactory(_ => throw new IOException("broken")));
        var ended = 0;
        versions.IndexSyncEnded += () => ended++;

        await versions.RefreshCommand.ExecuteAsync(null);

        versions.Error.Should().NotBeNull();
        ended.Should().Be(1);
    }

    // ------------------------------------------------------------------ through the plan editor

    /// <summary>The plan editor of a saved plan "p1" whose folder target is the in-memory target.</summary>
    private PlanEditorViewModel Editor(bool incremental)
    {
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(Directory.CreateDirectory(Path.Combine(_fixture.Root, "source")).FullName),
            Target = StorageLocation.FileSystem(_folderTarget),
            Incremental = incremental,
            Retention = [Daily(1)],
        });
        _fixture.Storages = Storages();
        return _fixture.Create().Plans.Single();
    }

    [Fact]
    public async Task The_editor_syncs_the_index_for_the_pool_numbers()
    {
        WriteThreeVersions();   // not indexed yet
        var editor = Editor(incremental: true);

        await LoadAsync(editor.RetentionPreview);
        await SettledAsync(editor.RetentionPreview);

        (await OnUi(() => editor.RetentionPreview.NowSummary)).Should().Be(
            Summary(3, pool: 6111, full: 12111, keep: 1, delete: 2, freed: 1110));
        _indexes.For("p1").Versions().Should().HaveCount(3, "the Versions tab synced the index");
    }

    [Fact]
    public async Task An_edited_target_has_no_index_for_the_pool_numbers()
    {
        WriteThreeVersions();
        await SyncIndexAsync();
        var editor = Editor(incremental: true);

        await OnUi(() => editor.Target = _otherFolder);   // lists the same versions, but the index is the saved target's
        await LoadAsync(editor.RetentionPreview);
        await SettledAsync(editor.RetentionPreview);

        (await OnUi(() => editor.RetentionPreview.NowSummary)).Should().Be(Summary(3, Unknown, 12111, 1, 2, Unknown));
        (await OnUi(() => editor.RetentionPreview.ForecastSpaceText)).Should().Be(Unknown);
    }

    [Fact]
    public async Task A_new_plan_switched_to_incremental_has_no_index_for_the_pool_numbers()
    {
        WriteThreeVersions();
        await SyncIndexAsync();
        var editor = Editor(incremental: false);
        await OnUi(() =>
        {
            editor.MarkAsNew();
            editor.ChooseStorageFormatCommand.Execute("Incremental");
        });

        await LoadAsync(editor.RetentionPreview);
        await SettledAsync(editor.RetentionPreview);

        (await OnUi(() => editor.RetentionPreview.NowSummary)).Should().Be(Summary(3, Unknown, 12111, 1, 2, Unknown),
            "the index of the saved plan holds full copies");
    }

    private sealed class LambdaStorageFactory(Func<StorageLocation, IStorage> open) : IStorageFactory
    {
        public IStorage Open(StorageLocation location) => open(location);
    }
}
