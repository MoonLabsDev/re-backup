using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Storage;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Json;
using ReBackup.Shared.Schedule;

namespace ReBackup.Core.Tests.Backup;

public class BackupRunnerTests : IDisposable
{
    private const string Minute = "2026_09_30-16_05";   // 14:05 UTC in the fixed +02:00 test zone
    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.Zero));
    private readonly string _source;
    private readonly string _target;

    public BackupRunnerTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        _source = _tmp.CreateDir("source");
        _target = _tmp.PathOf("target");
        _tmp.WriteFile(@"source\a.txt", "alpha");
        _tmp.WriteFile(@"source\sub\b.bin", "bravo-bravo");
        _tmp.CreateDir(@"source\empty");
    }

    public void Dispose() => _tmp.Dispose();

    private BackupPlan Plan(params string[] patterns) => new()
    {
        Id = "p1",
        Name = "Projects",
        Source = StorageLocation.FileSystem(_source),
        Target = StorageLocation.FileSystem(_target),
        Ignore = new IgnoreSettings { Patterns = [.. patterns] },
    };

    private BackupRunner Runner(ITargetVolume? volume = null) => new(volume ?? new PhysicalTargetVolume(), _time);

    private static BackupRequest Request(BackupPlan plan, params string[] globalDefaults) =>
        new(plan, globalDefaults, RunTrigger.Manual);

    private string VersionPath(string minute = Minute) => Path.Combine(_target, $"{minute} Projects");

    private string[] TargetEntries() =>
        Directory.Exists(_target) ? Directory.GetFileSystemEntries(_target).Select(e => Path.GetFileName(e)!).ToArray() : [];

    [Fact]
    public async Task Copies_everything_into_a_named_version_folder()
    {
        var mtime = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(_source, "a.txt"), mtime);

        var entry = await Runner().RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Completed);
        entry.Reason.Should().BeNull();
        entry.Trigger.Should().Be(RunTrigger.Manual);
        entry.Version.Should().Be($"{Minute} Projects");
        entry.FilesCopied.Should().Be(2);
        entry.BytesCopied.Should().Be(16);
        entry.SkippedCount.Should().Be(0);
        entry.RunId.Should().HaveLength(32);
        entry.StartUtc.Should().Be(_time.GetUtcNow().UtcDateTime);

        TargetEntries().Should().Equal($"{Minute} Projects");
        File.ReadAllText(Path.Combine(VersionPath(), "a.txt")).Should().Be("alpha");
        File.ReadAllText(Path.Combine(VersionPath(), "sub", "b.bin")).Should().Be("bravo-bravo");
        Directory.Exists(Path.Combine(VersionPath(), "empty")).Should().BeTrue();
        File.GetLastWriteTimeUtc(Path.Combine(VersionPath(), "a.txt")).Should().Be(mtime);
    }

    [Fact]
    public async Task Writes_a_manifest_with_sizes_times_and_xxhash64()
    {
        await Runner().RunAsync(Request(Plan()));

        var json = File.ReadAllText(Path.Combine(VersionPath(), "re-manifest.json"));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(json, JsonDefaults.Options)!;
        manifest.FormatVersion.Should().Be(1);
        manifest.PlanId.Should().Be("p1");
        manifest.PlanName.Should().Be("Projects");
        manifest.Source.Should().Be(_source);
        manifest.CreatedUtc.Should().Be(_time.GetUtcNow().UtcDateTime);
        manifest.Files.Select(f => f.Path).Should().BeEquivalentTo("a.txt", "sub/b.bin");

        var a = manifest.Files.Single(f => f.Path == "a.txt");
        a.Size.Should().Be(5);
        a.MtimeUtc.Should().Be(File.GetLastWriteTimeUtc(Path.Combine(_source, "a.txt")));
        a.Hash.Should().Be("xxh64:" + Convert.ToHexStringLower(XxHash64.Hash(Encoding.UTF8.GetBytes("alpha"))));
        json.Should().Contain("\"mtimeUtc\"").And.Contain("\"formatVersion\": 1");
    }

    [Fact]
    public async Task Applies_plan_global_and_nested_ignore_patterns()
    {
        _tmp.WriteFile(@"source\note.tmp", "x");
        _tmp.WriteFile(@"source\cache\big.dat", "x");
        _tmp.WriteFile(@"source\sub\.backupignore", "secret.txt");
        _tmp.WriteFile(@"source\sub\secret.txt", "x");

        var entry = await Runner().RunAsync(Request(Plan("*.tmp"), "cache/"));

        entry.Status.Should().Be(RunStatus.Completed);
        File.Exists(Path.Combine(VersionPath(), "note.tmp")).Should().BeFalse();
        Directory.Exists(Path.Combine(VersionPath(), "cache")).Should().BeFalse();
        File.Exists(Path.Combine(VersionPath(), "sub", "secret.txt")).Should().BeFalse();
        File.Exists(Path.Combine(VersionPath(), "sub", ".backupignore")).Should().BeTrue("ignore files are backed up");
        File.Exists(Path.Combine(VersionPath(), "a.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Locked_file_is_skipped_and_the_run_completes_with_warnings()
    {
        using var locked = new FileStream(Path.Combine(_source, "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var entry = await Runner().RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings);
        entry.SkippedCount.Should().Be(1);
        entry.Skipped.Should().ContainSingle().Which.Should().Be(new SkippedEntry("a.txt", "locked by another program"));
        entry.FilesCopied.Should().Be(1);
        File.Exists(Path.Combine(VersionPath(), "a.txt")).Should().BeFalse();
        File.Exists(Path.Combine(VersionPath(), "sub", "b.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task Folder_that_was_not_scanned_is_recorded_as_skipped()
    {
        var link = Path.Combine(_source, "link");
        RunMklink(link, Path.Combine(_source, "sub"));
        try
        {
            var entry = await Runner().RunAsync(Request(Plan()));

            entry.Status.Should().Be(RunStatus.CompletedWithWarnings);
            entry.Skipped.Should().ContainSingle().Which.Should().Be(new SkippedEntry("link", "Link is not followed."));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task Source_file_named_like_the_manifest_is_skipped()
    {
        _tmp.WriteFile(@"source\re-manifest.json", "mine");

        var entry = await Runner().RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings);
        entry.Skipped.Should().ContainSingle().Which.Path.Should().Be("re-manifest.json");
        File.ReadAllText(Path.Combine(VersionPath(), "re-manifest.json")).Should().Contain("\"planId\"");
    }

    [Fact]
    public async Task Cancellation_before_the_start_yields_Canceled_and_writes_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var entry = await Runner().RunAsync(Request(Plan()), cancellationToken: cts.Token);

        entry.Status.Should().Be(RunStatus.Canceled);
        entry.Version.Should().BeNull();
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_during_the_copy_removes_the_partial_folder()
    {
        using var cts = new CancellationTokenSource();
        var volume = new FakeVolume { OnCreateFile = _ => cts.Cancel() };

        var entry = await Runner(volume).RunAsync(Request(Plan()), cancellationToken: cts.Token);

        entry.Status.Should().Be(RunStatus.Canceled);
        entry.Version.Should().BeNull();
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_reports_the_cleanup_of_the_partial_folder()
    {
        using var cts = new CancellationTokenSource();
        var volume = new FakeVolume { OnCreateFile = _ => cts.Cancel() };
        var reports = new List<BackupProgress>();

        await Runner(volume).RunAsync(Request(Plan()), new SyncProgress(reports.Add), cts.Token);

        reports[^1].Phase.Should().Be(BackupPhase.CleaningUp);
    }

    [Fact]
    public async Task Reports_the_folder_creation_before_copying()
    {
        var reports = new List<BackupProgress>();

        await Runner().RunAsync(Request(Plan()), new SyncProgress(reports.Add));

        var folders = reports.Where(p => p.Phase == BackupPhase.CreatingFolders).ToList();
        folders.Should().NotBeEmpty();
        folders.Should().OnlyContain(p => p.FilesTotal == 2 && p.BytesTotal == 0);
        folders[^1].FilesDone.Should().Be(2);
        folders[^1].Fraction.Should().Be(1);
        reports.FindLastIndex(p => p.Phase == BackupPhase.CreatingFolders)
            .Should().BeLessThan(reports.FindIndex(p => p.Phase == BackupPhase.Copying));
    }

    [Fact]
    public async Task Too_little_free_space_aborts_as_Full_before_writing()
    {
        var volume = new FakeVolume { FreeSpace = 16 };   // 16 bytes needed + 5 % does not fit

        var entry = await Runner(volume).RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Full);
        entry.Reason.Should().Contain("is free on the target");
        volume.FilesCreated.Should().Be(0);
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Just_enough_free_space_passes_the_preflight()
    {
        var entry = await Runner(new FakeVolume { FreeSpace = 17 }).RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Completed);
    }

    [Fact]
    public async Task Disk_full_during_the_copy_aborts_as_Full_and_removes_the_partial_folder()
    {
        var volume = new FakeVolume { FailWritesWithDiskFull = true };

        var entry = await Runner(volume).RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Full);
        entry.Reason.Should().NotBeNullOrEmpty();
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_source_aborts_as_Error()
    {
        var plan = Plan();
        plan.Source = StorageLocation.FileSystem(_tmp.PathOf("nope"));

        var entry = await Runner().RunAsync(Request(plan));

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Contain("does not exist");
        Directory.Exists(_target).Should().BeFalse();
    }

    [Fact]
    public async Task Unexpected_write_error_aborts_as_Error_and_removes_the_partial_folder()
    {
        var volume = new FakeVolume { OnCreateFile = _ => throw new IOException("device not ready") };

        var entry = await Runner(volume).RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be("device not ready");
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Leftover_partial_folders_of_the_plan_are_removed_and_other_folders_kept()
    {
        _tmp.WriteFile(@"target\2026_09_29-10_00 Projects.partial\x.txt", "stale");
        _tmp.WriteFile(@"target\2026_09_29-10_00 Other.partial\x.txt", "foreign");
        _tmp.WriteFile(@"target\notes\x.txt", "foreign");
        _tmp.WriteFile(@"target\2026_09_28-09_00 Projects\a.txt", "older version");

        var entry = await Runner().RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Completed);
        TargetEntries().Should().BeEquivalentTo(
            "2026_09_28-09_00 Projects", "2026_09_29-10_00 Other.partial", "notes", $"{Minute} Projects");
    }

    [Fact]
    public async Task Removing_leftovers_reports_its_own_phase_with_the_files_removed()
    {
        _tmp.WriteFile(@"target\2026_09_29-10_00 Projects.partial\x.txt", "stale");
        _tmp.WriteFile(@"target\2026_09_29-10_00 Projects.partial\sub\y.txt", "stale");
        VersionFolder.Create(_target, "2026_09_28-09_00 Projects.deleting", "p1");
        var reports = new List<BackupProgress>();

        await Runner().RunAsync(Request(Plan()), new SyncProgress<BackupProgress>(reports.Add));

        var leftovers = reports.TakeWhile(r => r.Phase == BackupPhase.RemovingLeftovers).ToList();
        leftovers.Should().NotBeEmpty("the leftovers are removed before the source is indexed");
        leftovers.Select(r => r.FilesDone).Should().BeInAscendingOrder();
        leftovers.Last().FilesDone.Should().Be(3, "x.txt and y.txt of the partial folder, data.bin of the remains");
        reports.Skip(leftovers.Count).Should().NotContain(r => r.Phase == BackupPhase.RemovingLeftovers);
        TargetEntries().Should().BeEquivalentTo($"{Minute} Projects");
    }

    [Fact]
    public async Task Without_leftovers_there_is_no_leftover_phase()
    {
        var reports = new List<BackupProgress>();

        await Runner().RunAsync(Request(Plan()), new SyncProgress<BackupProgress>(reports.Add));

        reports.Should().NotContain(r => r.Phase == BackupPhase.RemovingLeftovers);
    }

    [Fact]
    public async Task A_plan_name_ending_in_partial_aborts_as_Error_and_touches_nothing()
    {
        _tmp.WriteFile(@"target\2026_09_28-09_00 Projects\a.txt", "older version");
        _tmp.WriteFile(@"target\2026_09_29-10_00 Projects.partial\x.txt", "stale");
        var before = TargetEntries();
        var plan = Plan();
        plan.Name = "Projects.partial";

        var entry = await Runner().RunAsync(Request(plan));

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be("The plan name must not end with \".partial\".");
        TargetEntries().Should().BeEquivalentTo(before);
        File.Exists(Path.Combine(_target, "2026_09_29-10_00 Projects.partial", "x.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task A_plan_name_that_Windows_would_change_aborts_as_Error()
    {
        var plan = Plan();
        plan.Name = "Projects.";

        var entry = await Runner().RunAsync(Request(plan));

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be("The plan name \"Projects.\" cannot be used: Name must not end with a dot.");
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task A_partial_folder_with_a_manifest_is_a_finished_version_of_another_plan_and_is_kept()
    {
        _tmp.WriteFile(@"target\2026_09_29-10_00 Projects.partial\re-manifest.json", "{}");
        _tmp.WriteFile(@"target\2026_09_29-10_00 Projects.partial\a.txt", "finished");

        var entry = await Runner().RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Completed);
        File.Exists(Path.Combine(_target, "2026_09_29-10_00 Projects.partial", "a.txt")).Should().BeTrue();
        TargetEntries().Should().BeEquivalentTo("2026_09_29-10_00 Projects.partial", $"{Minute} Projects");
    }

    [Fact]
    public async Task Waits_for_the_next_minute_when_the_version_folder_already_exists()
    {
        Directory.CreateDirectory(VersionPath());

        var run = Runner().RunAsync(Request(Plan()));
        var waited = Stopwatch.StartNew();
        while (!run.IsCompleted && waited.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(25);
            _time.Advance(TimeSpan.FromSeconds(10));
        }

        run.IsCompleted.Should().BeTrue();
        var entry = await run;
        entry.Status.Should().Be(RunStatus.Completed);
        entry.Version.Should().NotBe($"{Minute} Projects", "that folder already existed");
        VersionName.TryParse(entry.Version!, "Projects", out var versionTime).Should().BeTrue();
        versionTime.Should().BeAfter(new DateTime(2026, 9, 30, 16, 5, 0));
        File.Exists(Path.Combine(_target, entry.Version!, "a.txt")).Should().BeTrue();
        Directory.GetFileSystemEntries(VersionPath()).Should().BeEmpty("the existing folder is not touched");
    }

    [Fact]
    public async Task Reports_progress_up_to_the_end()
    {
        var reports = new List<BackupProgress>();

        await Runner().RunAsync(Request(Plan()), new SyncProgress(reports.Add));

        reports.Should().Contain(p => p.Phase == BackupPhase.Copying);
        var last = reports[^1];
        last.Phase.Should().Be(BackupPhase.Finishing);
        last.FilesDone.Should().Be(2);
        last.FilesTotal.Should().Be(2);
        last.BytesDone.Should().Be(16);
        last.BytesTotal.Should().Be(16);
        last.Fraction.Should().Be(1);
    }

    [Fact]
    public async Task Duration_is_measured_with_the_time_provider()
    {
        var volume = new FakeVolume { OnCreateFile = _ => _time.Advance(TimeSpan.FromSeconds(2)) };

        var entry = await Runner(volume).RunAsync(Request(Plan()));

        entry.DurationMs.Should().Be(4000);
        entry.EndUtc.Should().Be(entry.StartUtc.AddSeconds(4));
    }

    [Fact]
    public async Task File_modified_during_the_copy_is_kept_and_reported_as_changed()
    {
        var volume = new FakeVolume
        {
            OnCreateFile = path =>
            {
                if (Path.GetFileName(path) != "a.txt")
                    return;
                using var append = new FileStream(Path.Combine(_source, "a.txt"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                append.Write("-more"u8);
            },
        };

        var entry = await Runner(volume).RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings);
        entry.Skipped.Should().ContainSingle().Which.Should().Be(
            new SkippedEntry("a.txt", "changed while it was copied; the copy may be inconsistent"));
        entry.FilesCopied.Should().Be(2);
        var copied = File.ReadAllBytes(Path.Combine(VersionPath(), "a.txt"));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(
            File.ReadAllText(Path.Combine(VersionPath(), "re-manifest.json")), JsonDefaults.Options)!;
        var a = manifest.Files.Single(f => f.Path == "a.txt");
        a.Size.Should().Be(copied.Length);
        a.Hash.Should().Be("xxh64:" + Convert.ToHexStringLower(XxHash64.Hash(copied)));
        a.MtimeUtc.Should().Be(File.GetLastWriteTimeUtc(Path.Combine(_source, "a.txt")));
        File.GetLastWriteTimeUtc(Path.Combine(VersionPath(), "a.txt")).Should().Be(a.MtimeUtc);
    }

    [Fact]
    public async Task Source_that_disappears_during_the_run_aborts_as_Error()
    {
        var volume = new FakeVolume { OnFreeSpaceQuery = () => Directory.Move(_source, _source + "-gone") };

        var entry = await Runner(volume).RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be("The source folder is no longer available.");
        entry.Version.Should().BeNull();
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_final_rename_is_retried()
    {
        var volume = new FakeVolume { MoveFailures = 2 };

        var entry = await Runner(volume).RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Completed);
        TargetEntries().Should().Equal($"{Minute} Projects");
    }

    [Fact]
    public async Task A_final_rename_that_keeps_failing_aborts_as_Error_and_removes_the_partial_folder()
    {
        var volume = new FakeVolume { MoveFailures = 99 };

        var entry = await Runner(volume).RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.Error);
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Plan_name_with_path_characters_aborts_as_Error()
    {
        var plan = Plan();
        plan.Name = @"..\evil";

        var entry = await Runner().RunAsync(Request(plan));

        entry.Status.Should().Be(RunStatus.Error);
        Directory.Exists(_tmp.PathOf("evil")).Should().BeFalse();
        TargetEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Target_inside_the_source_aborts_as_Error()
    {
        var plan = Plan();
        plan.Target = StorageLocation.FileSystem(Path.Combine(_source, "out"));

        var entry = await Runner().RunAsync(Request(plan));

        entry.Status.Should().Be(RunStatus.Error);
        Directory.Exists(plan.Target.Path).Should().BeFalse();
    }

    [Fact]
    public async Task Source_inside_the_target_aborts_as_Error()
    {
        var plan = Plan();
        plan.Target = StorageLocation.FileSystem(_tmp.Root);

        var entry = await Runner().RunAsync(Request(plan));

        entry.Status.Should().Be(RunStatus.Error);
    }

    [Fact]
    public async Task Root_folder_named_like_the_manifest_is_skipped()
    {
        _tmp.WriteFile(@"source\re-manifest.json\inner.txt", "x");

        var entry = await Runner().RunAsync(Request(Plan()));

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings);
        entry.Skipped.Should().ContainSingle().Which.Path.Should().Be("re-manifest.json");
        File.ReadAllText(Path.Combine(VersionPath(), "re-manifest.json")).Should().Contain("\"planId\"");
        entry.FilesCopied.Should().Be(2);
    }

    [Fact]
    public async Task Manifest_carries_the_file_count_and_total_size_in_front_of_the_file_list()
    {
        await Runner().RunAsync(Request(Plan()));

        var json = File.ReadAllText(Path.Combine(VersionPath(), "re-manifest.json"));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(json, JsonDefaults.Options)!;
        manifest.FileCount.Should().Be(2);
        manifest.TotalBytes.Should().Be(16);
        json.IndexOf("\"totalBytes\"", StringComparison.Ordinal).Should().BeLessThan(json.IndexOf("\"files\"", StringComparison.Ordinal));
        ManifestReader.ReadHeader(Path.Combine(VersionPath(), "re-manifest.json")).TotalBytes.Should().Be(16);
    }

    private static void RunMklink(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Fail($"mklink failed: {output}");
    }

    private sealed class SyncProgress(Action<BackupProgress> onReport) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => onReport(value);
    }

    /// <summary>Writes to the real disk, with switches to simulate a small or full target.</summary>
    private sealed class FakeVolume : ITargetVolume
    {
        private readonly PhysicalTargetVolume _inner = new();

        public long? FreeSpace { get; init; }
        public Action<string>? OnCreateFile { get; init; }
        public bool FailWritesWithDiskFull { get; init; }
        public Action? OnFreeSpaceQuery { get; init; }
        public int MoveFailures { get; init; }
        public int FilesCreated { get; private set; }
        private int _moveAttempts;

        public long GetAvailableFreeSpace(string directory)
        {
            OnFreeSpaceQuery?.Invoke();
            return FreeSpace ?? _inner.GetAvailableFreeSpace(directory);
        }

        public void MoveDirectory(string source, string destination)
        {
            if (_moveAttempts++ < MoveFailures)
                throw new IOException("directory is in use");
            _inner.MoveDirectory(source, destination);
        }

        public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);

        public Stream CreateFile(string path)
        {
            OnCreateFile?.Invoke(path);
            FilesCreated++;
            var stream = _inner.CreateFile(path);
            return FailWritesWithDiskFull ? new DiskFullStream(stream) : stream;
        }
    }

    private sealed class DiskFullStream(Stream inner) : Stream
    {
        private const int ErrorDiskFull = unchecked((int)0x80070070);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("There is not enough space on the disk.", ErrorDiskFull);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
