using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Plans;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>The Ignore tab frees its scan once the tab has not been shown for a while; the next scan starts over.</summary>
public class IgnorePreviewReleaseTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);
    private readonly MainViewModelFixture _fixture = new();   // the labels the view model's texts need
    private readonly InMemoryStorage _source = new(StorageCapabilities.None);

    public IgnorePreviewReleaseTests()
    {
        _source.AddFile("a.txt", new byte[5]);
        _source.AddFile("docs/b.txt", new byte[7]);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task A_scan_not_shown_for_the_delay_is_freed()
    {
        var vm = await ScannedAsync();

        vm.SetShown(false);
        await WaitUntilAsync(() => vm.Root is null);

        vm.Tree.Rows.Should().BeEmpty();
        vm.ProgressText.Should().Be(Loc.T("ignore.progress.released"));
        vm.LastEvaluatedIncludedSize.Should().Be(12, "the Retention tab still falls back on the last size");

        await vm.IndexCommand.ExecuteAsync(null);
        vm.Root!.IncludedFiles.Should().Be(2, "the next scan starts over");
    }

    [Fact]
    public async Task Shown_again_before_the_delay_keeps_the_scan()
    {
        var vm = await ScannedAsync();

        vm.SetShown(false);
        vm.SetShown(true);
        await Task.Delay(Short * 4);

        vm.Root.Should().NotBeNull();
        vm.Tree.Rows.Should().NotBeEmpty();
    }

    [Fact]
    public void Release_without_a_scan_changes_nothing()
    {
        var vm = Create();

        vm.Release();

        vm.ProgressText.Should().Be(Loc.T("ignore.progress.notIndexed"));
    }

    private IgnorePreviewViewModel Create()
    {
        var location = new StorageLocation("mem", "", "conn-1");
        return new IgnorePreviewViewModel(() => location, () => new IgnoreSettings(), () => [], new Factory(_ => _source))
        {
            ReleaseDelay = Short,
        };
    }

    private async Task<IgnorePreviewViewModel> ScannedAsync()
    {
        var vm = Create();
        vm.SetShown(true);
        await vm.IndexCommand.ExecuteAsync(null);
        vm.Root.Should().NotBeNull();
        return vm;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(20);
        }
    }

    private sealed class Factory(Func<StorageLocation, IStorage> open) : IStorageFactory
    {
        public IStorage Open(StorageLocation location) => open(location);
    }
}
