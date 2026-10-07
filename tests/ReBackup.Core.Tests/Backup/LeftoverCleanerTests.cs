using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Backup;

public class LeftoverCleanerTests : IDisposable
{
    private const string PlanId = "p1";
    private const string PlanName = "Projects";
    private const string Kept = "2026_09_01-02_00 Projects";
    private const string Folder = "2026_09_02-02_00 Projects";
    private static readonly DateTime Started = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    private readonly InMemoryStorage _storage = new();
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static MarkerInfo Marker(string planId = PlanId) => new(1, planId, PlanName, Started, "HOST");

    private Task<IReadOnlyList<string>> Clean(IStorage? storage = null, string? current = null, CancellationToken ct = default,
        Action<string, int>? onFileDeleted = null) =>
        LeftoverCleaner.CleanAsync(storage ?? _storage, PlanId, PlanName, current, ct, onFileDeleted);

    private async Task<string> Unfinished(string name = Folder, string planId = PlanId, int files = 2, bool withManifest = false)
    {
        for (var i = 0; i < files; i++)
            _storage.AddFile($"{name}/sub/f{i}.txt", [1]);
        if (withManifest)
            _storage.AddFile($"{name}/re-manifest.json", "{}"u8.ToArray());
        await VersionMarkers.WritePendingAsync(_storage, name, Marker(planId), CancellationToken.None);
        return name;
    }

    private async Task<string> BeingDeleted(string name = Folder, string planId = PlanId, bool withManifest = false)
    {
        if (withManifest)
            await VersionFolder.CreateAsync(_storage, name, planId);
        else
            _storage.AddFile($"{name}/sub/left.txt", [1]);
        await VersionMarkers.WriteDeletingAsync(_storage, name, Marker(planId), CancellationToken.None);
        return name;
    }

    private IEnumerable<string> FilesIn(string folder) => _storage.Files.Where(f => f.StartsWith(folder + "/", StringComparison.Ordinal));

    [Fact]
    public async Task Cleanup_removes_pending_folder_of_this_plan()
    {
        await VersionFolder.CreateAsync(_storage, Kept, PlanId);
        await Unfinished(withManifest: true);   // crashed after the manifest, before the marker was deleted
        var deletes = new List<string>();
        var storage = new FaultyStorage(_storage) { Before = (operation, path) => { if (operation == "delete") deletes.Add(path); } };

        var warnings = await Clean(storage);

        warnings.Should().BeEmpty();
        FilesIn(Folder).Should().BeEmpty();
        (await _storage.StatAsync(Folder, CancellationToken.None)).Should().BeNull();
        FilesIn(Kept).Should().HaveCount(2, "a finished version stays");
        deletes.First().Should().Be($"{Folder}/re-manifest.json");
        deletes.TakeLast(2).Should().Equal($"{Folder}/re-pending.json", Folder);
    }

    [Fact]
    public async Task Cleanup_keeps_pending_folder_of_current_run()
    {
        await Unfinished();

        var warnings = await Clean(current: Folder);

        warnings.Should().BeEmpty();
        FilesIn(Folder).Should().HaveCount(3);
    }

    [Fact]
    public async Task Cleanup_finishes_deleting_folder_of_this_plan()
    {
        await BeingDeleted();
        await BeingDeleted("2026_09_03-02_00 Projects", withManifest: true);   // crashed right after the marker was written
        await VersionFolder.CreateAsync(_storage, Kept, PlanId);

        var warnings = await Clean();

        warnings.Should().BeEmpty();
        _storage.Files.Should().OnlyContain(f => f.StartsWith(Kept + "/", StringComparison.Ordinal));
        (await VersionCatalog.ListAsync(_storage, PlanId, PlanName)).Select(v => v.Name).Should().Equal(Kept);
    }

    [Fact]
    public async Task A_deleting_folder_of_the_current_run_name_is_finished_too()
    {
        await BeingDeleted();

        (await Clean(current: Folder)).Should().BeEmpty();
        FilesIn(Folder).Should().BeEmpty();
    }

