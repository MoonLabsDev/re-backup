using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Backup;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>
/// The Ignore tab compared with a version of the plan: each row shows the version's size and what the next backup would
/// hold minus it, so that what grew is easy to find.
/// </summary>
public class IgnorePreviewCompareTests : IDisposable
{
    private const string Version = "2026_09_29-14_05 Projects";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly MainViewModelFixture _fixture = new();   // the labels the view model's texts need
    private readonly InMemoryStorage _source = new(StorageCapabilities.None);
    private readonly InMemoryStorage _target = new();
    private readonly VersionIndexSet _indexes;
    private int _syncRequests;

    public IgnorePreviewCompareTests()
    {
        _source.AddFile("a.txt", new byte[5]);
        _source.AddFile("keep.txt", new byte[3]);
        _source.AddFile("big/x.bin", new byte[100]);
        _target.AddFile($"{Version}/a.txt", new byte[2]);
        _target.AddFile($"{Version}/keep.txt", new byte[3]);
        _target.AddFile($"{Version}/old.txt", new byte[7]);
        _indexes = new VersionIndexSet(Path.Combine(_fixture.Root, "index"));
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Rows_show_the_version_size_and_what_the_next_backup_adds()
    {
        var folders = await VersionCatalog.ListAsync(_target, "p1", "Projects");
        _indexes.For("p1").Sync(folders, _target);
        var vm = Create(() => _indexes.For("p1"));
        await vm.IndexCommand.ExecuteAsync(null);

        vm.EnsureCompareChoices();
        var choice = await WaitForAsync(() => vm.CompareChoices.FirstOrDefault(c => c.Name == Version));
        _syncRequests.Should().Be(1, "the Versions tab is asked once to bring the index up to date");
        choice.Label.Should().EndWith($" [{Formats.Bytes(12)}]", "the box shows each version's backup size");
        vm.SelectedCompare = choice;

        var changed = await RowAsync(vm, "a.txt");
        (changed.CompareSizeText, changed.SizeChangeText).Should().Be((Formats.Bytes(2), "+" + Formats.Bytes(3)));
        changed.IsGrowth.Should().BeTrue();
        var same = await RowAsync(vm, "keep.txt");
        (same.CompareSizeText, same.SizeChangeText).Should().Be((Formats.Bytes(3), ""));
        var added = await RowAsync(vm, "big");
        (added.CompareSizeText, added.SizeChangeText).Should().Be(("", "+" + Formats.Bytes(100)), "the version lacks the folder");
        var root = vm.Tree.Rows.First();
        root.SizeChangeText.Should().Be("+" + Formats.Bytes(108 - 12));

        vm.SelectedCompare = vm.CompareChoices.First();
        vm.Tree.IsComparing.Should().BeFalse();
        changed.SizeChangeText.Should().BeEmpty();
    }

    [Fact]
    public async Task Shrinking_entries_are_negative()
    {
        _target.AddFile($"{Version}/keep.txt", new byte[10]);   // the source's keep.txt is 3 bytes
        var folders = await VersionCatalog.ListAsync(_target, "p1", "Projects");
        _indexes.For("p1").Sync(folders, _target);
        var vm = Create(() => _indexes.For("p1"));
        await vm.IndexCommand.ExecuteAsync(null);

        vm.EnsureCompareChoices();
        vm.SelectedCompare = await WaitForAsync(() => vm.CompareChoices.FirstOrDefault(c => c.Name == Version));

        var shrunk = await RowAsync(vm, "keep.txt");
        shrunk.SizeChangeText.Should().Be("−" + Formats.Bytes(7));
        shrunk.IsShrink.Should().BeTrue();
        shrunk.IsGrowth.Should().BeFalse();
    }

    [Fact]
    public async Task A_new_plan_has_nothing_to_compare_with()
    {
        var vm = Create(null);
        await vm.IndexCommand.ExecuteAsync(null);

        vm.EnsureCompareChoices();
        await Task.Delay(100);

        vm.CompareChoices.Should().ContainSingle().Which.Name.Should().BeNull();
        _syncRequests.Should().Be(0);
        vm.Tree.Rows.Should().OnlyContain(r => r.CompareSizeText == "" && r.SizeChangeText == "");
    }

    private IgnorePreviewViewModel Create(Func<VersionIndex>? index)
    {
        var location = new StorageLocation("mem", "", "conn-1");
        return new IgnorePreviewViewModel(() => location, () => new IgnoreSettings(), () => [], new Factory(_ => _source),
            versionIndex: () => index, requestIndexSync: () => _syncRequests++);
    }

    /// <summary>The row of <paramref name="name"/> once its comparison is known.</summary>
    private static Task<PreviewRowViewModel> RowAsync(IgnorePreviewViewModel vm, string name) =>
        WaitForAsync(() => vm.Tree.Rows.FirstOrDefault(r => r.Name == name && r.SizeChange is not null));

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

    private sealed class Factory(Func<StorageLocation, IStorage> open) : IStorageFactory
    {
        public IStorage Open(StorageLocation location) => open(location);
    }
}
