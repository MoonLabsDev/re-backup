using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Backup.Retry;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Shared.Json;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.Core.Tests.Backup;

public class BackupRunnerRetryTests : IDisposable
{
    private const string Version = "2026_09_30-16_05 Projects";   // 14:05 UTC in the fixed +02:00 test zone
    private const string Locked = @"a\b\locked.txt";
    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.Zero));
    private readonly string _source;
    private readonly string _target;

    public BackupRunnerRetryTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        _source = _tmp.CreateDir("source");
        _target = _tmp.PathOf("target");
        _tmp.WriteFile(@"source\a.txt", "alpha");
        _tmp.WriteFile(@"source\" + Locked, "locked-content");
    }

    public void Dispose() => _tmp.Dispose();

    private BackupPlan Plan() => new()
    {
        Id = "p1",
        Name = "Projects",
        Source = StorageLocation.FileSystem(_source),
        Target = StorageLocation.FileSystem(_target),
    };

    /// <param name="target">Wraps the target's storage, e.g. in a <see cref="FaultyStorage"/>; the source is never wrapped.</param>
    private BackupRunner Runner(Func<IStorage, IStorage>? target = null, IVersionIndexSink? sink = null) =>
        new(target is null ? null : new TestStorageFactory(location =>
        {
            var storage = new StorageFactory().Open(location);
            return location.Path == _target ? target(storage) : storage;
        }), _time, indexSink: sink);

    private string VersionPath => Path.Combine(_target, Version);
    private string ManifestPath => Path.Combine(VersionPath, "re-manifest.json");

    private BackupManifest ReadManifest() =>
        JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(ManifestPath), JsonDefaults.Options)!;

    private static string Hash(string content) =>
        "xxh64:" + Convert.ToHexStringLower(XxHash64.Hash(Encoding.UTF8.GetBytes(content)));

    private FileStream Lock(string relative) =>
        new(Path.Combine(_source, relative), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    /// <summary>A regular run during which <paramref name="relative"/> is locked by another program.</summary>
    private async Task<RunLogEntry> RunWithLockedAsync(string relative = Locked)
    {
        using var locked = Lock(relative);
        var run = await Runner().RunAsync(new BackupRequest(Plan(), [], RunTrigger.Manual));
        run.Version.Should().Be(Version);
        run.RetryCandidates.Should().ContainSingle();
        return run;
    }

    private static RetryRequest RequestFor(RunLogEntry run, BackupPlan plan, IReadOnlyList<SkippedEntry>? files = null) =>
        new(plan, run.RunId, run.Version!, files ?? [.. run.RetryCandidates], Attempt: 1, Attempts: 3);

    private Task<RunLogEntry> RetryAsync(RunLogEntry run, BackupRunner? runner = null, IReadOnlyList<SkippedEntry>? files = null) =>
        (runner ?? Runner()).RetryAsync(RequestFor(run, Plan(), files));

    [Fact]
    public async Task A_file_locked_during_the_run_is_added_to_its_version_by_the_retry()
    {
        var run = await RunWithLockedAsync();
        var before = ReadManifest();

        var retry = await RetryAsync(run);

        File.Exists(Path.Combine(VersionPath, "a", "b", "locked.txt")).Should().BeTrue();
        File.Exists(Path.Combine(VersionPath, "locked.txt")).Should().BeFalse("a file in a subfolder keeps its folders");
        File.ReadAllText(Path.Combine(VersionPath, "a", "b", "locked.txt")).Should().Be("locked-content");
        var manifest = ReadManifest();
        manifest.Files.Should().ContainSingle(f => f.Path == "a/b/locked.txt" && f.Hash == Hash("locked-content"))
            .Which.MtimeUtc.Should().Be(File.GetLastWriteTimeUtc(Path.Combine(_source, Locked)));
        manifest.FileCount.Should().Be(before.FileCount + 1);
        manifest.TotalBytes.Should().Be(before.TotalBytes + "locked-content".Length);
        manifest.Directories.Should().BeEquivalentTo(before.Directories);
        manifest.CreatedUtc.Should().Be(before.CreatedUtc);

        retry.Status.Should().Be(RunStatus.Completed);
        retry.Trigger.Should().Be(RunTrigger.Retry);
        retry.RetryOf.Should().Be(run.RunId);
        retry.RunId.Should().HaveLength(32).And.NotBe(run.RunId);
        retry.Attempt.Should().Be(1);
        retry.Attempts.Should().Be(3);
        retry.Version.Should().Be(Version);
        retry.VersionGone.Should().BeFalse();
        retry.FilesCopied.Should().Be(1);
        retry.BytesCopied.Should().Be("locked-content".Length);
        retry.SkippedCount.Should().Be(0);
        retry.RetryCandidates.Should().BeEmpty();
        Directory.GetFiles(VersionPath, "*.rebackup-tmp", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_that_changed_while_copied_gets_the_new_content_and_hash()
    {
        var appended = false;
        var changing = (IStorage s) => new FaultyStorage(s)
        {
            Before = (operation, path) =>
            {
                if (appended || operation != "create" || path != $"{Version}/a.txt")
                    return;
                appended = true;
                using var append = new FileStream(Path.Combine(_source, "a.txt"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                append.Write("-more"u8);
            },
        };
        var run = await Runner(changing).RunAsync(new BackupRequest(Plan(), [], RunTrigger.Manual));
        run.RetryCandidates.Should().ContainSingle(c => c.Path == "a.txt" && c.Key == "core.skip.changed");
        var countBefore = ReadManifest().FileCount;
        File.WriteAllText(Path.Combine(_source, "a.txt"), "alpha-final");

        var retry = await RetryAsync(run);

        retry.Status.Should().Be(RunStatus.Completed);
        retry.FilesCopied.Should().Be(1);
        File.ReadAllText(Path.Combine(VersionPath, "a.txt")).Should().Be("alpha-final");
        var manifest = ReadManifest();
        var entry = manifest.Files.Should().ContainSingle(f => f.Path == "a.txt").Subject;
        entry.Hash.Should().Be(Hash("alpha-final"));
        entry.Size.Should().Be("alpha-final".Length);
        entry.MtimeUtc.Should().Be(File.GetLastWriteTimeUtc(Path.Combine(_source, "a.txt")));
        manifest.FileCount.Should().Be(countBefore);
        manifest.TotalBytes.Should().Be(manifest.Files.Sum(f => f.Size));
    }

    [Fact]
    public async Task A_path_that_differs_only_in_case_replaces_its_entry()
    {
        _tmp.WriteFile(@"source\Foo.txt", "foo-1");
        var run = await Runner().RunAsync(new BackupRequest(Plan(), [], RunTrigger.Manual));
        ReadManifest().Files.Should().ContainSingle(f => f.Path == "Foo.txt");
        var countBefore = ReadManifest().FileCount;
        File.WriteAllText(Path.Combine(_source, "Foo.txt"), "foo-two");

        var retry = await RetryAsync(run, files: [new SkippedEntry("foo.txt", "changed while it was copied", "core.skip.changed")]);

        retry.Status.Should().Be(RunStatus.Completed);
        var manifest = ReadManifest();
        manifest.Files.Should().ContainSingle(f => f.Path.Equals("foo.txt", StringComparison.OrdinalIgnoreCase))
            .Which.Hash.Should().Be(Hash("foo-two"));
        manifest.FileCount.Should().Be(countBefore);
        Directory.GetFiles(VersionPath, "foo.txt").Should().ContainSingle();
    }

    [Fact]
    public async Task A_file_still_locked_stays_open_and_the_version_is_unchanged()
    {
        var run = await RunWithLockedAsync();
        var manifestBytes = File.ReadAllBytes(ManifestPath);
        var sink = new RecordingSink();

        RunLogEntry retry;
        using (Lock(Locked))
            retry = await RetryAsync(run, Runner(sink: sink));

        retry.Status.Should().Be(RunStatus.CompletedWithWarnings);
        retry.FilesCopied.Should().Be(0);
        retry.SkippedCount.Should().Be(1);
        retry.RetryCandidates.Should().ContainSingle(c => c.Path == "a/b/locked.txt" && c.Key == "core.file.locked");
        File.ReadAllBytes(ManifestPath).Should().Equal(manifestBytes);
        File.Exists(Path.Combine(VersionPath, "a", "b", "locked.txt")).Should().BeFalse();
        sink.Added.Should().BeEmpty("nothing changed in the version");
    }

    [Fact]
    public async Task A_file_deleted_since_the_run_is_resolved()
    {
        var run = await RunWithLockedAsync();
        var manifestBytes = File.ReadAllBytes(ManifestPath);
        File.Delete(Path.Combine(_source, Locked));

        var retry = await RetryAsync(run);

        retry.Status.Should().Be(RunStatus.Completed);
        retry.FilesCopied.Should().Be(0);
        retry.SkippedCount.Should().Be(0);
        retry.RetryCandidates.Should().BeEmpty();
        File.ReadAllBytes(ManifestPath).Should().Equal(manifestBytes);
    }

    [Fact]
    public async Task A_version_that_is_gone_is_reported_and_nothing_is_written()
    {
        var run = await RunWithLockedAsync();
        Directory.Delete(VersionPath, recursive: true);

        var retry = await RetryAsync(run);

        retry.VersionGone.Should().BeTrue();
        retry.Trigger.Should().Be(RunTrigger.Retry);
        retry.RetryOf.Should().Be(run.RunId);
        Directory.Exists(VersionPath).Should().BeFalse();
        Directory.GetFileSystemEntries(_target).Should().BeEmpty();
    }

    [Fact]
    public async Task A_manifest_changed_meanwhile_fails_the_retry()
    {
        var run = await RunWithLockedAsync();
        var other = File.ReadAllText(ManifestPath) + "\n\n";
        var rewriting = (IStorage s) => new FaultyStorage(s)
        {
            Before = (operation, path) =>
            {
                if (operation == "commit" && path == $"{Version}/re-manifest.json")
                    File.WriteAllText(ManifestPath, other);
            },
        };

        var retry = await RetryAsync(run, Runner(rewriting));

        retry.Status.Should().Be(RunStatus.Error);
        retry.Reason.Should().NotBeNullOrEmpty();
        File.ReadAllText(ManifestPath).Should().Be(other, "the manifest written meanwhile is kept");
        retry.FilesCopied.Should().Be(0, "nothing was added to the version");
        retry.RetryCandidates.Should().ContainSingle(c => c.Path == "a/b/locked.txt", "the file is still to be retried");
    }

    [Fact]
    public async Task The_amended_version_is_sent_to_the_index()
    {
        var run = await RunWithLockedAsync();
        var sink = new RecordingSink();

        var retry = await RetryAsync(run, Runner(sink: sink));

        retry.Status.Should().Be(RunStatus.Completed);
        retry.Warnings.Should().BeEmpty();
        var (planId, version, manifest) = sink.Added.Should().ContainSingle().Subject;
        planId.Should().Be("p1");
        version.Name.Should().Be(Version);
        version.Ownership.Should().Be(VersionOwnership.Owned);
        version.FileCount.Should().Be(2);
        version.TotalBytes.Should().Be("alpha".Length + "locked-content".Length);
        manifest.Files.Select(f => f.Path).Should().BeEquivalentTo("a.txt", "a/b/locked.txt");
    }

    [Fact]
    public async Task A_file_that_changes_again_while_retried_is_replaced_but_stays_open()
    {
        var run = await RunWithLockedAsync();
        var changing = (IStorage s) => new FaultyStorage(s)
        {
            Before = (operation, path) =>
            {
                if (operation != "create" || path != $"{Version}/a/b/locked.txt")
                    return;
                using var append = new FileStream(Path.Combine(_source, Locked), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                append.Write("-more"u8);
            },
        };

        var retry = await RetryAsync(run, Runner(changing));

        retry.Status.Should().Be(RunStatus.CompletedWithWarnings);
        retry.FilesCopied.Should().Be(0, "a file that changed again is not recovered");
        retry.RetryCandidates.Should().ContainSingle(c => c.Path == "a/b/locked.txt" && c.Key == "core.skip.changed");
        var copy = File.ReadAllText(Path.Combine(VersionPath, "a", "b", "locked.txt"));
        ReadManifest().Files.Should().ContainSingle(f => f.Path == "a/b/locked.txt")
            .Which.Hash.Should().Be(Hash(copy), "the entry describes the copy on disk");
    }

    [Fact]
    public async Task A_source_that_is_gone_fails_the_retry_and_keeps_its_files()
    {
        var run = await RunWithLockedAsync();
        var manifestBytes = File.ReadAllBytes(ManifestPath);
        Directory.Delete(_source, recursive: true);

        var retry = await RetryAsync(run);

        retry.Status.Should().Be(RunStatus.Error);
        retry.RetryCandidates.Should().ContainSingle(c => c.Path == "a/b/locked.txt",
            "a missing source does not mean the file was deleted");
        File.ReadAllBytes(ManifestPath).Should().Equal(manifestBytes);
    }

    [Fact]
    public async Task A_canceled_retry_is_canceled_and_keeps_its_files()
    {
        var run = await RunWithLockedAsync();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var retry = await Runner().RetryAsync(RequestFor(run, Plan()), cancellationToken: cancel.Token);

        retry.Status.Should().Be(RunStatus.Canceled);
        retry.RetryCandidates.Should().ContainSingle(c => c.Path == "a/b/locked.txt");
        File.Exists(Path.Combine(VersionPath, "a", "b", "locked.txt")).Should().BeFalse();
    }

    private sealed class RecordingSink : IVersionIndexSink
    {
        public List<(string PlanId, VersionInfo Version, BackupManifest Manifest)> Added { get; } = [];

        public void Add(string planId, IStorage target, VersionInfo version, BackupManifest manifest) =>
            Added.Add((planId, version, manifest));
    }
}
