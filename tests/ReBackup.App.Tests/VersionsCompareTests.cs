using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;

namespace ReBackup.App.Tests;

/// <summary>
/// The Versions tab compares two versions: each row shows A's size, B's size and the difference from the older to the
/// newer version, whichever of them is shown.
/// </summary>
public class VersionsCompareTests : IDisposable
{
    private const string Older = "2026_09_29-14_05 Projects";
    private const string Newer = "2026_09_30-14_05 Projects";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly MainViewModelFixture _fixture = new();
    private readonly BackupPlan _plan;

    public VersionsCompareTests()
    {
        var target = Path.Combine(_fixture.Root, "target");
        Write(target, Older, "a.txt", "alpha");
        Write(target, Older, "gone.txt", "xx");
        Write(target, Newer, "a.txt", "alpha-longer");
        Write(target, Newer, "new.txt", "abc");
        _plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(Path.Combine(_fixture.Root, "source")),
            Target = StorageLocation.FileSystem(target),
        };
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Rows_show_both_sizes_and_the_growth_from_older_to_newer()
    {
        var vm = await CompareAsync(shown: Newer, other: Older);

        var changed = await RowAsync(vm, "a.txt");
        (changed.SizeText, changed.CompareSizeText, changed.SizeChangeText).Should().Be(
            (Formats.Bytes(12), Formats.Bytes(5), "+" + Formats.Bytes(7)));
        changed.IsGrowth.Should().BeTrue();

        var added = await RowAsync(vm, "new.txt");
        (added.SizeText, added.CompareSizeText, added.SizeChangeText).Should().Be(
            (Formats.Bytes(3), "", "+" + Formats.Bytes(3)));

        var deleted = await RowAsync(vm, "gone.txt");
        (deleted.SizeText, deleted.CompareSizeText, deleted.SizeChangeText).Should().Be(
            ("", Formats.Bytes(2), "−" + Formats.Bytes(2)), "an entry only in B has no size in A");
        deleted.IsGrowth.Should().BeFalse();
    }

    [Fact]
    public async Task Difference_keeps_its_direction_when_the_older_version_is_shown()
    {
        var vm = await CompareAsync(shown: Older, other: Newer);

        var changed = await RowAsync(vm, "a.txt");
        (changed.SizeText, changed.CompareSizeText, changed.SizeChangeText).Should().Be(
            (Formats.Bytes(5), Formats.Bytes(12), "+" + Formats.Bytes(7)));
    }

    [Fact]
    public async Task Without_a_comparison_only_the_size_is_shown()
    {
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedVersion = vm.VersionRows.Single(r => r.Name == Older);

        var row = await WaitForAsync(() => vm.Tree.Rows.FirstOrDefault(r => !r.IsMessage && r.Name == "gone.txt"));

        (row.SizeText, row.CompareSizeText, row.SizeChangeText).Should().Be((Formats.Bytes(2), "", ""));
    }

    private VersionsViewModel Create()
    {
        var worker = new VersionIndexWorker(new VersionIndexSet(Path.Combine(_fixture.Root, "index")));
        var context = new VersionsContext(worker, _fixture.Dialogs, _ => { }, new StorageFactory(), MachineName: "THIS-PC",
            TempOpener: new RecordingTempOpener());
        return new VersionsViewModel(() => _plan, () => false, new FakeFolderOpener(), context);
    }

    private async Task<VersionsViewModel> CompareAsync(string shown, string other)
    {
        var vm = Create();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedVersion = vm.VersionRows.Single(r => r.Name == shown);
        vm.SelectedCompare = vm.CompareChoices.Single(c => c.Row?.Name == other);
        return vm;
    }

    /// <summary>The compared row of <paramref name="name"/> (the tree loads in the background).</summary>
    private static Task<VersionTreeNode> RowAsync(VersionsViewModel vm, string name) =>
        WaitForAsync(() => vm.Tree.Rows.FirstOrDefault(r => !r.IsMessage && r.Name == name && r.Status is not null));

    private static void Write(string target, string version, string name, string content)
    {
        Directory.CreateDirectory(Path.Combine(target, version));
        File.WriteAllText(Path.Combine(target, version, name), content);
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
}