    [Fact]
    public async Task Cleanup_never_touches_foreign_or_unreadable_markers_and_warns()
    {
        const string foreignPending = "2026_09_03-02_00 Projects";
        const string foreignDeleting = "2026_09_04-02_00 Projects";
        const string unreadablePending = "2026_09_05-02_00 Projects";
        const string unreadableDeleting = "2026_09_06-02_00 Projects";
        await Unfinished(foreignPending, planId: "p2");
        await BeingDeleted(foreignDeleting, planId: "p2", withManifest: true);
        _storage.AddFile($"{unreadablePending}/a.txt", [1]);
        _storage.AddFile($"{unreadablePending}/re-pending.json", "not json"u8.ToArray());
        _storage.AddFile($"{unreadableDeleting}/a.txt", [1]);
        _storage.AddFile($"{unreadableDeleting}/re-deleting.json", "{"u8.ToArray());
        var before = _storage.Files.ToList();

        var warnings = await Clean();

        _storage.Files.Should().BeEquivalentTo(before);
        warnings.Should().BeEquivalentTo(
            $"\"{foreignPending}\" was left alone: it is marked by another plan.",
            $"\"{foreignDeleting}\" was left alone: it is marked by another plan.",
            $"\"{unreadablePending}\" was left alone: its marker cannot be read.",
            $"\"{unreadableDeleting}\" was left alone: its marker cannot be read.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_marked_folder_renamed_by_hand_is_left_alone_with_a_warning(bool deleting)
    {
        const string rescued = "2026_09_02-02_00 Projects rescued";
        if (deleting)
            await BeingDeleted(rescued);
        else
            await Unfinished(rescued);
        var before = _storage.Files.ToList();

        var warnings = await Clean();

        _storage.Files.Should().BeEquivalentTo(before);
        warnings.Should().Equal($"\"{rescued}\" was left alone: it is marked by this plan but was renamed.");
    }

    [Fact]
    public async Task A_marked_folder_named_after_the_plan_name_in_its_marker_is_cleaned()
    {
        // Written before the plan was renamed from "Old name" to "Projects".
        const string older = "2026_09_02-02_00 Old name";
        _storage.AddFile($"{older}/a.txt", [1]);
        await VersionMarkers.WriteDeletingAsync(_storage, older, Marker() with { PlanName = "Old name" }, CancellationToken.None);

        (await Clean()).Should().BeEmpty();
        FilesIn(older).Should().BeEmpty();
    }

    [Fact]
    public async Task Folders_not_named_like_a_version_are_not_looked_into()
    {
        _storage.AddFile("notes/a.txt", [1]);
        await VersionMarkers.WritePendingAsync(_storage, "notes", Marker(), CancellationToken.None);
        var calls = new List<(string Operation, string Path)>();
        var storage = new FaultyStorage(_storage) { Before = (operation, path) => calls.Add((operation, path)) };

        (await Clean(storage)).Should().BeEmpty();

        calls.Should().Equal(("list", ""));
        FilesIn("notes").Should().HaveCount(2);
    }

    [Fact]
    public async Task The_empty_folder_a_deletion_could_not_remove_goes_at_the_next_run()
    {
        await VersionFolder.CreateAsync(_storage, Folder, PlanId);
        var faulty = new FaultyStorage(_storage) { FailDelete = path => path == Folder };

        var result = (await VersionDeleter.DeleteAsync(faulty, PlanId, PlanName, [Folder])).Single();

        result.Outcome.Should().Be(VersionDeletionOutcome.RemainsLeft);
        (await _storage.StatAsync(Folder, CancellationToken.None)).Should().NotBeNull("the empty folder is left");
        FilesIn(Folder).Should().BeEmpty("no marker is left either");

        (await Clean()).Should().BeEmpty();
        (await _storage.StatAsync(Folder, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task The_empty_folder_of_the_current_run_is_never_removed_as_remains()
    {
        await _storage.EnsureDirectoryAsync(Folder, CancellationToken.None);   // reserved, its marker not visible yet

        (await Clean(current: Folder.ToUpperInvariant())).Should().BeEmpty();

        (await _storage.StatAsync(Folder, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task An_unmarked_folder_without_a_manifest_that_holds_anything_is_left_alone()
    {
        _storage.AddFile($"{Folder}/a.txt", [1]);
        await _storage.EnsureDirectoryAsync("2026_09_03-02_00 Other", CancellationToken.None);   // empty, another plan's name

        (await Clean()).Should().BeEmpty();

        FilesIn(Folder).Should().Equal($"{Folder}/a.txt");
        (await _storage.StatAsync("2026_09_03-02_00 Other", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_deletion_that_cannot_be_finished_is_a_warning_and_is_retried_next_time()
    {
        await BeingDeleted();
        var faulty = new FaultyStorage(_storage) { FailDelete = path => path == $"{Folder}/sub/left.txt" };

        var warnings = await Clean(faulty);

        warnings.Should().ContainSingle().Which.Should().StartWith($"Remains of an earlier removal could not be deleted (\"{Folder}\"): ");
        FilesIn(Folder).Should().Contain($"{Folder}/re-deleting.json");

        (await Clean()).Should().BeEmpty();
        FilesIn(Folder).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unfinished_folder_that_cannot_be_removed_is_a_warning_and_keeps_its_marker()
    {
        await Unfinished();
        var faulty = new FaultyStorage(_storage) { FailDelete = path => path == $"{Folder}/sub/f1.txt" };

        var warnings = await Clean(faulty);

        warnings.Should().ContainSingle().Which.Should().StartWith($"An unfinished backup could not be deleted (\"{Folder}\"): ");
        FilesIn(Folder).Should().Contain($"{Folder}/re-pending.json");

        (await Clean()).Should().BeEmpty();
        FilesIn(Folder).Should().BeEmpty();
    }

    [Fact]
    public async Task Reports_the_files_it_removes_per_folder()
    {
        await Unfinished(files: 3);
        var reports = new List<(string Name, int Count)>();

        await Clean(onFileDeleted: (name, count) => reports.Add((name, count)));

        reports.Should().Equal((Folder, 0), (Folder, 3));
    }

    [Fact]
    public async Task Cancellation_propagates_and_leaves_the_marker()
    {
        await Unfinished();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => Clean(ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        FilesIn(Folder).Should().Contain($"{Folder}/re-pending.json");
    }

    [Fact]
    public async Task A_missing_root_has_no_leftovers()
    {
        (await Clean(new FileSystemStorage(_tmp.PathOf("not-created-yet")))).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unavailable_root_throws_unavailable()
    {
        var act = () => Clean(new WrappedStorage(_storage) { Unavailable = true });

        await act.Should().ThrowAsync<StorageUnavailableException>();
    }

    [Fact]
    public async Task A_link_with_a_marker_of_the_plan_is_left_alone()
    {
        var target = _tmp.CreateDir("target");
        var real = _tmp.CreateDir("elsewhere");
        File.WriteAllText(Path.Combine(real, "keep.txt"), "not ours");
        await VersionMarkers.WritePendingAsync(new FileSystemStorage(real), "inner", Marker(), CancellationToken.None);
        var link = Path.Combine(target, Folder);
        Junction.Create(link, Path.Combine(real, "inner"));

        try
        {
            (await Clean(new FileSystemStorage(target))).Should().BeEmpty();
            File.Exists(Path.Combine(real, "inner", "re-pending.json")).Should().BeTrue();
            File.Exists(Path.Combine(real, "keep.txt")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task Cleanup_handles_legacy_partial_and_deleting_folders_as_before()
    {
        // Folders as ReBackup 1.0.5 left them behind.
        var target = _tmp.CreateDir("target");
        string Write(string relative, string content = "x")
        {
            var path = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        Write(@"2026_09_20-10_00 Projects.partial\x.txt", "stale");                       // ours, unfinished: removed
        Write(@"2026_09_20-10_00 Projects.partial\sub\y.txt", "stale");
        var finished = VersionFolder.Create(target, "2026_09_21-10_00 Projects.partial", PlanId);   // has a manifest: stays
        Write(@"2026_09_22-10_00 Other.partial\x.txt", "foreign");                         // another plan's name: stays
        Write(@"notes\x.txt", "foreign");                                                  // not a version: stays
        VersionFolder.Create(target, "2026_09_23-10_00 Projects", PlanId);                // a version: stays
        VersionFolder.Create(target, "2026_09_01-02_00 Projects.deleting", PlanId);       // own remains: removed
        VersionFolder.Create(target, "2026_09_02-02_00 Projects.deleting", "other");      // another plan's: stays
        Write(@"2026_09_03-02_00 Projects.deleting\a.txt", "remains without a manifest"); // not provably ours: stays
        Write(@"2026_09_04-02_00 Other.deleting\a.txt", "remains of another plan");       // stays
        Directory.CreateDirectory(Path.Combine(target, "2026_09_05-02_00 Projects.deleting"));   // empty, our name: removed
        Write(@"2026_09_06-02_00 Projects.deleting\re-manifest.json", "not json");       // unreadable: stays
        VersionFolder.Create(target, "2026_09_07-02_00 Projects - Copy.deleting", PlanId, manifestPlanName: PlanName);   // copied by hand: stays
        var real = VersionFolder.Create(_tmp.CreateDir("elsewhere"), "2026_09_08-02_00 Projects", PlanId);
        var link = Path.Combine(target, "2026_09_08-02_00 Projects.deleting");
        Junction.Create(link, real);                                                        // a link: never followed

        try
        {
            var removed = 0;
            var warnings = await Clean(new FileSystemStorage(target), onFileDeleted: (_, count) => removed += count);

            Directory.GetFileSystemEntries(target).Select(Path.GetFileName).Should().BeEquivalentTo(
                "2026_09_21-10_00 Projects.partial",
                "2026_09_22-10_00 Other.partial",
                "notes",
                "2026_09_23-10_00 Projects",
                "2026_09_02-02_00 Projects.deleting",
                "2026_09_03-02_00 Projects.deleting",
                "2026_09_04-02_00 Other.deleting",
                "2026_09_06-02_00 Projects.deleting",
                "2026_09_07-02_00 Projects - Copy.deleting",
                "2026_09_08-02_00 Projects.deleting");
            warnings.Should().Equal("\"2026_09_21-10_00 Projects.partial\" was left alone: it looks unfinished but holds a manifest.");
            removed.Should().Be(3, "x.txt and y.txt of the partial folder, data.bin of the remains");
            File.Exists(Path.Combine(finished, "data.bin")).Should().BeTrue();
            File.Exists(Path.Combine(finished, "re-manifest.json")).Should().BeTrue();
            File.Exists(Path.Combine(real, "data.bin")).Should().BeTrue();
            File.Exists(Path.Combine(real, "re-manifest.json")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}
