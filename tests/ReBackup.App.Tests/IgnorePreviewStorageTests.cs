using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>The Ignore tab scans a source that is no folder (as S3 is) through the app's storage factory.</summary>
public class IgnorePreviewStorageTests : IDisposable
{
    private readonly MainViewModelFixture _fixture = new();   // the labels the view model's texts need

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task A_storage_source_is_scanned_and_its_ignore_file_applied()
    {
        var bucket = new InMemoryStorage(StorageCapabilities.None);
        bucket.AddFile("a.txt", [1, 2, 3]);
        bucket.AddFile("docs/.backupignore", "*.tmp"u8.ToArray());
        bucket.AddFile("docs/scratch.tmp", [1]);
        var opened = new List<StorageLocation>();
        var factory = new Factory(location =>
        {
            opened.Add(location);
            return bucket;
        });
        var location = new StorageLocation("mem", "", "conn-1");
        var vm = new IgnorePreviewViewModel(() => location, () => new IgnoreSettings(), () => [], factory);

        await vm.IndexCommand.ExecuteAsync(null);

        vm.ErrorText.Should().BeNull();
        opened.Should().AllBeEquivalentTo(location);
        vm.Root.Should().NotBeNull();
        vm.Root!.IncludedFiles.Should().Be(2);
        vm.Root.IgnoredFiles.Should().Be(1);
    }

    private sealed class Factory(Func<StorageLocation, IStorage> open) : IStorageFactory
    {
        public IStorage Open(StorageLocation location) => open(location);
    }
}
