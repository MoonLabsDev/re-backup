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
/// </summary>
public class RetentionPoolTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const string A = "2026_09_28-14_05 Projects";
    private const string B = "2026_09_29-14_05 Projects";
    private const string C = "2026_09_30-14_05 Projects";
    private const string Calculating = "calculating…";

    private readonly MainViewModelFixture _fixture = new();
    private readonly InMemoryStorage _target = new();
    private readonly VersionIndexSet _indexes;
    private readonly SerialContext _ui = new();
    private int _syncRequests;
    private int _indexOpens;

    public RetentionPoolTests()
    {
        _indexes = new VersionIndexSet(Path.Combine(_fixture.Root, "index"));
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

    private RetentionPreviewViewModel Create(BackupPlan plan, long? fallback = null)
    {
        var storages = new LambdaStorageFactory(location => location.Kind == "s3" ? _target : new StorageFactory().Open(location));
        return new RetentionPreviewViewModel(() => plan, () => fallback, new FakeFolderOpener(), storages, () => [],
            poolIndex: p =>
            {
                Interlocked.Increment(ref _indexOpens);
                return _indexes.For(p.Id);
            },
            requestIndexSync: () => Interlocked.Increment(ref _syncRequests));
    }

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

    private async Task SyncIndexAsync()
    {
        var folders = await VersionCatalog.ListAsync(_target, "p1", "Projects");
        _indexes.For("p1").Sync(folders, _target, scanFallback: false);
    }

    /// <summary>Reads the target on the serial "UI" context and waits until the forecast settled.</summary>
    private async Task LoadAsync(RetentionPreviewViewModel preview)
    {
        await _ui.RunAsync(() => preview.RefreshCommand.ExecuteAsync(null)).WaitAsync(Timeout);
        await WaitUntilAsync(() => preview.HasForecast, "the forecast is calculated");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting until " + because);
            await Task.Delay(10);
        }
    }

    private static string Summary(int count, long pool, long full, int keep, int delete, long freed) =>
        $"{count} versions, pool {Formats.Bytes(pool)} ({Formats.Bytes(full)} as full copies)  →  " +
        $"the rules keep {keep} and delete {delete} (frees {Formats.Bytes(freed)})";

    private static string PoolSpace(long bytes) => $"≈ {Formats.Bytes(bytes)} (pool, estimated)";

    // ------------------------------------------------------------------ what the rules free

    [Fact]
    public async Task The_rules_free_exactly_the_pool_content_that_only_deleted_versions_hold()
    {
        WriteThreeVersions();
        await SyncIndexAsync();
        var preview = Create(Plan(incremental: true, Daily(1)));

        await LoadAsync(preview);
        await WaitUntilAsync(() => !preview.NowSummary.Contains(Calculating), "the pool numbers are read");

        // A and B are deleted: their shared 1000 bytes count once and are freed, as are their own 100 and 10 bytes;
        // the 5000 bytes B shares with the kept C stay.
        preview.NowSummary.Should().Be(Summary(3, pool: 6111, full: 12111, keep: 1, delete: 2, freed: 1110));
        _syncRequests.Should().Be(0);
    }

    [Fact]
    public async Task Rules_that_delete_nothing_free_nothing_in_the_pool()
    {
        WriteThreeVersions();
        await SyncIndexAsync();
        var preview = Create(Plan(incremental: true));

        await LoadAsync(preview);
        await WaitUntilAsync(() => !preview.NowSummary.Contains(Calculating), "the pool numbers are read");

        preview.NowSummary.Should().Be(Summary(3, pool: 6111, full: 12111, keep: 3, delete: 0, freed: 0));
    }

    // ------------------------------------------------------------------ forecast

    [Fact]
    public async Task The_forecast_grows_from_the_newest_version_by_the_average_new_content()
    {
        WriteThreeVersions();
        await SyncIndexAsync();
        var preview = Create(Plan(incremental: true, Daily(3)));

        await LoadAsync(preview);
        await WaitUntilAsync(() => !preview.ForecastSpaceText.Contains(Calculating), "the pool forecast is read");

        // Base: the newest version (5001 bytes); growth: (6111 - 5001) / 2 = 555 bytes per further version.
        var steady = preview.ForecastVersions!.Value;
        steady.Should().BeGreaterThan(1);
        var expected = 5001 + 555L * (steady - 1);
        preview.ForecastBytes.Should().Be(expected);
        preview.ForecastSpaceText.Should().Be(PoolSpace(expected));
        preview.FullSummary.Should().Contain(Formats.Bytes(expected));
    }

    [Fact]
    public async Task With_one_version_the_forecast_is_that_version_alone()
    {
        WriteC();
        await SyncIndexAsync();
        var preview = Create(Plan(incremental: true, Daily(7)));

        await LoadAsync(preview);
        await WaitUntilAsync(() => !preview.ForecastSpaceText.Contains(Calculating), "the pool forecast is read");

        preview.ForecastVersions.Should().BeGreaterThan(1);
        preview.ForecastBytes.Should().Be(5001, "without a second version there is no growth to go by");
        preview.ForecastSpaceText.Should().Be(PoolSpace(5001));
    }

    // ------------------------------------------------------------------ index not ready

    [Fact]
    public async Task Until_the_index_is_synced_the_pool_numbers_are_being_calculated()
    {
        WriteThreeVersions();   // the index has not seen them yet
        var preview = Create(Plan(incremental: true, Daily(1)));

        await LoadAsync(preview);
        await WaitUntilAsync(() => _syncRequests == 1, "a sync of the index is asked for");

        preview.NowSummary.Should().Be(
            $"3 versions, pool {Calculating} ({Formats.Bytes(12111)} as full copies)  →  " +
            $"the rules keep 1 and delete 2 (frees {Calculating})");
        preview.ForecastSpaceText.Should().Be(Calculating);
        preview.ForecastBytes.Should().BeNull("a full-copy estimate is never shown as the pool's");

        await SyncIndexAsync();
        await _ui.RunAsync(() =>
        {
            preview.OnIndexSynced();
            return Task.CompletedTask;
        }).WaitAsync(Timeout);

        await WaitUntilAsync(() => !preview.NowSummary.Contains(Calculating) && !preview.ForecastSpaceText.Contains(Calculating),
            "the pool numbers are read after the sync");
        preview.NowSummary.Should().Be(Summary(3, pool: 6111, full: 12111, keep: 1, delete: 2, freed: 1110));
        preview.ForecastSpaceText.Should().Be(PoolSpace(preview.ForecastBytes!.Value));
        _syncRequests.Should().Be(1, "the sync was asked for once");
    }

    [Fact]
    public async Task A_version_the_index_does_not_have_yet_keeps_the_pool_numbers_open()
    {
        WriteThreeVersions();
        await SyncIndexAsync();
        PoolTargets.WriteVersion(_target, "2026_10_01-14_05 Projects", "p1", "Projects", new PoolFile("d.bin", "d", 7));
        var preview = Create(Plan(incremental: true, Daily(1)));

        await LoadAsync(preview);
        await WaitUntilAsync(() => _syncRequests == 1, "a sync of the index is asked for");

        preview.NowSummary.Should().Contain($"pool {Calculating}").And.Contain($"frees {Calculating}");
        preview.ForecastSpaceText.Should().Be(Calculating);
    }

    // ------------------------------------------------------------------ full plans

    [Fact]
    public async Task A_full_plan_shows_full_copy_numbers_and_never_reads_the_index()
    {
        WriteThreeVersions();
        var preview = Create(Plan(incremental: false, Daily(1)));

        await LoadAsync(preview);

        preview.NowSummary.Should().Be(
            $"3 versions, {Formats.Bytes(12111)}  →  the rules keep 1 and delete 2 (frees {Formats.Bytes(1100 + 6010)})");
        preview.ForecastSpaceText.Should().Be("≈ " + Formats.Bytes(preview.ForecastBytes!.Value));
        preview.ForecastBytes.Should().Be((12111 / 3) * preview.ForecastVersions!.Value);
        _indexOpens.Should().Be(0);
        _syncRequests.Should().Be(0);
    }

    // ------------------------------------------------------------------ the Versions tab's sync

    [Fact]
    public async Task The_versions_tab_reports_each_finished_sync()
    {
        WriteThreeVersions();
        var storages = new LambdaStorageFactory(location => location.Kind == "s3" ? _target : new StorageFactory().Open(location));
        var context = new VersionsContext(new VersionIndexWorker(_indexes), _fixture.Dialogs, _ => { }, storages, MachineName: "THIS-PC");
        var plan = Plan(incremental: true);
        var versions = new VersionsViewModel(() => plan, () => false, new FakeFolderOpener(), context);
        var synced = 0;
        versions.IndexSynced += () => synced++;

        await versions.RefreshCommand.ExecuteAsync(null);

        synced.Should().Be(1);
        _indexes.For("p1").Versions().Select(v => v.Name).Should().Equal(A, B, C);
    }

    private sealed class LambdaStorageFactory(Func<StorageLocation, IStorage> open) : IStorageFactory
    {
        public IStorage Open(StorageLocation location) => open(location);
    }
}
