using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Localization;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Localization;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Backup;

public class VersionRemoverTests : IDisposable
{
    private const string Name = "2026_09_01-02_00 Projects";
    private static readonly MarkerInfo Marker = new(1, "p1", "Projects", new DateTime(2026, 9, 30, 14, 5, 0, DateTimeKind.Utc), "HOST");
    private readonly TempDir _tmp = new();
    private readonly string _target;
    private readonly string _version;

    public VersionRemoverTests()
    {
        _target = _tmp.CreateDir("target");
        _version = VersionFolder.Create(_target, Name, "p1");
        _tmp.WriteFile($@"target\{Name}\sub\deep\c.txt", "x");
    }

    public void Dispose() => _tmp.Dispose();

    private IStorage Target => new FileSystemStorage(_target);

    private static async Task<InMemoryStorage> MemoryVersion(int extraFiles = 0, StorageCapabilities? capabilities = null)
    {
        var storage = capabilities is { } caps ? new InMemoryStorage(caps) : new InMemoryStorage();
        await VersionFolder.CreateAsync(storage, Name, "p1");
        for (var i = 0; i < extraFiles; i++)
            storage.AddFile($"{Name}/sub/f{i:0000}.txt", [1]);
        return storage;
    }

    [Fact]
    public async Task Removes_the_folder_with_everything_in_it()
    {
        await VersionRemover.RemoveAsync(Target, Name, Marker);

        Directory.GetFileSystemEntries(_target).Should().BeEmpty();
    }

    [Fact]
    public async Task Reports_the_deleted_files_but_not_the_manifest_or_the_marker()
    {
        _tmp.WriteFile($@"target\{Name}\sub\b.txt", "b");
        var reported = new List<int>();

        await VersionRemover.RemoveAsync(Target, Name, Marker, reported.Add);

        // data.bin, sub/b.txt and sub/deep/c.txt, in one batch
        reported.Should().Equal(3);
        Directory.GetFileSystemEntries(_target).Should().BeEmpty();
    }

