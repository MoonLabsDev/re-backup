using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using static ReBackup.Core.Tests.TestSupport.VersionBuilder;

namespace ReBackup.Core.Tests.Versions;

public class RestorerTests : IDisposable
{
    private static readonly DateTime Old = new(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    private readonly TempDir _tmp = new();
    private readonly string _version;
    private readonly string _dest;

    public RestorerTests()
    {
        _version = Write(_tmp.CreateDir("target"), "2026_09_30-14_05",
            [File("a.txt", "alpha", Old), File("docs/b.txt", "bravo"), File("docs/sub/c.txt", "charlie")]);
        Directory.CreateDirectory(Path.Combine(_version, "docs", "empty"));
        _dest = _tmp.CreateDir("dest");
    }

    public void Dispose() => _tmp.Dispose();

    private string Dest(string relative) => Path.Combine(_dest, relative);

    private string[] DestFiles() =>
        Directory.GetFiles(_dest, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_dest, f)).Order().ToArray();

    [Fact]
    public void Plans_a_file_and_a_folder_to_their_original_place()
    {
        var plan = Restorer.Plan(_version, ["a.txt", "docs"], _dest, RestoreMode.Original);

        plan.Files.Select(f => (Path.GetRelativePath(_version, f.Source), Path.GetRelativePath(_dest, f.Destination), f.Size))
            .Should().BeEquivalentTo([
                ("a.txt", "a.txt", 5L), (@"docs\b.txt", @"docs\b.txt", 5L), (@"docs\sub\c.txt", @"docs\sub\c.txt", 7L)]);
        plan.Directories.Select(d => Path.GetRelativePath(_dest, d)).Should().BeEquivalentTo([@"docs", @"docs\sub", @"docs\empty"]);
        plan.Conflicts.Should().BeEmpty();
        plan.TotalBytes.Should().Be(17);
        plan.VersionTime.Should().Be(new DateTime(2026, 9, 30, 14, 5, 0));
    }

    [Fact]
    public void Restores_into_a_chosen_folder_under_the_items_name()
    {
        var plan = Restorer.Plan(_version, ["docs/sub", "docs/b.txt"], _dest, RestoreMode.ToFolder);

        var result = Restorer.Run(plan, ConflictPolicy.Overwrite);

        result.Should().BeEquivalentTo(new RestoreResult(2, 0, 0, [], Canceled: false));
        DestFiles().Should().Equal("b.txt", @"sub\c.txt");
    }

    [Fact]
    public void Restores_a_whole_version_without_its_manifest()
    {
        var plan = Restorer.Plan(_version, [""], _dest, RestoreMode.Original);

        Restorer.Run(plan, ConflictPolicy.Overwrite).Copied.Should().Be(3);

        DestFiles().Should().Equal("a.txt", @"docs\b.txt", @"docs\sub\c.txt");
        Directory.Exists(Dest(@"docs\empty")).Should().BeTrue("a folder restore keeps the structure, also empty folders");
    }

    [Fact]
    public void Keeps_the_versions_last_write_time()
    {
        Restorer.Run(Restorer.Plan(_version, ["a.txt"], _dest, RestoreMode.Original), ConflictPolicy.Overwrite);

        System.IO.File.GetLastWriteTimeUtc(Dest("a.txt")).Should().Be(Old);
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("alpha");
    }

    [Fact]
    public void Overwrites_a_conflicting_file_and_leaves_no_temp_file()
    {
        _tmp.WriteFile(@"dest\a.txt", "changed since");
        var plan = Restorer.Plan(_version, ["a.txt"], _dest, RestoreMode.Original);
        plan.Conflicts.Select(c => c.Destination).Should().Equal(Dest("a.txt"));

        var result = Restorer.Run(plan, ConflictPolicy.Overwrite);

        result.Copied.Should().Be(1);
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("alpha");
        DestFiles().Should().Equal("a.txt");
    }

    [Fact]
    public void Skips_a_conflicting_file()
    {
        _tmp.WriteFile(@"dest\a.txt", "mine");

        var result = Restorer.Run(Restorer.Plan(_version, ["a.txt", "docs/b.txt"], _dest, RestoreMode.Original), ConflictPolicy.Skip);

        result.Should().BeEquivalentTo(new RestoreResult(1, 1, 0, [], Canceled: false));
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("mine");
        System.IO.File.ReadAllText(Dest(@"docs\b.txt")).Should().Be("bravo");
    }

