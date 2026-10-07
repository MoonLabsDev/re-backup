using System.Text;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;
using ReBackup.Storage.InMemory;
using static ReBackup.Core.Tests.TestSupport.VersionBuilder;

namespace ReBackup.Core.Tests.Versions;

public class RestorerTests : IDisposable
{
    private const string TempSuffix = ".rebackup-tmp";
    private static readonly DateTime Old = new(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    private readonly TempDir _tmp = new();
    private readonly string _version;
    private readonly string _versionPath;
    private readonly string _dest;
    private readonly FileSystemStorage _versions;
    private readonly FileSystemStorage _destination;

    public RestorerTests()
    {
        var target = _tmp.CreateDir("target");
        _version = Write(target, "2026_09_30-14_05",
            [File("a.txt", "alpha", Old), File("docs/b.txt", "bravo"), File("docs/sub/c.txt", "charlie")]);
        Directory.CreateDirectory(Path.Combine(_version, "docs", "empty"));
        _versionPath = Path.GetFileName(_version);
        _dest = _tmp.CreateDir("dest");
        _versions = new FileSystemStorage(target);
        _destination = new FileSystemStorage(_dest);
    }

    public void Dispose() => _tmp.Dispose();

    private string Dest(string relative) => Path.Combine(_dest, relative);

    private string[] DestFiles() =>
        Directory.GetFiles(_dest, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_dest, f)).Order().ToArray();

    private Task<RestorePlan> PlanAsync(IReadOnlyList<string> relativePaths, RestoreMode mode = RestoreMode.Original,
        IStorage? destination = null) =>
        Restorer.PlanAsync(_versions, _versionPath, relativePaths, destination ?? _destination, mode);

    private async Task<RestoreResult> RestoreAsync(IReadOnlyList<string> relativePaths, ConflictPolicy policy,
        RestoreMode mode = RestoreMode.Original, IStorage? destination = null) =>
        await Restorer.RunAsync(await PlanAsync(relativePaths, mode, destination), policy);

    /// <summary>A destination that puts a file in place right before the restore starts writing it.</summary>
    private FaultyStorage AppearsBeforeWriting(string path, string content) => new(_destination)
    {
        Before = (operation, at) =>
        {
            if (operation == "create" && at == path && !System.IO.File.Exists(Dest(path)))
                System.IO.File.WriteAllText(Dest(path), content);
        },
    };

    [Fact]
    public async Task Plans_a_file_and_a_folder_to_their_original_place()
    {
        var plan = await PlanAsync(["a.txt", "docs"]);

        plan.Files.Select(f => (f.Source, f.Destination, f.Size)).Should().BeEquivalentTo([
            ($"{_versionPath}/a.txt", "a.txt", 5L), ($"{_versionPath}/docs/b.txt", "docs/b.txt", 5L),
            ($"{_versionPath}/docs/sub/c.txt", "docs/sub/c.txt", 7L)]);
        plan.Directories.Should().BeEquivalentTo(["docs", "docs/sub", "docs/empty"]);
        plan.Conflicts.Should().BeEmpty();
        plan.TotalBytes.Should().Be(17);
        plan.VersionTime.Should().Be(new DateTime(2026, 9, 30, 14, 5, 0));
        plan.Versions.Should().BeSameAs(_versions);
        plan.VersionPath.Should().Be(_versionPath);
        plan.Destination.Should().BeSameAs(_destination);
    }

    [Fact]
    public async Task Restores_into_a_chosen_folder_under_the_items_name()
    {
        var result = await RestoreAsync(["docs/sub", "docs/b.txt"], ConflictPolicy.Overwrite, RestoreMode.ToFolder);

        result.Should().BeEquivalentTo(new RestoreResult(2, 0, 0, [], Canceled: false));
        DestFiles().Should().Equal("b.txt", @"sub\c.txt");
    }

