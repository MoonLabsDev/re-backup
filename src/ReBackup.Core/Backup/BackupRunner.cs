using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;
using ReBackup.Core.Json;
using ReBackup.Core.Plans;

namespace ReBackup.Core.Backup;

public enum BackupPhase { Indexing, Copying, Finishing }

public readonly record struct BackupProgress(
    BackupPhase Phase, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentFile)
{
    /// <summary>0..1, by bytes; by files when there are no bytes to copy.</summary>
    public double Fraction =>
        BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1)
        : FilesTotal > 0 ? Math.Clamp((double)FilesDone / FilesTotal, 0, 1)
        : Phase == BackupPhase.Finishing ? 1 : 0;
}

/// <summary>A plan as saved, the global ignore defaults at the time of the request, and why the run starts.</summary>
public sealed record BackupRequest(BackupPlan Plan, IReadOnlyList<string> GlobalIgnoreDefaults, RunTrigger Trigger);

public interface IBackupRunner
{
    /// <summary>Runs one backup. Never throws: every outcome, including cancellation, is returned as an entry.</summary>
    Task<RunLogEntry> RunAsync(BackupRequest request, IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class BackupRunner : IBackupRunner
{
    /// <summary>The target must have room for the included bytes times this factor.</summary>
    public const double FreeSpaceMargin = 1.05;

    private const int BufferSize = 1024 * 1024;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorDiskFull = 112;
    private const int MoveAttempts = 5;
    private const string NoLongerExists = "no longer exists";
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly ITargetVolume _volume;
    private readonly TimeProvider _time;

    public BackupRunner(ITargetVolume? volume = null, TimeProvider? timeProvider = null)
    {
        _volume = volume ?? new PhysicalTargetVolume();
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<RunLogEntry> RunAsync(BackupRequest request, IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = request.Plan;
        var started = _time.GetTimestamp();
        var entry = new RunLogEntry
        {
            RunId = Guid.NewGuid().ToString("N"),
            Trigger = request.Trigger,
            StartUtc = _time.GetUtcNow().UtcDateTime,
        };

        string? partialPath = null;
        var completed = false;
        try
        {
            var work = await Task.Run(() => Prepare(request, progress, cancellationToken), cancellationToken);
            foreach (var skipped in work.Skipped)
                entry.AddSkipped(skipped);

            var versionName = await ReserveVersionNameAsync(plan, cancellationToken);
            var finalPath = Path.Combine(plan.Target, versionName);
            partialPath = finalPath + VersionName.PartialSuffix;
            var partial = partialPath;
            await Task.Run(() => CopyAndFinish(work, plan, partial, finalPath, entry, progress, cancellationToken),
                cancellationToken);

            entry.Version = versionName;
            entry.Status = entry.SkippedCount > 0 ? RunStatus.CompletedWithWarnings : RunStatus.Completed;
            completed = true;
        }
        catch (OperationCanceledException)
        {
            entry.Status = RunStatus.Canceled;
        }
        catch (BackupAbortException ex)
        {
            entry.Status = ex.Status;
            entry.Reason = ex.Message;
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            entry.Status = RunStatus.Full;
            entry.Reason = "The target ran out of space during the backup.";
        }
        catch (Exception ex)
        {
            entry.Status = RunStatus.Error;
            entry.Reason = ex.Message;
        }

        if (!completed && partialPath is not null)
            TryDeleteDirectory(partialPath);

        entry.DurationMs = (long)_time.GetElapsedTime(started).TotalMilliseconds;
        entry.EndUtc = entry.StartUtc.AddMilliseconds(entry.DurationMs);
        return entry;
    }

    /// <summary>Index, evaluate and preflight. Writes nothing except creating the target folder and removing leftovers.</summary>
    private BackupWork Prepare(BackupRequest request, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        var plan = request.Plan;
        if (string.IsNullOrWhiteSpace(plan.Source) || !Directory.Exists(plan.Source))
            throw new BackupAbortException(RunStatus.Error, $"Source folder \"{plan.Source}\" does not exist.");
        if (string.IsNullOrWhiteSpace(plan.Target))
            throw new BackupAbortException(RunStatus.Error, "No target folder is set.");
        if (string.IsNullOrWhiteSpace(plan.Name) || plan.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new BackupAbortException(RunStatus.Error, $"The plan name \"{plan.Name}\" cannot be used as a folder name.");
        if (plan.Name.EndsWith(VersionName.PartialSuffix, StringComparison.OrdinalIgnoreCase))
            throw new BackupAbortException(RunStatus.Error, "The plan name must not end with \".partial\".");
        if (PathUtil.IsSameOrInside(plan.Target, plan.Source))
            throw new BackupAbortException(RunStatus.Error, "The target folder is the source folder or inside it.");
        if (PathUtil.IsSameOrInside(plan.Source, plan.Target))
            throw new BackupAbortException(RunStatus.Error, "The source folder is inside the target folder.");

        Directory.CreateDirectory(plan.Target);
        DeleteLeftovers(plan);

        var indexProgress = progress is null ? null : new IndexProgressAdapter(progress);
        var index = SourceIndexer.Build(plan.Source, indexProgress, cancellationToken);
        var matcher = IgnoreMatcher.ForPlan(plan.Ignore, request.GlobalIgnoreDefaults, index.IgnoreFiles);
        var root = IndexEvaluator.Evaluate(index, matcher, cancellationToken);
        if (root.Node.Error is { } rootError)
            throw new BackupAbortException(RunStatus.Error, $"The source folder could not be read: {rootError}");

        var work = new BackupWork(index.Root);
        foreach (var path in index.UnreadableIgnoreFiles)
            work.Skipped.Add(new SkippedEntry(path, "ignore file could not be read; its patterns were not applied"));
        Collect(root, work);

        var required = (long)Math.Ceiling(work.TotalBytes * FreeSpaceMargin);
        var free = _volume.GetAvailableFreeSpace(plan.Target);
        if (required > free)
        {
            throw new BackupAbortException(RunStatus.Full,
                $"The backup needs {ByteSize.Format(required)} but only {ByteSize.Format(free)} is free on the target.");
        }
        return work;
    }

    private static void Collect(EvaluatedNode node, BackupWork work)
    {
        if (node.Status == IncludeStatus.Ignored)
            return;

        var path = node.Node.RelativePath;
        if (!node.Node.IsDirectory)
        {
            if (path.Equals(VersionName.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                work.Skipped.Add(new SkippedEntry(path, "the name is reserved for the backup manifest"));
                return;
            }
            work.Files.Add(node.Node);
            work.TotalBytes += node.Node.Size;
            return;
        }

        if (path.Equals(VersionName.ManifestFileName, StringComparison.OrdinalIgnoreCase))
        {
            work.Skipped.Add(new SkippedEntry(path, "the name is reserved for the backup manifest"));
            return;
        }

        if (path.Length > 0)
            work.Directories.Add(node.Node);
        if (node.Node.Error is { } error)
            work.Skipped.Add(new SkippedEntry(path, error));
        foreach (var child in node.Children)
            Collect(child, work);
    }

    private void DeleteLeftovers(BackupPlan plan)
    {
        foreach (var directory in Directory.EnumerateDirectories(plan.Target, "*" + VersionName.PartialSuffix))
        {
            var name = Path.GetFileName(directory);
            var versionName = name[..^VersionName.PartialSuffix.Length];
            if (!VersionName.TryParse(versionName, plan.Name, out _))
                continue;
            // A manifest is written last, right before the rename: such a folder is a finished version of a plan
            // whose name ends in ".partial" (or a crash just before the rename). Leaving it is the safe choice.
            if (File.Exists(Path.Combine(directory, VersionName.ManifestFileName)))
                continue;
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>Picks the folder name for the current minute; waits for the next minute if that name is taken.</summary>
    private async Task<string> ReserveVersionNameAsync(BackupPlan plan, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _time.GetLocalNow().DateTime;
            var name = VersionName.Format(now, plan.Name);
            var path = Path.Combine(plan.Target, name);
            if (!Directory.Exists(path) && !Directory.Exists(path + VersionName.PartialSuffix))
                return name;

            var nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(1);
            await Task.Delay(nextMinute - now + TimeSpan.FromMilliseconds(50), _time, cancellationToken);
        }
    }

    private void CopyAndFinish(BackupWork work, BackupPlan plan, string partialPath, string finalPath,
        RunLogEntry entry, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(partialPath);
        foreach (var directory in work.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(partialPath, ToLocalPath(directory.RelativePath)));
        }

        var manifest = new BackupManifest
        {
            PlanId = plan.Id,
            PlanName = plan.Name,
            CreatedUtc = _time.GetUtcNow().UtcDateTime,
            Source = work.SourceRoot,
        };

        var buffer = new byte[BufferSize];
        var sinceReport = Stopwatch.StartNew();
        var filesDone = 0;
        long bytesDone = 0;

        void Report(BackupPhase phase, string currentFile, bool force)
        {
            if (progress is null || (!force && sinceReport.Elapsed < ProgressInterval))
                return;
            sinceReport.Restart();
            progress.Report(new BackupProgress(phase, filesDone, work.Files.Count, bytesDone, work.TotalBytes, currentFile));
        }

        Report(BackupPhase.Copying, "", force: true);
        foreach (var file in work.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesBefore = bytesDone;
            var localPath = ToLocalPath(file.RelativePath);
            var copied = CopyFile(Path.Combine(work.SourceRoot, localPath), Path.Combine(partialPath, localPath), file,
                buffer, count =>
                {
                    bytesDone += count;
                    Report(BackupPhase.Copying, file.RelativePath, force: false);
                }, cancellationToken, out var skipReason, out var changed);

            if (copied is null)
            {
                if (skipReason == NoLongerExists && !Directory.Exists(work.SourceRoot))
                    throw new BackupAbortException(RunStatus.Error, "The source folder is no longer available.");
                entry.AddSkipped(new SkippedEntry(file.RelativePath, skipReason!));
            }
            else
            {
                if (changed)
                {
                    entry.AddSkipped(new SkippedEntry(file.RelativePath,
                        "changed while it was copied; the copy may be inconsistent"));
                }
                manifest.Files.Add(copied);
                entry.FilesCopied++;
                entry.BytesCopied += copied.Size;
            }

            filesDone++;
            bytesDone = bytesBefore + file.Size;   // keeps the bar moving for skipped or changed files
            Report(BackupPhase.Copying, file.RelativePath, force: false);
        }

        // The last report comes before the rename: a throwing progress callback must not undo a finished backup.
        Report(BackupPhase.Finishing, "", force: true);
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        using (var stream = File.Create(Path.Combine(partialPath, VersionName.ManifestFileName)))
        {
            JsonSerializer.Serialize(stream, manifest, JsonDefaults.Options);
            stream.Flush(flushToDisk: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        MoveWithRetry(partialPath, finalPath);
    }

    /// <summary>Antivirus, indexers or Explorer can briefly hold a handle inside the folder; retry before giving up.</summary>
    private void MoveWithRetry(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _volume.MoveDirectory(source, destination);
                return;
            }
            catch (Exception ex) when (attempt < MoveAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(MoveRetryDelay * attempt);
            }
        }
    }

    /// <summary>Copies one file while hashing it. Returns null and a reason when the source cannot be read; target errors propagate.</summary>
    private ManifestFile? CopyFile(string sourcePath, string targetPath, IndexNode node, byte[] buffer,
        Action<int> onBytes, CancellationToken cancellationToken, out string? skipReason, out bool changed)
    {
        skipReason = null;
        changed = false;
        FileStream input;
        try
        {
            input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                BufferSize, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (SourceSkipReason(ex) is { } reason)
        {
            skipReason = reason;
            return null;
        }

        var hash = new XxHash64();
        long size = 0;
        using (input)
        using (var output = _volume.CreateFile(targetPath))
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read;
                try
                {
                    read = input.Read(buffer, 0, buffer.Length);
                }
                catch (IOException ex) when (IsLocked(ex))
                {
                    skipReason = "locked by another program";
                    break;
                }

                if (read == 0)
                    break;
                hash.Append(buffer.AsSpan(0, read));
                output.Write(buffer, 0, read);
                size += read;
                onBytes(read);
            }
        }

        if (skipReason is not null)
        {
            TryDeleteFile(targetPath);
            return null;
        }

        // The source is opened shared, so it can change while it is read: compare with what was indexed.
        var mtime = node.LastWriteUtc;
        try
        {
            var info = new FileInfo(sourcePath);
            var length = info.Length;
            mtime = info.LastWriteTimeUtc;
            changed = size != node.Size || length != node.Size || mtime != node.LastWriteUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            changed = true;
        }

        try
        {
            File.SetLastWriteTimeUtc(targetPath, mtime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // The copy itself is fine; the manifest still records the source time.
        }

        return new ManifestFile(node.RelativePath, size, mtime,
            "xxh64:" + Convert.ToHexStringLower(hash.GetCurrentHash()));
    }

    private static string? SourceSkipReason(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => NoLongerExists,
        UnauthorizedAccessException => "access denied",
        IOException io when IsLocked(io) => "locked by another program",
        _ => null,
    };

    private static bool IsLocked(IOException exception) =>
        (exception.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    private static bool IsDiskFull(IOException exception) =>
        (exception.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull;

    private static string ToLocalPath(string relativePath) => relativePath.Replace('/', Path.DirectorySeparatorChar);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the next run of the plan removes leftovers.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class BackupWork(string sourceRoot)
    {
        public string SourceRoot { get; } = sourceRoot;
        public List<IndexNode> Directories { get; } = [];
        public List<IndexNode> Files { get; } = [];
        public List<SkippedEntry> Skipped { get; } = [];
        public long TotalBytes { get; set; }
    }

    private sealed class BackupAbortException(RunStatus status, string message) : Exception(message)
    {
        public RunStatus Status { get; } = status;
    }

    private sealed class IndexProgressAdapter(IProgress<BackupProgress> progress) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) =>
            progress.Report(new BackupProgress(BackupPhase.Indexing, value.Files, 0, 0, 0, value.CurrentDirectory));
    }
}