    [Fact]
    public void Keeps_both_with_the_versions_time_in_the_name_and_counts_up_on_collisions()
    {
        _tmp.WriteFile(@"dest\a.txt", "mine");
        _tmp.WriteFile(@"dest\a (2026_09_30-14_05).txt", "earlier restore");
        _tmp.WriteFile(@"dest\a (2026_09_30-14_05) 2.txt", "and another");

        var result = Restorer.Run(Restorer.Plan(_version, ["a.txt"], _dest, RestoreMode.Original), ConflictPolicy.KeepBoth);

        result.Should().BeEquivalentTo(new RestoreResult(0, 0, 1, [], Canceled: false));
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("mine");
        System.IO.File.ReadAllText(Dest("a (2026_09_30-14_05) 3.txt")).Should().Be("alpha");
    }

    [Fact]
    public void Keep_both_name_without_an_extension()
    {
        Restorer.KeepBothName(@"C:\x\Makefile", new DateTime(2026, 9, 30, 14, 5, 0)).Should().Be(@"C:\x\Makefile (2026_09_30-14_05)");
    }

    [Fact]
    public void Never_deletes_files_the_version_does_not_have()
    {
        _tmp.WriteFile(@"dest\docs\extra.txt", "mine");
        _tmp.WriteFile(@"dest\docs\sub\extra2.txt", "mine too");

        Restorer.Run(Restorer.Plan(_version, ["docs"], _dest, RestoreMode.Original), ConflictPolicy.Overwrite);

        DestFiles().Should().Equal(@"docs\b.txt", @"docs\extra.txt", @"docs\sub\c.txt", @"docs\sub\extra2.txt");
    }

