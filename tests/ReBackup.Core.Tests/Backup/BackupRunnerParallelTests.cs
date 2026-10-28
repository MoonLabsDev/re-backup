using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Json;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Backup;

/// <summary>
/// Runs from or to anything but a folder (S3, Google Drive) copy with <see cref="BackupRunner.ParallelS3Copies"/> workers (spec 4); folder to folder stays one
/// file at a time. The storages hold copies in flight at known points (no sleeps): a hook waits until enough copies have
/// arrived, and the guard timeout only turns a hang into a failure.
/// </summary>
public class BackupRunnerParallelTests
{
    private const string Version = "2026_09_30-16_05 Projects";
    private const int FileCount = 20;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly DateTime Mtime = new(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.Zero));
    private readonly InMemoryStorage _sourceFiles = new(StorageCapabilities.None);
    private readonly InMemoryStorage _targetFiles = new(StorageCapabilities.None);

    public BackupRunnerParallelTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        for (var i = 0; i < FileCount; i++)
            _sourceFiles.AddFile(Name(i), Enumerable.Repeat((byte)i, 100 + i).ToArray(), Mtime);
    }

    private static string Name(int i) => $"f{i:00}.bin";

    private static long TotalBytes => Enumerable.Range(0, FileCount).Sum(i => 100L + i);

    private static bool IsCopy(string path) => path.StartsWith(Version + "/f", StringComparison.Ordinal);

    private static int IndexOf(string path) => int.Parse(path[^6..^4]);

    private Task<RunLogEntry> Run(string sourceKind, string targetKind, IStorage source, IStorage target,
        IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = Location(sourceKind, Path.GetTempPath(), "conn-1"),
            Target = Location(targetKind, FolderTarget, "conn-2"),
        };
        var storages = new TestStorageFactory(location => location == plan.Source ? source : target);
        return new BackupRunner(storages, _time, host: "TESTHOST")
            .RunAsync(new BackupRequest(plan, [], RunTrigger.Manual), progress, cancellationToken);
    }

    /// <summary>A folder target outside the source folder (the runner checks that a folder source exists, not the target).</summary>
    private static readonly string FolderTarget = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "rebackup-parallel-test-target");

    /// <summary>A folder location needs a real folder as source; other kinds address their storage through the connection.</summary>
    private static StorageLocation Location(string kind, string folder, string connectionId) =>
        kind == StorageLocation.FileSystemKind ? StorageLocation.FileSystem(folder) : new StorageLocation(kind, "", connectionId);

    private BackupManifest Manifest() =>
        JsonSerializer.Deserialize<BackupManifest>(_targetFiles.ReadAllBytes($"{Version}/re-manifest.json"), JsonDefaults.Options)!;

    /// <summary>A hook that holds every file until <paramref name="count"/> of them are held at once, then lets all pass.</summary>
    private static Func<string, CancellationToken, Task> HoldUntil(int count, Func<string, bool> applies,
        TaskCompletionSource allHeld)
    {
        var held = 0;
        return async (path, ct) =>
        {
            if (!applies(path))
                return;
            if (Interlocked.Increment(ref held) >= count)
                allHeld.TrySetResult();
            await allHeld.Task.WaitAsync(Guard, ct);
        };
    }

    [Theory]
    [InlineData("s3", "mem")]
    [InlineData("mem", "s3")]
    [InlineData("s3", "s3")]
    [InlineData("fs", "gdrive")]
    [InlineData("gdrive", "fs")]
    [InlineData("gdrive", "s3")]
    public async Task Copies_with_eight_workers_when_source_or_target_is_not_a_folder(string sourceKind, string targetKind)
    {
        var allHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new GatedStorage(_sourceFiles) { BeforeOpen = HoldUntil(BackupRunner.ParallelS3Copies, _ => true, allHeld) };
        var reports = new ConcurrentQueue<BackupProgress>();

        var entry = await Run(sourceKind, targetKind, source, _targetFiles, new SyncProgress<BackupProgress>(reports.Enqueue));

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        BackupRunner.ParallelS3Copies.Should().Be(8);
        source.MaxInFlight.Should().Be(8);
        entry.FilesCopied.Should().Be(FileCount);
        entry.BytesCopied.Should().Be(TotalBytes);
        Manifest().Files.Select(f => f.Path).Should().Equal(Enumerable.Range(0, FileCount).Select(Name));
        _targetFiles.ReadAllBytes($"{Version}/f07.bin").Should().Equal(Enumerable.Repeat((byte)7, 107));

        // Progress from many workers still only grows, and ends at the totals.
        var copying = reports.Where(r => r.Phase == BackupPhase.Copying).ToList();
        copying.Select(r => r.FilesDone).Should().BeInAscendingOrder();
        copying.Select(r => r.BytesDone).Should().BeInAscendingOrder();
        copying.Should().OnlyContain(r => r.FilesTotal == FileCount && r.BytesTotal == TotalBytes && r.BytesDone <= TotalBytes);
        var finishing = reports.Single(r => r.Phase == BackupPhase.Finishing);
        finishing.FilesDone.Should().Be(FileCount);
        finishing.BytesDone.Should().Be(TotalBytes);
    }

    [Fact]
    public async Task Folder_to_folder_copies_one_file_at_a_time()
    {
        // Each open waits a moment for a second one to come while it is in flight; with one copy at a time none comes.
        var secondOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GatedStorage source = null!;
        source = new GatedStorage(_sourceFiles)
        {
            BeforeOpen = async (_, ct) =>
            {
                if (source.InFlight > 1)
                    secondOpen.TrySetResult();
                try
                {
                    await secondOpen.Task.WaitAsync(TimeSpan.FromMilliseconds(25), ct);
                }
                catch (TimeoutException)
                {
                    // No second open came: as it should be.
                }
            },
        };

        var entry = await Run("fs", "fs", source, _targetFiles);

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        secondOpen.Task.IsCompleted.Should().BeFalse("no file is opened while another is copied");
        source.MaxInFlight.Should().Be(1);
        Manifest().Files.Select(f => f.Path).Should().Equal(Enumerable.Range(0, FileCount).Select(Name));
    }

    [Fact]
    public async Task Manifest_order_is_the_index_order_whatever_order_the_copies_finish_in()
    {
        // Eight files, eight workers: each copy commits only after the next file's copy did, so they finish in reverse.
        for (var i = 8; i < FileCount; i++)
            await _sourceFiles.DeleteAsync([Name(i)], CancellationToken.None);
        var committed = Enumerable.Range(0, 8)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var target = new GatedStorage(_targetFiles)
        {
            BeforeCommit = async (path, ct) =>
            {
                if (IsCopy(path) && IndexOf(path) < 7)
                    await committed[IndexOf(path) + 1].Task.WaitAsync(Guard, ct);
            },
            AfterCommit = path =>
            {
                if (IsCopy(path))
                    committed[IndexOf(path)].TrySetResult();
            },
        };

        var entry = await Run("mem", "s3", _sourceFiles, target);

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        target.Commits.Where(IsCopy).Select(IndexOf).Should().Equal(7, 6, 5, 4, 3, 2, 1, 0);
        Manifest().Files.Select(f => f.Path).Should().Equal(Enumerable.Range(0, 8).Select(Name));
    }

    [Fact]
    public async Task A_failing_file_is_skipped_and_logged_while_the_others_finish()
    {
        var allHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = HoldUntil(BackupRunner.ParallelS3Copies, _ => true, allHeld);
        var source = new GatedStorage(_sourceFiles)
        {
            BeforeOpen = async (path, ct) =>
            {
                await hold(path, ct);
                if (path == Name(3))
                    throw new StorageLockedException(path);
                if (path == Name(12))
                    throw new StorageNotFoundException(path);
            },
        };

        var entry = await Run("s3", "mem", source, _targetFiles);

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings, entry.Reason);
        entry.Skipped.Should().Equal(
            new SkippedEntry(Name(3), CoreTexts.English("core.file.locked"), "core.file.locked"),
            new SkippedEntry(Name(12), CoreTexts.English("core.file.noLongerExists"), "core.file.noLongerExists"));
        entry.FilesCopied.Should().Be(FileCount - 2);
        Manifest().Files.Select(f => f.Path).Should().Equal(
            Enumerable.Range(0, FileCount).Where(i => i is not (3 or 12)).Select(Name));
        _targetFiles.Files.Count(IsCopy).Should().Be(FileCount - 2);
    }

    [Fact]
    public async Task Archived_objects_are_skipped_with_warnings()
    {
        var source = new GatedStorage(_sourceFiles)
        {
            BeforeOpen = (path, _) => path is "f04.bin" or "f15.bin"
                ? throw new StorageIOException(path, $"'{path}' is archived (Glacier or Deep Archive) and must be restored first.")
                {
                    Archived = true,
                }
                : Task.CompletedTask,
        };

        var entry = await Run("s3", "mem", source, _targetFiles);

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings, entry.Reason);
        entry.Skipped.Should().Equal(
            new SkippedEntry("f04.bin", CoreTexts.English("core.file.archived"), "core.file.archived"),
            new SkippedEntry("f15.bin", CoreTexts.English("core.file.archived"), "core.file.archived"));
        entry.FilesCopied.Should().Be(FileCount - 2);
        _targetFiles.Files.Should().NotContain([$"{Version}/f04.bin", $"{Version}/f15.bin"]);
        _targetFiles.Files.Count(IsCopy).Should().Be(FileCount - 2);
    }

    [Fact]
    public async Task Not_readable_files_are_skipped_with_their_message()
    {
        const string Message = "'f07.bin' is a Google file larger than the 10 MB export limit.";
        var source = new GatedStorage(_sourceFiles)
        {
            BeforeOpen = (path, _) => path == "f07.bin"
                ? throw new StorageIOException(path, Message) { NotReadable = true }
                : Task.CompletedTask,
        };

        var entry = await Run("s3", "mem", source, _targetFiles);

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings, entry.Reason);
        entry.Skipped.Should().Equal(new SkippedEntry("f07.bin", Message));
        entry.FilesCopied.Should().Be(FileCount - 1);
        _targetFiles.Files.Should().NotContain($"{Version}/f07.bin");
    }

    [Fact]
    public async Task Storage_failure_stops_all_workers_and_cleans_up()
    {
        // Eight uploads are in flight when the connection drops for one of them.
        var allHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = HoldUntil(BackupRunner.ParallelS3Copies, IsCopy, allHeld);
        var canceledWaits = 0;
        var target = new GatedStorage(_targetFiles)
        {
            BeforeCommit = async (path, ct) =>
            {
                if (!IsCopy(path))
                    return;
                try
                {
                    await hold(path, ct);
                    if (path == $"{Version}/f00.bin")
                        throw new StorageUnavailableException(path);
                    await new TaskCompletionSource().Task.WaitAsync(Guard, ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref canceledWaits);
                    throw;
                }
            },
        };
        var source = new GatedStorage(_sourceFiles);

        var entry = await Run("mem", "s3", source, target);

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be(new StorageUnavailableException("").Message);
        canceledWaits.Should().Be(7, "the other seven uploads are stopped");
        source.Opens.Should().Be(8, "no file is started after the failure");
        target.Aborted.Should().Be(8, "every upload in flight is aborted");
        target.Commits.Where(IsCopy).Should().BeEmpty();
        _targetFiles.Files.Should().BeEmpty("the unfinished version is removed, its pending marker last");
    }

    /// <summary>
    /// The source refuses or cannot be reached for the first file: an unreachable source stops at once, a refusal stops
    /// when the source's root is refused (or unreachable) too, as with revoked credentials.
    /// </summary>
    [Theory]
    [InlineData(typeof(StorageUnavailableException), null)]
    [InlineData(typeof(StorageAccessDeniedException), typeof(StorageAccessDeniedException))]
    [InlineData(typeof(StorageAccessDeniedException), typeof(StorageUnavailableException))]
    public async Task Storage_failure_opening_a_source_file_stops_all_workers(Type failureType, Type? rootFailureType)
    {
        // Eight downloads are starting when the bucket becomes unreachable (or refuses) for one of them.
        var allHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = HoldUntil(BackupRunner.ParallelS3Copies, _ => true, allHeld);
        var canceledWaits = 0;
        var failed = 0;
        var failure = (StorageException)Activator.CreateInstance(failureType, Name(0), null, null)!;
        var source = new GatedStorage(_sourceFiles)
        {
            BeforeOpen = async (path, ct) =>
            {
                try
                {
                    await hold(path, ct);
                    if (path == Name(0))
                    {
                        Volatile.Write(ref failed, 1);
                        throw failure;
                    }
                    await new TaskCompletionSource().Task.WaitAsync(Guard, ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref canceledWaits);
                    throw;
                }
            },
            BeforeStat = (path, _) => path.Length == 0 && rootFailureType is not null && Volatile.Read(ref failed) == 1
                ? throw (StorageException)Activator.CreateInstance(rootFailureType, "", null, null)!
                : Task.CompletedTask,
        };

        var entry = await Run("s3", "mem", source, _targetFiles);

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be(failure.Message);
        canceledWaits.Should().Be(7, "the other seven downloads are stopped");
        source.Opens.Should().Be(8, "no file is started after the failure");
        entry.Skipped.Should().BeEmpty("the file is not skipped: the run stops");
        _targetFiles.Files.Should().BeEmpty("the unfinished version is removed");
    }

    [Fact]
    public async Task A_denied_file_of_a_folder_source_is_skipped_while_the_others_are_uploaded()
    {
        // One file of the folder is protected by its ACL (or one object is encrypted with a key the user may not use):
        // the source itself answers, so the file is skipped instead of failing the run forever.
        using var tmp = new TempDir();
        var plan = new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(tmp.CreateDir("source")),
            Target = new StorageLocation("s3", "", "conn-2"),
        };
        var source = new GatedStorage(_sourceFiles)
        {
            BeforeOpen = (path, _) => path == Name(5) ? throw new StorageAccessDeniedException(path) : Task.CompletedTask,
        };
        var storages = new TestStorageFactory(location => location == plan.Source ? source : _targetFiles);

        var entry = await new BackupRunner(storages, _time, host: "TESTHOST")
            .RunAsync(new BackupRequest(plan, [], RunTrigger.Manual));

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings, entry.Reason);
        entry.Skipped.Should().Equal(new SkippedEntry(Name(5), CoreTexts.English("core.file.accessDenied"), "core.file.accessDenied"));
        entry.FilesCopied.Should().Be(FileCount - 1);
        Manifest().Files.Select(f => f.Path).Should().Equal(Enumerable.Range(0, FileCount).Where(i => i != 5).Select(Name));
    }

    [Fact]
    public async Task A_failure_after_the_cancellation_still_ends_as_canceled()
    {
        // The uploads in flight answer the cancellation with a storage error (as an aborted connection can).
        var allHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = HoldUntil(BackupRunner.ParallelS3Copies, IsCopy, allHeld);
        var target = new GatedStorage(_targetFiles)
        {
            BeforeCommit = async (path, ct) =>
            {
                if (!IsCopy(path))
                    return;
                try
                {
                    await hold(path, ct);
                    await new TaskCompletionSource().Task.WaitAsync(Guard, ct);
                }
                catch (OperationCanceledException)
                {
                    throw new StorageUnavailableException(path);
                }
            },
        };
        using var cancel = new CancellationTokenSource();

        var run = Run("mem", "s3", _sourceFiles, target, cancellationToken: cancel.Token);
        await allHeld.Task.WaitAsync(Guard);
        cancel.Cancel();
        var entry = await run.WaitAsync(Guard);

        entry.Status.Should().Be(RunStatus.Canceled, entry.Reason);
        target.Aborted.Should().Be(8);
        _targetFiles.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_stops_all_workers_promptly()
    {
        var allHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = HoldUntil(BackupRunner.ParallelS3Copies, IsCopy, allHeld);
        var canceledWaits = 0;
        var target = new GatedStorage(_targetFiles)
        {
            BeforeCommit = async (path, ct) =>
            {
                if (!IsCopy(path))
                    return;
                try
                {
                    await hold(path, ct);
                    await new TaskCompletionSource().Task.WaitAsync(Guard, ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref canceledWaits);
                    throw;
                }
            },
        };
        var source = new GatedStorage(_sourceFiles);
        using var cancel = new CancellationTokenSource();

        var run = Run("mem", "s3", source, target, cancellationToken: cancel.Token);
        await allHeld.Task.WaitAsync(Guard);
        cancel.Cancel();
        var entry = await run.WaitAsync(Guard);

        entry.Status.Should().Be(RunStatus.Canceled);
        canceledWaits.Should().Be(8);
        source.Opens.Should().Be(8, "no file is started after the cancellation");
        target.Aborted.Should().Be(8, "every upload in flight is aborted");
        _targetFiles.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task No_free_space_check_when_the_target_cannot_tell()
    {
        _targetFiles.FreeSpace = 0;   // would stop the run, were it asked
        var target = new GatedStorage(_targetFiles, StorageCapabilities.CaseSensitive);

        var entry = await Run("mem", "s3", _sourceFiles, target);

        entry.Status.Should().Be(RunStatus.Completed, entry.Reason);
        target.FreeSpaceQueries.Should().Be(0);
    }
}
