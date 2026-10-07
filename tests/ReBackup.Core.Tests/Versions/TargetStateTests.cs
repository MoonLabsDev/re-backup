using FluentAssertions;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Versions;

public class TargetStateTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task A_listable_root_is_present()
    {
        var storage = new InMemoryStorage();

        (await TargetStates.ProbeAsync(storage, CancellationToken.None)).Should().Be(TargetState.Present);
    }

    [Fact]
    public async Task A_root_that_does_not_exist_yet_is_not_created_yet()
    {
        var storage = new FaultyStorage(new InMemoryStorage())
        {
            Before = (operation, path) =>
            {
                if (operation == "list" && path.Length == 0) throw new StorageNotFoundException("");
            },
        };

        (await TargetStates.ProbeAsync(storage, CancellationToken.None)).Should().Be(TargetState.NotCreatedYet);
    }

    [Fact]
    public async Task An_unavailable_storage_is_unreachable()
    {
        var storage = new FaultyStorage(new InMemoryStorage())
        {
            Before = (operation, _) =>
            {
                if (operation == "list") throw new StorageUnavailableException("");
            },
        };

        (await TargetStates.ProbeAsync(storage, CancellationToken.None)).Should().Be(TargetState.Unreachable);
    }

    [Fact]
    public async Task Any_other_storage_failure_counts_as_unreachable()
    {
        var storage = new FaultyStorage(new InMemoryStorage())
        {
            Before = (operation, _) =>
            {
                if (operation == "list") throw new StorageAccessDeniedException("");
            },
        };

        (await TargetStates.ProbeAsync(storage, CancellationToken.None)).Should().Be(TargetState.Unreachable);
    }

    [Fact]
    public async Task A_cancelled_probe_throws_instead_of_guessing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => TargetStates.ProbeAsync(new InMemoryStorage(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_folder_on_disk_is_present_and_a_missing_one_on_an_existing_drive_is_not_created_yet()
    {
        var existing = _tmp.CreateDir("target");

        (await TargetStates.ProbeAsync(new FileSystemStorage(existing), CancellationToken.None)).Should().Be(TargetState.Present);
        (await TargetStates.ProbeAsync(new FileSystemStorage(_tmp.PathOf("later")), CancellationToken.None))
            .Should().Be(TargetState.NotCreatedYet);
    }

    [Fact]
    public async Task A_folder_on_a_drive_that_is_not_there_is_unreachable()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = Enumerable.Range('A', 26).Select(c => (char)c).FirstOrDefault(c => !used.Contains(c));
        if (free == default) return;

        var storage = new FileSystemStorage($@"{free}:\backup");

        (await TargetStates.ProbeAsync(storage, CancellationToken.None)).Should().Be(TargetState.Unreachable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\folder")]
    public async Task An_unset_or_invalid_location_is_unreachable_without_a_storage(string path)
    {
        var (state, storage) = await TargetStates.OpenAndProbeAsync(new StorageFactory(), StorageLocation.FileSystem(path), CancellationToken.None);

        state.Should().Be(TargetState.Unreachable);
        storage.Should().BeNull();
    }

    [Fact]
    public async Task Opening_a_present_folder_returns_its_storage()
    {
        var (state, storage) = await TargetStates.OpenAndProbeAsync(new StorageFactory(),
            StorageLocation.FileSystem(_tmp.CreateDir("target")), CancellationToken.None);

        state.Should().Be(TargetState.Present);
        storage.Should().NotBeNull();
    }
}