    [Fact]
    public void A_locked_or_read_only_file_fails_and_the_rest_continues()
    {
        _tmp.WriteFile(@"dest\docs\b.txt", "locked");
        var readOnly = _tmp.WriteFile(@"dest\docs\sub\c.txt", "read-only");
        System.IO.File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var plan = Restorer.Plan(_version, ["a.txt", "docs"], _dest, RestoreMode.Original);

        RestoreResult result;
        using (new FileStream(Dest(@"docs\b.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            result = Restorer.Run(plan, ConflictPolicy.Overwrite);
        System.IO.File.SetAttributes(readOnly, FileAttributes.Normal);

        result.Copied.Should().Be(1);
        // Windows reports both as "access denied" when the file is replaced; the reason is the system's text.
        result.Failures.Select(f => f.Path).Should().BeEquivalentTo([Dest(@"docs\b.txt"), Dest(@"docs\sub\c.txt")]);
        result.Failures.Should().OnlyContain(f => f.Reason.Length > 0);
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("alpha");
        System.IO.File.ReadAllText(Dest(@"docs\b.txt")).Should().Be("locked");
        System.IO.File.ReadAllText(readOnly).Should().Be("read-only");
        DestFiles().Should().NotContain(f => f.EndsWith(Restorer.TempSuffix));
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData("docs/../../outside.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\Windows")]
    [InlineData("C:relative")]
    [InlineData("missing.txt")]
    [InlineData(VersionName.ManifestFileName)]
    public void Rejects_paths_outside_the_version_and_what_it_does_not_have(string relative)
    {
        var plan = () => Restorer.Plan(_version, [relative], _dest, RestoreMode.Original);

        plan.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Does_not_follow_links()
    {
        var outside = _tmp.CreateDir("outside");
        _tmp.WriteFile(@"outside\secret.txt", "secret");
        Junction.Create(Path.Combine(_version, "docs", "link"), outside);

        var plan = Restorer.Plan(_version, ["docs"], _dest, RestoreMode.Original);
        var linkItself = () => Restorer.Plan(_version, ["docs/link"], _dest, RestoreMode.Original);

        plan.Files.Should().NotContain(f => f.Source.Contains("secret"));
        linkItself.Should().Throw<ArgumentException>();
        var throughLink = () => Restorer.Plan(_version, ["docs/link/secret.txt"], _dest, RestoreMode.Original);
        throughLink.Should().Throw<ArgumentException>();
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void A_failed_copy_leaves_no_temp_file()
    {
        var source = Path.Combine(_version, "a.txt");
        var plan = Restorer.Plan(_version, ["a.txt", "docs/b.txt"], _dest, RestoreMode.Original);

        RestoreResult result;
        using (var holder = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            holder.Lock(0, holder.Length);   // opens fine, reading fails after the temp file exists
            result = Restorer.Run(plan, ConflictPolicy.Overwrite);
        }

        result.Failures.Select(f => f.Path).Should().Equal(Dest("a.txt"));
        result.Copied.Should().Be(1);
        Directory.GetFiles(_dest, "*" + Restorer.TempSuffix, SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void An_existing_file_with_a_temp_like_name_is_never_touched()
    {
        var other = _tmp.WriteFile(@"dest\a.txt" + Restorer.TempSuffix, "not ours");

        var result = Restorer.Run(Restorer.Plan(_version, ["a.txt"], _dest, RestoreMode.Original), ConflictPolicy.Overwrite);

        result.Copied.Should().Be(1);
        System.IO.File.ReadAllText(other).Should().Be("not ours");
        System.IO.File.ReadAllText(Dest("a.txt")).Should().Be("alpha");
    }

    [Fact]
    public void Does_not_write_through_a_link_at_the_destination()
    {
        var outside = _tmp.CreateDir("outside");
        Junction.Create(Dest("docs"), outside);

        var result = Restorer.Run(Restorer.Plan(_version, ["a.txt", "docs"], _dest, RestoreMode.Original), ConflictPolicy.Overwrite);

        Directory.GetFileSystemEntries(outside).Should().BeEmpty();
        result.Copied.Should().Be(1);
        result.Failures.Select(f => f.Path).Should().Contain([Dest(@"docs\b.txt"), Dest(@"docs\sub\c.txt")]);
        result.Failures.Should().OnlyContain(f => f.Reason.Contains("link"));
    }

    [Fact]
    public void Refuses_a_destination_that_is_a_link()
    {
        var outside = _tmp.CreateDir("outside");
        var linkRoot = Dest("linkroot");
        Junction.Create(linkRoot, outside);

        var result = Restorer.Run(Restorer.Plan(_version, ["a.txt"], linkRoot, RestoreMode.Original), ConflictPolicy.Overwrite);

        Directory.GetFileSystemEntries(outside).Should().BeEmpty();
        result.Failures.Should().HaveCount(1);
    }

    [Fact]
    public void Rejects_two_items_with_the_same_name_in_a_folder_restore()
    {
        System.IO.File.WriteAllText(Path.Combine(_version, "docs", "sub", "B.TXT"), "other bravo");
        var plan = () => Restorer.Plan(_version, ["docs/b.txt", "docs/sub/B.TXT"], _dest, RestoreMode.ToFolder);

        plan.Should().Throw<ArgumentException>().WithMessage("*B.TXT*");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void An_unreadable_subfolder_becomes_a_failure_of_the_plan()
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
            var plan = Restorer.Plan(_version, ["docs"], _dest, RestoreMode.Original);
            var result = Restorer.Run(plan, ConflictPolicy.Overwrite);

            result.Failures.Select(f => f.Path).Should().Contain(locked);
            result.Copied.Should().Be(1);   // docs\b.txt
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
    public void Refuses_a_destination_inside_the_version(string below)
    {
        var plan = () => Restorer.Plan(_version, ["a.txt"], Path.Combine(_version, below), RestoreMode.ToFolder);

        plan.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Requires_an_absolute_destination()
    {
        var plan = () => Restorer.Plan(_version, ["a.txt"], "relative", RestoreMode.ToFolder);

        plan.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Cancellation_stops_before_the_next_file()
    {
        var plan = Restorer.Plan(_version, ["a.txt", "docs"], _dest, RestoreMode.Original);
        using var cts = new CancellationTokenSource();

        var result = Restorer.Run(plan, ConflictPolicy.Overwrite, new SyncProgress<RestoreProgress>(p =>
        {
            if (p.FilesDone == 1)
                cts.Cancel();
        }), cts.Token);

        result.Canceled.Should().BeTrue();
        result.Copied.Should().Be(1);
        DestFiles().Should().HaveCount(1).And.NotContain(f => f.EndsWith(Restorer.TempSuffix));
    }

    [Fact]
    public void Reports_progress_by_bytes()
    {
        var reports = new List<RestoreProgress>();

        Restorer.Run(Restorer.Plan(_version, ["docs"], _dest, RestoreMode.Original), ConflictPolicy.Overwrite,
            new SyncProgress<RestoreProgress>(reports.Add));

        reports[^1].Should().Be(new RestoreProgress(2, 2, 12, 12, ""));
        reports.Select(r => r.BytesDone).Should().BeInAscendingOrder();
    }
}