    [Fact]
    public async Task Deletes_in_batches_of_1000_and_reports_each_batch()
    {
        var storage = await MemoryVersion(extraFiles: 2499);   // + data.bin
        var batches = new List<int>();

        await VersionRemover.RemoveAsync(storage, Name, Marker, batches.Add);

        batches.Should().Equal(1000, 1000, 500);
        storage.Files.Should().BeEmpty();
        (await storage.StatAsync(Name, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Remove_writes_deleting_marker_then_hides_version_before_deleting_files()
    {
        var inner = await MemoryVersion(extraFiles: 2);
        var calls = new List<(string Operation, string Path)>();
        var storage = new FaultyStorage(inner) { Before = (operation, path) => calls.Add((operation, path)) };

        await VersionRemover.RemoveAsync(storage, Name, Marker);

        var writes = calls.Where(c => c.Operation is "create" or "commit" or "delete").ToList();
        writes.Take(3).Should().Equal(
            ("create", $"{Name}/re-deleting.json"),
            ("commit", $"{Name}/re-deleting.json"),
            ("delete", $"{Name}/re-manifest.json"));
        writes.Skip(3).Take(3).Select(c => c.Path).Should().BeEquivalentTo(
            $"{Name}/data.bin", $"{Name}/sub/f0000.txt", $"{Name}/sub/f0001.txt");
        writes.Skip(6).Should().Equal(
            ("delete", $"{Name}/sub"),
            ("delete", $"{Name}/re-deleting.json"),
            ("delete", Name));
        inner.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_marker_write_leaves_the_version_untouched()
    {
        var storage = new FaultyStorage(Target) { FailCreate = path => path.EndsWith("re-deleting.json", StringComparison.Ordinal) };

        var act = () => VersionRemover.RemoveAsync(storage, Name, Marker);

        (await act.Should().ThrowAsync<StorageException>()).Which.Should().NotBeAssignableTo<VersionRemainsException>();
        File.Exists(Path.Combine(_version, "data.bin")).Should().BeTrue();
        File.Exists(Path.Combine(_version, "re-manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(_version, "sub", "deep", "c.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_version, "re-deleting.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Failure_after_marker_leaves_remains_and_throws_VersionRemainsException()
    {
        var inner = await MemoryVersion(extraFiles: 1);
        var storage = new FaultyStorage(inner) { FailDelete = path => path == $"{Name}/sub/f0000.txt" };

        var act = () => VersionRemover.RemoveAsync(storage, Name, Marker);

        var thrown = (await act.Should().ThrowExactlyAsync<VersionRemainsException>()).Which;
        thrown.RemainsPath.Should().Be(Name);
        thrown.InnerException.Should().BeOfType<StorageLockedException>();
        inner.Files.Should().Contain($"{Name}/re-deleting.json").And.NotContain($"{Name}/re-manifest.json");
        (await VersionCatalog.ListAsync(inner, "p1", "Projects")).Should().BeEmpty("it must no longer look like a version");

        await VersionRemover.FinishRemovalAsync(inner, Name);
        inner.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task A_removal_that_fails_half_way_keeps_its_deleting_marker_for_the_next_run()
    {
        var storage = new FaultyStorage(Target) { FailDelete = path => path == $"{Name}/re-deleting.json" };

        var act = () => VersionRemover.RemoveAsync(storage, Name, Marker);

        var thrown = (await act.Should().ThrowExactlyAsync<VersionRemainsException>()).Which;
        thrown.RemainsPath.Should().Be(Name);
        thrown.InnerException.Should().BeOfType<StorageLockedException>();
        File.Exists(Path.Combine(_version, "re-manifest.json")).Should().BeFalse("the manifest goes first");
        File.Exists(Path.Combine(_version, "data.bin")).Should().BeFalse();
        Directory.Exists(Path.Combine(_version, "sub")).Should().BeFalse();
        File.Exists(Path.Combine(_version, "re-deleting.json")).Should().BeTrue("the marker goes last");
        (await VersionCatalog.ListAsync(Target, "p1", "Projects")).Should().BeEmpty();

        await VersionRemover.FinishRemovalAsync(Target, Name);
        Directory.GetFileSystemEntries(_target).Should().BeEmpty();
    }

    [Fact]
    public async Task The_deleting_marker_names_the_plan_the_folder_was_made_under()
    {
        // Made as "Old name"; the plan has been renamed to "Projects" since (Marker carries the current name).
        const string older = "2026_09_02-02_00 Old name";
        var storage = new InMemoryStorage();
        await VersionFolder.CreateAsync(storage, older, "p1");
        var faulty = new FaultyStorage(storage) { FailDelete = path => path == older + "/data.bin" };

        var act = () => VersionRemover.RemoveAsync(faulty, older, Marker);

        await act.Should().ThrowAsync<VersionRemainsException>();
        (await VersionMarkers.TryReadAsync(storage, $"{older}/re-deleting.json", CancellationToken.None))
            .Should().Be(Marker with { PlanName = "Old name" });
    }

    [Fact]
    public async Task Cancellation_stops_between_batches_and_the_deleting_marker_stays()
    {
        var storage = await MemoryVersion(extraFiles: 1500);
        using var cts = new CancellationTokenSource();

        var act = () => VersionRemover.RemoveAsync(storage, Name, Marker, _ => cts.Cancel(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        storage.Files.Should().HaveCount(502, "the second batch of 501 files and the marker are left")
            .And.Contain($"{Name}/re-deleting.json");
        (await VersionMarkers.TryReadAsync(storage, $"{Name}/re-deleting.json", CancellationToken.None)).Should().Be(Marker);
    }

    [Fact]
    public async Task Cancellation_during_a_batch_finishes_the_batch_and_reports_it()
    {
        var storage = await MemoryVersion(extraFiles: 1500);
        using var cts = new CancellationTokenSource();
        var faulty = new FaultyStorage(storage)
        {
            Before = (operation, path) =>
            {
                if (operation == "delete" && path.EndsWith(".txt", StringComparison.Ordinal))
                    cts.Cancel();   // right at the first file of the first batch
            },
        };
        var reported = new List<int>();

        var act = () => VersionRemover.RemoveAsync(faulty, Name, Marker, reported.Add, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        reported.Should().Equal(1000);
        storage.Files.Should().HaveCount(502, "exactly the reported files are gone; 501 files and the marker are left");
    }

    [Fact]
    public async Task A_manifest_spelled_in_another_case_still_goes_last()
    {
        var storage = new InMemoryStorage();   // not CaseSensitive, yet its paths are compared ordinally
        const string legacy = Name + ".deleting";
        storage.AddFile($"{legacy}/RE-MANIFEST.json", "{}"u8.ToArray());
        storage.AddFile($"{legacy}/a.txt", [1]);
        storage.AddFile($"{legacy}/sub/b.txt", [1]);
        var faulty = new FaultyStorage(storage) { FailDelete = path => path == $"{legacy}/sub/b.txt" };

        var act = () => VersionRemover.RemoveLegacyFolderAsync(faulty, legacy);

        await act.Should().ThrowAsync<StorageLockedException>();
        storage.Files.Should().Contain($"{legacy}/RE-MANIFEST.json");

        var reported = new List<int>();
        await VersionRemover.RemoveLegacyFolderAsync(storage, legacy, reported.Add);
        reported.Sum().Should().BeInRange(1, 2, "the manifest is not counted");
        storage.Files.Should().BeEmpty();
        (await storage.StatAsync(legacy, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_manifest_spelled_in_another_case_is_hidden_first()
    {
        var storage = new InMemoryStorage();
        storage.AddFile($"{Name}/a.txt", [1]);
        storage.AddFile($"{Name}/RE-MANIFEST.json", "{}"u8.ToArray());
        var deletes = new List<string>();
        var faulty = new FaultyStorage(storage) { Before = (operation, path) => { if (operation == "delete") deletes.Add(path); } };

        await VersionRemover.RemoveAsync(faulty, Name, Marker);

        deletes.Take(2).Should().Equal($"{Name}/re-manifest.json", $"{Name}/RE-MANIFEST.json");
        storage.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task Works_on_a_storage_without_directories()
    {
        var storage = await MemoryVersion(extraFiles: 3, capabilities: StorageCapabilities.None);

        await VersionRemover.RemoveAsync(storage, Name, Marker);

        storage.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task A_version_that_is_gone_is_a_plain_failure_and_not_a_removal()
    {
        Directory.Delete(_version, recursive: true);

        var act = () => VersionRemover.RemoveAsync(Target, Name, Marker);

        (await act.Should().ThrowAsync<StorageNotFoundException>()).Which.Should().NotBeAssignableTo<VersionRemainsException>();
        Directory.GetFileSystemEntries(_target).Should().BeEmpty("no marker brings the folder back");
    }

    [Fact]
    public async Task A_file_in_place_of_the_folder_is_refused_with_a_translatable_reason()
    {
        const string file = "2026_09_03-02_00 Projects";
        _tmp.WriteFile($@"target\{file}", "a file, not a folder");

        var act = () => VersionRemover.RemoveAsync(Target, file, Marker);

        var thrown = await act.Should().ThrowAsync<StorageIOException>();
        CoreTexts.Recognize(thrown.Which.Message).Should().Be(Message.Of("core.file.notAFolder", ("name", file)));
        File.Exists(Path.Combine(_target, file)).Should().BeTrue();
    }

    [Fact]
    public async Task The_root_is_never_removed()
    {
        var act = () => VersionRemover.RemoveAsync(Target, "", Marker);

        // The same refusal the storages give for deleting their root.
        await act.Should().ThrowAsync<StorageConflictException>();
        File.Exists(Path.Combine(_version, "data.bin")).Should().BeTrue();
    }

    [Theory]
    [InlineData("link")]             // directly in the version folder
    [InlineData(@"sub\deep\link")]   // further down
    public async Task A_junction_inside_the_version_is_removed_as_a_link_and_its_target_is_untouched(string relativeLink)
    {
        var outside = _tmp.CreateDir("outside");
        var foreign = _tmp.WriteFile(@"outside\nested\keep.txt", "not ours");
        var link = Path.Combine(_version, relativeLink);
        Junction.Create(link, outside);

        try
        {
            await VersionRemover.RemoveAsync(Target, Name, Marker);

            Directory.GetFileSystemEntries(_target).Should().BeEmpty();
            File.ReadAllText(foreign).Should().Be("not ours");
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);   // removes the junction only
        }
    }

    [Fact]
    public async Task A_link_is_refused_and_its_target_is_untouched()
    {
        var real = VersionFolder.Create(_tmp.CreateDir("elsewhere"), Name, "p1");
        const string linkName = "2026_09_02-02_00 Projects";
        var link = Path.Combine(_target, linkName);
        Junction.Create(link, real);

        try
        {
            var act = () => VersionRemover.RemoveAsync(Target, linkName, Marker);

            (await act.Should().ThrowAsync<StorageException>()).Which.Should().NotBeAssignableTo<VersionRemainsException>();
            File.Exists(Path.Combine(real, "data.bin")).Should().BeTrue();
            File.Exists(Path.Combine(real, "re-manifest.json")).Should().BeTrue();
            File.Exists(Path.Combine(real, "re-deleting.json")).Should().BeFalse("nothing is written through a link");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task Finishing_a_removal_also_refuses_a_link()
    {
        var real = VersionFolder.Create(_tmp.CreateDir("elsewhere"), Name, "p1");
        const string linkName = "2026_09_02-02_00 Projects";
        var link = Path.Combine(_target, linkName);
        Junction.Create(link, real);

        try
        {
            var act = () => VersionRemover.FinishRemovalAsync(Target, linkName);

            await act.Should().ThrowAsync<StorageException>();
            File.Exists(Path.Combine(real, "data.bin")).Should().BeTrue();
            File.Exists(Path.Combine(real, "re-manifest.json")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task A_legacy_folder_keeps_its_manifest_until_everything_else_is_gone()
    {
        var legacy = Name + ".deleting";
        Directory.Move(_version, Path.Combine(_target, legacy));
        var storage = new FaultyStorage(Target) { FailDelete = path => path == $"{legacy}/sub/deep/c.txt" };

        var act = () => VersionRemover.RemoveLegacyFolderAsync(storage, legacy);

        await act.Should().ThrowAsync<StorageLockedException>();
        File.Exists(Path.Combine(_target, legacy, "re-manifest.json")).Should().BeTrue("the manifest goes last");

        var reported = new List<int>();
        await VersionRemover.RemoveLegacyFolderAsync(Target, legacy, reported.Add);
        reported.Sum().Should().BeInRange(1, 2, "c.txt (and maybe data.bin) was left; the manifest is not counted");
        Directory.GetFileSystemEntries(_target).Should().BeEmpty();
    }

    [Fact]
    public async Task A_junction_inside_a_legacy_folder_is_removed_as_a_link()
    {
        var legacy = Name + ".partial";
        Directory.Move(_version, Path.Combine(_target, legacy));
        var outside = _tmp.CreateDir("outside");
        var foreign = _tmp.WriteFile(@"outside\keep.txt", "not ours");
        var link = Path.Combine(_target, legacy, "sub", "link");
        Junction.Create(link, outside);

        try
        {
            await VersionRemover.RemoveLegacyFolderAsync(Target, legacy);

            Directory.GetFileSystemEntries(_target).Should().BeEmpty();
            File.ReadAllText(foreign).Should().Be("not ours");
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
        }
    }
}