    [Fact]
    public async Task Restores_a_whole_version_without_its_manifest()
    {
        (await RestoreAsync([""], ConflictPolicy.Overwrite)).Copied.Should().Be(3);

        DestFiles().Should().Equal("a.txt", @"docs\b.txt", @"docs\sub\c.txt");
        Directory.Exists(Dest(@"docs\empty")).Should().BeTrue("a folder restore keeps the structure, also empty folders");
    }

    [Fact]
    public async Task Keeps_the_versions_last_write_time()
    {
        await RestoreAsync(["a.txt"], ConflictPolicy.Overwrite);

        System.IO.File.GetLastWriteTimeUtc(Dest("a.txt")).Should().Be(Old);
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("alpha");
    }

    [Theory]
    [InlineData(ConflictPolicy.Overwrite, true)]
    [InlineData(ConflictPolicy.Skip, false)]
    [InlineData(ConflictPolicy.KeepBoth, false)]
    public async Task Writes_with_the_manifests_time_and_replaces_only_under_overwrite(ConflictPolicy policy, bool overwrite)
    {
        // The copy in the version has another time than the manifest: the manifest holds what the source file had.
        System.IO.File.SetLastWriteTimeUtc(Path.Combine(_version, "a.txt"), new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc));
        var destination = new FaultyStorage(_destination);

        await RestoreAsync(["a.txt"], policy, destination: destination);

