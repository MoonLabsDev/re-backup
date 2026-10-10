using System.IO;
using System.Text;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>
/// "Restore to the original location" of a plan whose source is a storage that is no folder (as S3 is): the version goes
/// back into that storage, opened from the plan's source through the app's storage factory.
/// </summary>
public class VersionsRestoreStorageTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly MainViewModelFixture _fixture = new();
    private readonly InMemoryStorage _bucket = new(StorageCapabilities.None);
    private readonly IStorageFactory _storages;
    private readonly BackupPlan _plan;

    public VersionsRestoreStorageTests()
    {
        _storages = new LambdaStorageFactory(location =>
            location.Kind == "mem" ? _bucket : new StorageFactory().Open(location));
        _plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = new StorageLocation("mem", "photos", "conn-1"),
            Target = StorageLocation.FileSystem(Path.Combine(_fixture.Root, "target")),
        };
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Restore_to_original_location_writes_into_the_plans_source_storage()
    {
        _bucket.AddFile("a.txt", Encoding.UTF8.GetBytes("alpha"));
        var run = await new BackupRunner(_storages).RunAsync(new BackupRequest(_plan, [], RunTrigger.Manual));
        run.Status.Should().Be(RunStatus.Completed, run.Reason);
        await _bucket.DeleteAsync(["a.txt"], CancellationToken.None);

        var worker = new VersionIndexWorker(new VersionIndexSet(Path.Combine(_fixture.Root, "index")));
        var context = new VersionsContext(worker, _fixture.Dialogs, _ => { }, _storages, MachineName: "THIS-PC",
            DescribeLocation: location => "Offsite · s3://bucket/" + location.Path);
        var vm = new VersionsViewModel(() => _plan, () => false, new FakeFolderOpener(), context);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedVersion = vm.VersionRows.Should().ContainSingle().Subject;
        var row = await WaitForAsync(() => vm.Tree.Rows.FirstOrDefault(r => !r.IsMessage && r.Name == "a.txt"));
        vm.Tree.SelectedNode = row;

        await vm.RestoreToOriginalCommand.ExecuteAsync(null);

        _fixture.Dialogs.Errors.Should().BeEmpty();
        _fixture.Dialogs.Confirms.Should().ContainSingle().Which.Should()
            .Contain("Offsite · s3://bucket/photos/a.txt", "the destination is named by connection, bucket and prefix")
            .And.NotContain("mem://");
        _bucket.Files.Should().Equal("a.txt");
        Encoding.UTF8.GetString(_bucket.ReadAllBytes("a.txt")).Should().Be("alpha");
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe) where T : class
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } found)
                return found;
            await Task.Delay(20);
        }
        throw new TimeoutException("The condition was not met in time.");
    }

    private sealed class LambdaStorageFactory(Func<StorageLocation, IStorage> open) : IStorageFactory
    {
        public IStorage Open(StorageLocation location) => open(location);
    }
}
