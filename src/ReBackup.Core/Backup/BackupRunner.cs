using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;
using ReBackup.Core.Json;
using ReBackup.Core.Plans;
using ReBackup.Core.Retention;
using ReBackup.Core.Versions;

namespace ReBackup.Core.Backup;

/// <summary>
/// CreatingFolders counts folders in FilesDone/FilesTotal. CleaningUp: the run did not finish and the incomplete copy
/// is being removed.
/// </summary>
public enum BackupPhase { Indexing, CreatingFolders, Copying, Finishing, CleaningUp, Retention }

public readonly record struct BackupProgress(
    BackupPhase Phase, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentFile)
{
    /// <summary>0..1, by bytes; by files when there are no bytes to copy.</summary>
    public double Fraction =>
        BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1)
        : FilesTotal > 0 ? Math.Clamp((double)FilesDone / FilesTotal, 0, 1)
        : Phase is BackupPhase.Finishing or BackupPhase.Retention ? 1 : 0;
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
    private readonly Func<string, IReadOnlyList<RetentionRule>?>? _currentRules;
    private readonly IVersionIndexSink? _indexSink;

    /// <param name="volume">The target's file system; the real one when null.</param>
    /// <param name="timeProvider">The clock; the system clock when null.</param>
    /// <param name="currentRules">
    /// Gives the retention rules of a plan (by its id) as they are saved right now; null when the plan no longer
    /// exists. A run can take hours and the rules can be changed meanwhile, so versions are deleted by these rules
    /// and not by those of the request. Without it, the rules of the request are used.
    /// </param>
    /// <param name="indexSink">
    /// Gets the manifest of every version a run finishes, for the plan's version index. Its failures become warnings.
    /// It is called on the run's path, so it must return quickly (the app hands the work to a
    /// <see cref="VersionIndexWorker"/>).
    /// </param>
    public BackupRunner(ITargetVolume? volume = null, TimeProvider? timeProvider = null,
        Func<string, IReadOnlyList<RetentionRule>?>? currentRules = null, IVersionIndexSink? indexSink = null)
    {
        _volume = volume ?? new PhysicalTargetVolume();
        _time = timeProvider ?? TimeProvider.System;
        _currentRules = currentRules;
        _indexSink = indexSink;
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
        string? finalPath = null;
        BackupManifest? manifest = null;
        var completed = false;
        try
        {
            var work = await Task.Run(() => Prepare(request, entry, progress, cancellationToken), cancellationToken);
            foreach (var skipped in work.Skipped)
                entry.AddSkipped(skipped);

            var versionName = await ReserveVersionNameAsync(plan, cancellationToken);
            var final = finalPath = Path.Combine(plan.Target, versionName);
            partialPath = finalPath + VersionName.PartialSuffix;
            var partial = partialPath;
            manifest = await Task.Run(() => CopyAndFinish(work, plan, partial, final, entry, progress, cancellationToken),
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
        {
            progress?.Report(new BackupProgress(BackupPhase.CleaningUp, 0, 0, 0, 0, ""));
            TryDeleteDirectory(partialPath);
        }

        if (completed && _indexSink is not null && manifest is not null && finalPath is not null)
            await Task.Run(() => AddToIndex(plan, entry, finalPath, manifest));

        if (completed)
        {
            try
            {
                await Task.Run(() => ApplyRetention(plan, entry, progress, cancellationToken));
            }
            catch (Exception ex)
            {
                // The backup itself is done; whatever goes wrong here must not turn it into a failure.
                entry.Warnings.Add($"Retention was skipped: {ex.Message}");
            }
        }

        entry.DurationMs = (long)_time.GetElapsedTime(started).TotalMilliseconds;
        entry.EndUtc = entry.StartUtc.AddMilliseconds(entry.DurationMs);
        return entry;
    }

    /// <summary>
    /// Index, evaluate and preflight. Copies nothing yet, but changes the target: it creates the target folder,
    /// removes leftovers of earlier runs and, when the plan allows it and the space is short, deletes old versions
    /// to make room.
    /// </summary>
    private BackupWork Prepare(BackupRequest request, RunLogEntry entry, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
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
        if (plan.Name.EndsWith(VersionName.DeletingSuffix, StringComparison.OrdinalIgnoreCase))
            throw new BackupAbortException(RunStatus.Error, "The plan name must not end with \".deleting\".");
        if (PlanValidator.NameErrors(plan.Name) is [var nameProblem, ..])
            throw new BackupAbortException(RunStatus.Error, $"The plan name \"{plan.Name}\" cannot be used: {nameProblem}");
        if (PathUtil.IsSameOrInside(plan.Target, plan.Source))
            throw new BackupAbortException(RunStatus.Error, "The target folder is the source folder or inside it.");
        if (PathUtil.IsSameOrInside(plan.Source, plan.Target))
            throw new BackupAbortException(RunStatus.Error, "The source folder is inside the target folder.");

        Directory.CreateDirectory(plan.Target);
        DeleteLeftovers(plan, entry);

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
        if (required > free && plan.FreeSpaceByRetention)
            free = FreeSpaceByRetention(plan, required, free, entry, cancellationToken);
        if (required > free)
        {
            throw new BackupAbortException(RunStatus.Full,
                $"The backup needs {ByteSize.Format(required)} but only {ByteSize.Format(free)} is free on the target.");
        }
        return work;
    }

    /// <summary>
    /// Makes room by deleting versions that retention would delete after this run anyway, oldest first. Nothing is
    /// deleted unless that can make the run fit, and the newest existing version always stays. Returns the free space.
    /// </summary>
    private long FreeSpaceByRetention(BackupPlan plan, long required, long free, RunLogEntry entry,
        CancellationToken cancellationToken)
    {
        if (ResolveRules(plan, entry, "Old versions were not deleted to free space") is not { Count: > 0 } rules)
            return free;

        List<VersionInfo> candidates;
        try
        {
            var versions = VersionCatalog.List(plan.Target, plan.Id, plan.Name, cancellationToken);
            var newest = versions.LastOrDefault(v => v.IsOwned);
            candidates = RetentionPlanner.Decide(versions, rules, upcomingRun: _time.GetLocalNow().DateTime)
                .Where(d => d.Delete && !ReferenceEquals(d.Version, newest))
                .Select(d => d.Version)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            entry.Warnings.Add($"Old versions could not be examined to free space: {ex.Message}");
            return free;
        }

        if (free + candidates.Sum(v => v.TotalBytes ?? 0) < required)
            return free;

        foreach (var version in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryRemoveVersion(version, entry, $"\"{version.Name}\" could not be deleted to free space"))
                continue;

            free = _volume.GetAvailableFreeSpace(plan.Target);
            if (required <= free)
                break;
        }
        return free;
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

    /// <summary>Removes what earlier runs of this plan left behind: unfinished ".partial" folders and ".deleting" remains.</summary>
    private void DeleteLeftovers(BackupPlan plan, RunLogEntry entry)
    {
        foreach (var directory in Directory.EnumerateDirectories(plan.Target).ToList())
        {
            var name = Path.GetFileName(directory);
            if (!VersionName.IsTransient(name))
                continue;
            if (new DirectoryInfo(directory).LinkTarget is not null)
                continue;   // never follow a link out of the target

            if (name.EndsWith(VersionName.PartialSuffix, StringComparison.OrdinalIgnoreCase))
            {
                if (!VersionName.TryParse(name[..^VersionName.PartialSuffix.Length], plan.Name, out _))
                    continue;
                // A manifest is written last, right before the rename: such a folder is a finished version of a
                // plan whose name ends in ".partial" (or a crash just before the rename). Leaving it is the safe
                // choice, and so is leaving a folder that cannot be examined.
                if (HasManifest(directory) != false)
                    continue;
                TryDeleteDirectory(directory);
            }
            else if (name.EndsWith(VersionName.DeletingSuffix, StringComparison.OrdinalIgnoreCase))
            {
                if (!VersionName.TryParseAny(name[..^VersionName.DeletingSuffix.Length], out _, out var folderPlanName) ||
                    !IsOwnRemains(directory, plan, folderPlanName))
                    continue;
                try
                {
                    VersionRemover.RemoveRemains(directory, _volume);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    entry.Warnings.Add($"Remains of an earlier removal could not be deleted (\"{name}\"): {ex.Message}");
                }
            }
        }
    }

    /// <summary>True or false when it is known; null when the folder cannot be examined.</summary>
    private static bool? HasManifest(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, VersionName.ManifestFileName).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>False when the folder holds anything or cannot be examined.</summary>
    private static bool IsEmpty(string directory)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Remains belong to the plan by their manifest. The manifest is deleted last, so remains without one that still
    /// hold files are not ours by construction; empty ones are removed when the name matches. Everything else is
    /// left alone, also remains of a folder that was copied or renamed by hand
    /// (<see cref="VersionOwnership.Renamed"/>): retention never renamed that folder, a person did.
    /// </summary>
    private static bool IsOwnRemains(string directory, BackupPlan plan, string folderPlanName) =>
        VersionCatalog.Probe(directory, plan.Id).Ownership switch
        {
            VersionOwnership.Owned => true,
            VersionOwnership.NoManifest =>
                HasManifest(directory) == false && folderPlanName.Equals(plan.Name, StringComparison.OrdinalIgnoreCase) &&
                IsEmpty(directory),
            _ => false,
        };

    /// <summary>Hands the finished version to the version index. Whatever goes wrong there is a warning only.</summary>
    private void AddToIndex(BackupPlan plan, RunLogEntry entry, string finalPath, BackupManifest manifest)
    {
        try
        {
            var name = Path.GetFileName(finalPath);
            VersionName.TryParseAny(name, out var localTime, out _);
            var version = new VersionInfo(name, finalPath, localTime, VersionOwnership.Owned,
                manifest.FileCount, manifest.TotalBytes);
            _indexSink!.Add(plan.Id, version, manifest);
        }
        catch (Exception ex)
        {
            entry.Warnings.Add($"The version index could not be updated: {ex.Message}");
        }
    }

    /// <summary>Deletes the versions the plan's rules no longer keep. Problems become warnings; the run stays successful.</summary>
    private void ApplyRetention(BackupPlan plan, RunLogEntry entry, IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (ResolveRules(plan, entry, "Retention was skipped") is not { Count: > 0 } rules)
            return;

        progress?.Report(new BackupProgress(BackupPhase.Retention, entry.FilesCopied, entry.FilesCopied,
            entry.BytesCopied, entry.BytesCopied, ""));

        List<VersionInfo> doomed;
        try
        {
            var versions = VersionCatalog.List(plan.Target, plan.Id, plan.Name);
            doomed = RetentionPlanner.Decide(versions, rules).Where(d => d.Delete).Select(d => d.Version).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            entry.Warnings.Add($"Retention was skipped: {ex.Message}");
            return;
        }

        foreach (var version in doomed)
        {
            if (cancellationToken.IsCancellationRequested)
                return;   // the next successful run deletes the rest
            if (version.Name.Equals(entry.Version, StringComparison.OrdinalIgnoreCase))
                continue;   // never the version this run just made, whatever the clock or the rules say

            TryRemoveVersion(version, entry, $"Retention could not delete \"{version.Name}\"");
        }
    }

    /// <summary>
    /// The rules a deletion pass applies. A run can take hours and the rules can be changed and saved meanwhile, so
    /// the rules saved right now count when the runner can look them up; otherwise those of the request. Null
    /// means: delete nothing in this pass. The reason is then added as a warning that starts with
    /// <paramref name="skippedText"/>.
    /// </summary>
    private IReadOnlyList<RetentionRule>? ResolveRules(BackupPlan plan, RunLogEntry entry, string skippedText)
    {
        if (_currentRules is null)
            return plan.Retention;

        IReadOnlyList<RetentionRule>? rules;
        try
        {
            rules = _currentRules(plan.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            entry.Warnings.Add($"{skippedText}: the plan's current rules could not be read: {ex.Message}");
            return null;
        }

        if (rules is null)
            entry.Warnings.Add($"{skippedText}: the plan no longer exists.");
        return rules;
    }

    /// <summary>
    /// Removes one version. True when the folder no longer is a version: it is gone, or it was renamed and only
    /// its remains are left (the next run of the plan removes them). False when nothing was changed; that is also
    /// the answer when the folder or the whole target has vanished, because nothing was deleted then.
    /// </summary>
    private bool TryRemoveVersion(VersionInfo version, RunLogEntry entry, string failurePrefix)
    {
        try
        {
            VersionRemover.Remove(version.Path, _volume);
        }
        catch (VersionRemainsException ex)
        {
            entry.Warnings.Add($"\"{version.Name}\" was removed from the versions, but its remains could not be deleted yet: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entry.Warnings.Add($"{failurePrefix}: {ex.Message}");
            return false;
        }
        entry.RetentionDeleted.Add(version.Name);
        return true;
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

    private BackupManifest CopyAndFinish(BackupWork work, BackupPlan plan, string partialPath, string finalPath,
        RunLogEntry entry, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        // Creating many folders on a network share takes a while, so it reports progress of its own.
        var foldersTotal = work.Directories.Count;
        var foldersDone = 0;
        var sinceFolderReport = Stopwatch.StartNew();
        progress?.Report(new BackupProgress(BackupPhase.CreatingFolders, 0, foldersTotal, 0, 0, ""));
        Directory.CreateDirectory(partialPath);
        foreach (var directory in work.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(partialPath, ToLocalPath(directory.RelativePath)));
            foldersDone++;
            if (progress is not null && (sinceFolderReport.Elapsed >= ProgressInterval || foldersDone == foldersTotal))
            {
                sinceFolderReport.Restart();
                progress.Report(new BackupProgress(BackupPhase.CreatingFolders, foldersDone, foldersTotal, 0, 0,
                    directory.RelativePath));
            }
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
        return manifest;
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