        destination.Creates.Should().Equal(("a.txt", new CreateOptions(Overwrite: overwrite, ModifiedUtc: Old)));
        System.IO.File.GetLastWriteTimeUtc(Dest("a.txt")).Should().Be(Old);
    }

    [Fact]
    public async Task Overwrites_a_conflicting_file_and_leaves_no_temp_file()
    {
        _tmp.WriteFile(@"dest\a.txt", "changed since");
        var plan = await PlanAsync(["a.txt"]);
        plan.Conflicts.Select(c => c.Destination).Should().Equal("a.txt");

        var result = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite);

        result.Copied.Should().Be(1);
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("alpha");
        DestFiles().Should().Equal("a.txt");
    }

    [Fact]
    public async Task Skips_a_conflicting_file()
    {
        _tmp.WriteFile(@"dest\a.txt", "mine");

        var result = await RestoreAsync(["a.txt", "docs/b.txt"], ConflictPolicy.Skip);

        result.Should().BeEquivalentTo(new RestoreResult(1, 1, 0, [], Canceled: false));
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("mine");
        System.IO.File.ReadAllText(Dest(@"docs\b.txt")).Should().Be("bravo");
    }

    [Fact]
    public async Task A_file_that_appears_after_planning_is_skipped_and_never_overwritten()
    {
        var plan = await PlanAsync(["a.txt"], destination: AppearsBeforeWriting("a.txt", "came in between"));
        plan.Conflicts.Should().BeEmpty();

        var result = await Restorer.RunAsync(plan, ConflictPolicy.Skip);

        result.Should().BeEquivalentTo(new RestoreResult(0, 1, 0, [], Canceled: false));
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("came in between");
    }

    [Fact]
    public async Task A_file_that_appears_after_planning_is_kept_beside_under_keep_both()
    {
        var plan = await PlanAsync(["a.txt"], destination: AppearsBeforeWriting("a.txt", "came in between"));

        var result = await Restorer.RunAsync(plan, ConflictPolicy.KeepBoth);

        result.Should().BeEquivalentTo(new RestoreResult(0, 0, 1, [], Canceled: false));
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("came in between");
        System.IO.File.ReadAllText(Dest("a (2026_09_30-14_05).txt")).Should().Be("alpha");
    }

    [Fact]
    public async Task Keeps_both_with_the_versions_time_in_the_name_and_counts_up_on_collisions()
    {
        _tmp.WriteFile(@"dest\a.txt", "mine");
        _tmp.WriteFile(@"dest\a (2026_09_30-14_05).txt", "earlier restore");
        _tmp.WriteFile(@"dest\a (2026_09_30-14_05) 2.txt", "and another");

        var result = await RestoreAsync(["a.txt"], ConflictPolicy.KeepBoth);

        result.Should().BeEquivalentTo(new RestoreResult(0, 0, 1, [], Canceled: false));
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("mine");
        System.IO.File.ReadAllText(Dest("a (2026_09_30-14_05) 3.txt")).Should().Be("alpha");
    }

    [Fact]
    public void Keep_both_name_without_an_extension()
    {
        var time = new DateTime(2026, 9, 30, 14, 5, 0);

        Restorer.KeepBothName("x/Makefile", time, 1).Should().Be("x/Makefile (2026_09_30-14_05)");
        Restorer.KeepBothName("Makefile", time, 2).Should().Be("Makefile (2026_09_30-14_05) 2");
        Restorer.KeepBothName("x/y/a.tar.gz", time, 3).Should().Be("x/y/a.tar (2026_09_30-14_05) 3.gz");
    }

    [Fact]
    public async Task Never_deletes_files_the_version_does_not_have()
    {
        _tmp.WriteFile(@"dest\docs\extra.txt", "mine");
        _tmp.WriteFile(@"dest\docs\sub\extra2.txt", "mine too");

        await RestoreAsync(["docs"], ConflictPolicy.Overwrite);

        DestFiles().Should().Equal(@"docs\b.txt", @"docs\extra.txt", @"docs\sub\c.txt", @"docs\sub\extra2.txt");
    }

    [Fact]
    public async Task A_locked_or_read_only_file_fails_and_the_rest_continues()
    {
        _tmp.WriteFile(@"dest\docs\b.txt", "locked");
        var readOnly = _tmp.WriteFile(@"dest\docs\sub\c.txt", "read-only");
        System.IO.File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var plan = await PlanAsync(["a.txt", "docs"]);

        RestoreResult result;
        using (new FileStream(Dest(@"docs\b.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            result = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite);
        System.IO.File.SetAttributes(readOnly, FileAttributes.Normal);

        result.Copied.Should().Be(1);
        // Windows reports both as "access denied" when the file is replaced.
        result.Failures.Select(f => f.Path).Should().BeEquivalentTo(["docs/b.txt", "docs/sub/c.txt"]);
        result.Failures.Should().OnlyContain(f => f.Reason.Length > 0 && !f.InVersion);
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("alpha");
        System.IO.File.ReadAllText(Dest(@"docs\b.txt")).Should().Be("locked");
        System.IO.File.ReadAllText(readOnly).Should().Be("read-only");
        DestFiles().Should().NotContain(f => f.EndsWith(TempSuffix));
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData("docs/../../outside.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\Windows")]
    [InlineData("C:relative")]
    [InlineData("missing.txt")]
    [InlineData(VersionName.ManifestFileName)]
    public async Task Rejects_paths_outside_the_version_and_what_it_does_not_have(string relative)
    {
        var plan = () => PlanAsync([relative]);

        await plan.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Does_not_follow_links()
    {
        var outside = _tmp.CreateDir("outside");
        _tmp.WriteFile(@"outside\secret.txt", "secret");
        Junction.Create(Path.Combine(_version, "docs", "link"), outside);

        var plan = await PlanAsync(["docs"]);
        var linkItself = () => PlanAsync(["docs/link"]);
        var throughLink = () => PlanAsync(["docs/link/secret.txt"]);

        plan.Files.Should().NotContain(f => f.Source.Contains("secret"));
        plan.Directories.Should().NotContain(d => d.Contains("link"));
        await linkItself.Should().ThrowAsync<ArgumentException>();
        await throughLink.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task A_failed_copy_leaves_no_temp_file()
    {
        var source = Path.Combine(_version, "a.txt");
        var plan = await PlanAsync(["a.txt", "docs/b.txt"]);

        RestoreResult result;
        using (var holder = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            holder.Lock(0, holder.Length);   // opens fine, reading fails after the temp file exists
            result = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite);
        }

        result.Failures.Select(f => f.Path).Should().Equal("a.txt");
        result.Copied.Should().Be(1);
        Directory.GetFiles(_dest, "*" + TempSuffix, SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task An_existing_file_with_a_temp_like_name_is_never_touched()
    {
        var other = _tmp.WriteFile(@"dest\a.txt" + TempSuffix, "not ours");

        var result = await RestoreAsync(["a.txt"], ConflictPolicy.Overwrite);

        result.Copied.Should().Be(1);
        System.IO.File.ReadAllText(other).Should().Be("not ours");
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("alpha");
    }

    [Fact]
    public async Task A_leftover_temp_file_in_the_version_is_never_restored()
    {
        _tmp.WriteFile(Path.Combine("target", _versionPath, "docs", "b.txt.0badf00d" + TempSuffix), "half a copy");

        var plan = await PlanAsync(["docs"]);
        var result = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite);

        plan.Files.Should().NotContain(f => f.Source.EndsWith(TempSuffix));
        result.Copied.Should().Be(2);
        DestFiles().Should().Equal(@"docs\b.txt", @"docs\sub\c.txt");
    }

    [Fact]
    public async Task Does_not_write_through_a_link_at_the_destination()
    {
        var outside = _tmp.CreateDir("outside");
        _tmp.WriteFile(@"outside\b.txt", "behind the link");
        Junction.Create(Dest("docs"), outside);

        var plan = await PlanAsync(["a.txt", "docs"]);
        var result = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite);

        plan.Conflicts.Should().BeEmpty("nothing behind a link is looked at");
        Directory.GetFileSystemEntries(outside).Select(Path.GetFileName).Should().Equal("b.txt");
        System.IO.File.ReadAllText(Path.Combine(outside, "b.txt")).Should().Be("behind the link");
        result.Copied.Should().Be(1);
        result.Failures.Select(f => f.Path).Should().Contain(["docs/b.txt", "docs/sub/c.txt"]);
        result.Failures.Should().OnlyContain(f => f.Reason.Contains("link"));
    }

    [Fact]
    public async Task Does_not_write_through_a_link_in_the_middle_of_the_destination_path()
    {
        var outside = _tmp.CreateDir("outside");
        Directory.CreateDirectory(Dest("docs"));
        Junction.Create(Dest(@"docs\sub"), outside);

        var result = await RestoreAsync(["docs"], ConflictPolicy.Overwrite);

        Directory.GetFileSystemEntries(outside).Should().BeEmpty();
        result.Copied.Should().Be(1);   // docs/b.txt
        result.Failures.Select(f => f.Path).Should().Contain("docs/sub/c.txt");
        result.Failures.Should().OnlyContain(f => f.Reason.Contains("link"));
    }

    [Theory]
    [InlineData(ConflictPolicy.Overwrite, 0, 0, 1)]
    [InlineData(ConflictPolicy.Skip, 1, 0, 0)]
    [InlineData(ConflictPolicy.KeepBoth, 0, 1, 0)]
    public async Task Never_replaces_or_writes_into_a_link_where_a_file_goes(ConflictPolicy policy, int skipped, int keptBoth,
        int failed)
    {
        var outside = _tmp.CreateDir("outside");
        Junction.Create(Dest("a.txt"), outside);

        var result = await RestoreAsync(["a.txt"], policy);

        Directory.GetFileSystemEntries(outside).Should().BeEmpty();
        new DirectoryInfo(Dest("a.txt")).LinkTarget.Should().NotBeNull("the link itself stays");
        result.Copied.Should().Be(0);
        result.Skipped.Should().Be(skipped);
        result.KeptBoth.Should().Be(keptBoth);
        result.Failures.Should().HaveCount(failed);
    }

    [Fact]
    public async Task Refuses_a_destination_that_is_a_link()
    {
        var outside = _tmp.CreateDir("outside");
        var linkRoot = Dest("linkroot");
        Junction.Create(linkRoot, outside);

        var result = await RestoreAsync(["a.txt"], ConflictPolicy.Overwrite, destination: new FileSystemStorage(linkRoot));

        Directory.GetFileSystemEntries(outside).Should().BeEmpty();
        result.Failures.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_file_where_a_folder_exists_fails_under_overwrite()
    {
        Directory.CreateDirectory(Dest("a.txt"));

        var result = await RestoreAsync(["a.txt"], ConflictPolicy.Overwrite);

        result.Copied.Should().Be(0);
        result.Failures.Select(f => f.Path).Should().Equal("a.txt");
        Directory.Exists(Dest("a.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Rejects_two_items_with_the_same_name_in_a_folder_restore()
    {
        System.IO.File.WriteAllText(Path.Combine(_version, "docs", "sub", "B.TXT"), "other bravo");
        var plan = () => PlanAsync(["docs/b.txt", "docs/sub/B.TXT"], RestoreMode.ToFolder);

        await plan.Should().ThrowAsync<ArgumentException>().WithMessage("*B.TXT*");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task An_unreadable_subfolder_becomes_a_failure_of_the_plan()
    {
        var locked = Path.Combine(_version, "docs", "sub");
        var info = new DirectoryInfo(locked);
        var security = info.GetAccessControl();
        var deny = new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            var plan = await PlanAsync(["docs"]);
            var result = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite);

            plan.PlanFailures.Should().ContainSingle().Which.Should().Match<RestoreFailure>(
                f => f.Path == $"{_versionPath}/docs/sub" && f.InVersion);
            result.Failures.Select(f => f.Path).Should().Contain($"{_versionPath}/docs/sub");
            result.Copied.Should().Be(1);   // docs/b.txt
        }
        finally
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("docs")]
    public async Task Refuses_a_destination_inside_the_version(string below)
    {
        var plan = () => PlanAsync(["a.txt"], RestoreMode.ToFolder, new FileSystemStorage(Path.Combine(_version, below)));

        await plan.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_missing_version_folder_is_reported()
    {
        var plan = () => Restorer.PlanAsync(_versions, "2026_01_01-00_00 Projects", ["a.txt"], _destination, RestoreMode.Original);

        await plan.Should().ThrowAsync<StorageNotFoundException>();
    }

    [Fact]
    public async Task Cancellation_stops_before_the_next_file()
    {
        var plan = await PlanAsync(["a.txt", "docs"]);
        using var cts = new CancellationTokenSource();

        var result = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite, new SyncProgress<RestoreProgress>(p =>
        {
            if (p.FilesDone == 1)
                cts.Cancel();
        }), cts.Token);

        result.Canceled.Should().BeTrue();
        result.Copied.Should().Be(1);
        DestFiles().Should().HaveCount(1).And.NotContain(f => f.EndsWith(TempSuffix));
    }

    [Fact]
    public async Task Reports_progress_by_bytes()
    {
        var reports = new List<RestoreProgress>();

        await Restorer.RunAsync(await PlanAsync(["docs"]), ConflictPolicy.Overwrite, new SyncProgress<RestoreProgress>(reports.Add));

        reports[^1].Should().Be(new RestoreProgress(2, 2, 12, 12, ""));
        reports.Select(r => r.BytesDone).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Restores_empty_directories_from_manifest()
    {
        // A storage without real folders knows the empty ones only from the manifest.
        var versions = new InMemoryStorage(StorageCapabilities.SetModifiedTime);
        const string version = "2026_09_30-14_05 Projects";
        versions.AddFile($"{version}/docs/b.txt", Encoding.UTF8.GetBytes("bravo"));
        versions.AddFile($"{version}/{VersionName.ManifestFileName}", Encoding.UTF8.GetBytes(Serialize(new BackupManifest
        {
            PlanId = PlanId, PlanName = PlanName, CreatedUtc = Mtime, Source = Source,
            Files = [new ManifestFile("docs/b.txt", 5, Mtime, "")],
            Directories = ["docs", "docs/empty", "docs/empty/deeper", "other"],
        })));

        var plan = await Restorer.PlanAsync(versions, version, ["docs"], _destination, RestoreMode.Original);
        var result = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite);

        plan.Directories.Should().BeEquivalentTo(["docs", "docs/empty", "docs/empty/deeper"]);
        result.Should().BeEquivalentTo(new RestoreResult(1, 0, 0, [], Canceled: false));
        Directory.Exists(Dest(@"docs\empty\deeper")).Should().BeTrue();
        Directory.Exists(Dest("other")).Should().BeFalse("only what was selected is restored");
    }

    [Fact]
    public async Task Restore_from_in_memory_versions_to_file_system()
    {
        var versions = new InMemoryStorage();
        const string version = "2026_09_30-14_05 Projects";
        versions.AddFile($"{version}/a.txt", Encoding.UTF8.GetBytes("alpha"), DateTime.UtcNow);
        versions.AddFile($"{version}/docs/b.txt", Encoding.UTF8.GetBytes("bravo"), DateTime.UtcNow);
        versions.AddFile($"{version}/{VersionName.ManifestFileName}", Encoding.UTF8.GetBytes(Serialize(new BackupManifest
        {
            FormatVersion = 1, PlanId = PlanId, PlanName = PlanName, CreatedUtc = Mtime, Source = Source,
            Files = [new ManifestFile("a.txt", 5, Old, ""), new ManifestFile("docs/b.txt", 5, Mtime, "")],
        })));

        var plan = await Restorer.PlanAsync(versions, version, [""], _destination, RestoreMode.Original);
        var result = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite);

        result.Should().BeEquivalentTo(new RestoreResult(2, 0, 0, [], Canceled: false));
        DestFiles().Should().Equal("a.txt", @"docs\b.txt");
        System.IO.File.ReadAllText(Dest(@"docs\b.txt")).Should().Be("bravo");
        System.IO.File.GetLastWriteTimeUtc(Dest("a.txt")).Should().Be(Old, "the time comes from the manifest");
        System.IO.File.GetLastWriteTimeUtc(Dest(@"docs\b.txt")).Should().Be(Mtime);
    }

    [Fact]
    public async Task The_version_time_comes_from_the_manifest_when_the_folder_name_has_none()
    {
        var versions = new InMemoryStorage();
        var created = new DateTime(2026, 3, 4, 5, 6, 0, DateTimeKind.Utc);
        versions.AddFile("renamed/a.txt", Encoding.UTF8.GetBytes("alpha"));
        versions.AddFile($"renamed/{VersionName.ManifestFileName}", Encoding.UTF8.GetBytes(Serialize(new BackupManifest
        {
            PlanId = PlanId, PlanName = PlanName, CreatedUtc = created, Source = Source,
        })));

        var plan = await Restorer.PlanAsync(versions, "renamed", ["a.txt"], _destination, RestoreMode.Original);

        plan.VersionTime.Should().Be(created.ToLocalTime());
    }

    [Fact]
    public async Task A_name_the_destination_cannot_hold_fails_alone()
    {
        var versions = new InMemoryStorage();
        const string version = "2026_09_30-14_05 Projects";
        versions.AddFile($"{version}/docs/bad:name.txt", Encoding.UTF8.GetBytes("x"));
        versions.AddFile($"{version}/docs/good.txt", Encoding.UTF8.GetBytes("good"));

        var plan = await Restorer.PlanAsync(versions, version, ["docs"], _destination, RestoreMode.Original);
        var result = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite);

        result.Copied.Should().Be(1);
        result.Failures.Should().ContainSingle().Which.Path.Should().Be("docs/bad:name.txt");
        DestFiles().Should().Equal(@"docs\good.txt");
    }
}
