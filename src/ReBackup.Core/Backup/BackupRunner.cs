using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Retention;
using ReBackup.Core.Versions;
using ReBackup.Shared.IO;
using ReBackup.Shared.Json;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.Core.Backup;

/// <summary>
/// CreatingFolders counts folders in FilesDone/FilesTotal. CleaningUp: the run did not finish and the incomplete copy
/// is being removed. RemovingLeftovers: before indexing, what earlier runs left behind (an unfinished copy, the remains of
/// a deleted version) is removed; FilesDone counts the files removed, CurrentFile is the folder.
/// </summary>
public enum BackupPhase { Indexing, CreatingFolders, Copying, Finishing, CleaningUp, Retention, RemovingLeftovers }

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
    private const int MarkerDeleteAttempts = 5;
    private static readonly string NoLongerExists = CoreTexts.English("core.file.noLongerExists");
    private static readonly TimeSpan MarkerDeleteRetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly IStorageFactory _storages;
    private readonly TimeProvider _time;
    private readonly Func<string, IReadOnlyList<RetentionRule>?>? _currentRules;
    private readonly IVersionIndexSink? _indexSink;
    private readonly string _host;

    /// <param name="storages">
    /// Opens the plan's source and target as storages; everything a run reads and writes goes through them. A
    /// <see cref="StorageFactory"/> when null.
    /// </param>
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
    /// <param name="host">The computer the markers a run writes name; <see cref="Environment.MachineName"/> when null.</param>
    public BackupRunner(IStorageFactory? storages = null, TimeProvider? timeProvider = null,
        Func<string, IReadOnlyList<RetentionRule>?>? currentRules = null, IVersionIndexSink? indexSink = null,
        string? host = null)
    {
        _storages = storages ?? new StorageFactory();
        _time = timeProvider ?? TimeProvider.System;
        _currentRules = currentRules;
        _indexSink = indexSink;
        _host = host ?? Environment.MachineName;
    }

    /// <remarks>
    /// The version is written under its final name (spec 6.1): the name is reserved by creating
    /// <see cref="VersionMarkerNames.Pending"/> in its folder exclusively, then the folders and files are copied and the
    /// manifest is written; only removing the marker makes the folder a version. A run that does not get there removes
    /// what it wrote, the marker last. What it cannot remove keeps the marker, and the next run of the plan removes it
    /// (<see cref="LeftoverCleaner"/>).
    /// </remarks>
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

        BackupWork? work = null;
        string? reserved = null;   // the version folder that holds this run's pending marker
        BackupManifest? manifest = null;
        var completed = false;
        try
        {
            var prepared = work = await Task.Run(() => PrepareAsync(request, entry, progress, cancellationToken), cancellationToken);
            foreach (var skipped in prepared.Skipped)
                entry.AddSkipped(skipped);

            var versionName = reserved = await Task.Run(() => ReserveVersionNameAsync(plan, prepared.Target, cancellationToken),
                cancellationToken);
            manifest = await Task.Run(() => CopyAndFinishAsync(prepared, plan, versionName, entry, progress, cancellationToken),
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
        catch (StorageFullException)
        {
            entry.Status = RunStatus.Full;
            entry.Reason = CoreTexts.English("core.run.targetFull");
        }
        catch (Exception ex)
        {
            entry.Status = RunStatus.Error;
            entry.Reason = ex.Message;
        }

        if (!completed && work is not null && reserved is not null)
        {
            progress?.Report(new BackupProgress(BackupPhase.CleaningUp, 0, 0, 0, 0, ""));
            var target = work.Target;
            var name = reserved;
            await Task.Run(() => RemoveUnfinishedAsync(target, name, entry));
        }

        if (completed && _indexSink is not null && manifest is not null)
            await Task.Run(() => AddToIndex(plan, work!.Target, entry, entry.Version!, manifest));

        if (completed)
        {
            try
            {
                await Task.Run(() => ApplyRetentionAsync(plan, work!.Target, entry, progress, cancellationToken));
            }
            catch (Exception ex)
            {
                // The backup itself is done; whatever goes wrong here must not turn it into a failure.
                entry.Warnings.Add(CoreTexts.English("core.run.retentionFailed", ("error", ex.Message)));
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
    /// <remarks>
    /// The checks that need paths on a file system (the source exists, source and target do not overlap) apply to
    /// <c>"fs"</c> locations only; for other kinds a missing source shows when it is indexed.
    /// </remarks>
    private async Task<BackupWork> PrepareAsync(BackupRequest request, RunLogEntry entry, IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = request.Plan;
        if (string.IsNullOrWhiteSpace(plan.Source.Path) || (plan.Source.IsFileSystem && !Directory.Exists(plan.Source.Path)))
            throw new BackupAbortException(RunStatus.Error, CoreTexts.English("core.run.sourceMissing", ("source", plan.Source.Path)));
        if (string.IsNullOrWhiteSpace(plan.Target.Path))
            throw new BackupAbortException(RunStatus.Error, CoreTexts.English("core.run.noTarget"));
        if (string.IsNullOrWhiteSpace(plan.Name) || plan.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new BackupAbortException(RunStatus.Error, CoreTexts.English("core.run.nameNotFolder", ("name", plan.Name)));
        if (plan.Name.EndsWith(VersionName.PartialSuffix, StringComparison.OrdinalIgnoreCase))
            throw new BackupAbortException(RunStatus.Error, CoreTexts.English("core.run.namePartial"));
        if (plan.Name.EndsWith(VersionName.DeletingSuffix, StringComparison.OrdinalIgnoreCase))
            throw new BackupAbortException(RunStatus.Error, CoreTexts.English("core.run.nameDeleting"));
        if (PlanValidator.NameErrors(plan.Name) is [var nameProblem, ..])
            throw new BackupAbortException(RunStatus.Error,
                CoreTexts.English("core.run.nameUnusable", ("name", plan.Name), ("problem", nameProblem)));
        if (plan.Source.IsFileSystem && plan.Target.IsFileSystem)
        {
            if (PathUtil.IsSameOrInside(plan.Target.Path, plan.Source.Path))
                throw new BackupAbortException(RunStatus.Error, CoreTexts.English("core.run.targetInsideSource"));
            if (PathUtil.IsSameOrInside(plan.Source.Path, plan.Target.Path))
                throw new BackupAbortException(RunStatus.Error, CoreTexts.English("core.run.sourceInsideTarget"));
        }

        var source = _storages.Open(plan.Source);
        var target = _storages.Open(plan.Target);
        await target.EnsureDirectoryAsync("", cancellationToken).ConfigureAwait(false);
        await DeleteLeftoversAsync(target, plan, entry, progress, cancellationToken).ConfigureAwait(false);

        var indexProgress = progress is null ? null : new IndexProgressAdapter(progress);
        SourceIndex index;
        try
        {
            index = await SourceIndexer.BuildAsync(source, indexProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageNotFoundException)
        {
            throw new BackupAbortException(RunStatus.Error, CoreTexts.English("core.run.sourceMissing", ("source", plan.Source.Path)));
        }
        var matcher = IgnoreMatcher.ForPlan(plan.Ignore, request.GlobalIgnoreDefaults, index.IgnoreFiles);
        var root = IndexEvaluator.Evaluate(index, matcher, cancellationToken);
        if (root.Node.Error is { } rootError)
            throw new BackupAbortException(RunStatus.Error, CoreTexts.English("core.run.sourceUnreadable", ("error", rootError)));

        // index.Root is only a display name; the manifest records where the source is.
        var work = new BackupWork(source, target,
            plan.Source.IsFileSystem ? PathUtil.Normalize(plan.Source.Path) : plan.Source.Path);
        foreach (var path in index.UnreadableIgnoreFiles)
            work.Skipped.Add(new SkippedEntry(path, CoreTexts.English("core.skip.ignoreFileUnreadable")));
        Collect(root, work);

        // A storage that cannot tell its free space (a network share, most remote storages) skips the preflight; a
        // full target is still caught while copying.
        if (await target.GetFreeSpaceAsync(cancellationToken).ConfigureAwait(false) is not { } free)
            return work;
        var required = (long)Math.Ceiling(work.TotalBytes * FreeSpaceMargin);
        if (required > free && plan.FreeSpaceByRetention)
            free = await FreeSpaceByRetentionAsync(plan, target, required, free, entry, cancellationToken).ConfigureAwait(false);
        if (required > free)
        {
            throw new BackupAbortException(RunStatus.Full,
                CoreTexts.English("core.run.notEnoughSpace", ("required", ByteSize.Format(required)), ("free", ByteSize.Format(free))));
        }
        return work;
    }

    /// <summary>
    /// Makes room by deleting versions that retention would delete after this run anyway, oldest first. Nothing is
    /// deleted unless that can make the run fit, and the newest existing version always stays. Returns the free space;
    /// <see cref="long.MaxValue"/> when the storage stops telling it.
    /// </summary>
    private async Task<long> FreeSpaceByRetentionAsync(BackupPlan plan, IStorage target, long required, long free,
        RunLogEntry entry, CancellationToken cancellationToken)
    {
        if (ResolveRules(plan, entry, "core.run.freeSpaceRulesUnreadable", "core.run.freeSpacePlanGone") is not { Count: > 0 } rules)
            return free;

        List<VersionInfo> candidates;
        try
        {
            var versions = await VersionCatalog.ListAsync(target, plan.Id, plan.Name, cancellationToken).ConfigureAwait(false);
            var newest = versions.LastOrDefault(v => v.IsOwned);
            candidates = RetentionPlanner.Decide(versions, rules, upcomingRun: _time.GetLocalNow().DateTime)
                .Where(d => d.Delete && !ReferenceEquals(d.Version, newest))
                .Select(d => d.Version)
                .ToList();
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            entry.Warnings.Add(CoreTexts.English("core.run.freeSpaceExamineFailed", ("error", ex.Message)));
            return free;
        }

        if (free + candidates.Sum(v => v.TotalBytes ?? 0) < required)
            return free;

        foreach (var version in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await TryRemoveVersionAsync(plan, target, version, entry, "core.run.freeSpaceDeleteFailed").ConfigureAwait(false))
                continue;

            free = await target.GetFreeSpaceAsync(cancellationToken).ConfigureAwait(false) ?? long.MaxValue;
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
        if (IsReservedRootName(path))
        {
            work.Skipped.Add(new SkippedEntry(path, CoreTexts.English("core.skip.reservedName")));
            return;
        }

        if (!node.Node.IsDirectory)
        {
            work.Files.Add(node.Node);
            work.TotalBytes += node.Node.Size;
            return;
        }

        if (path.Length > 0)
            work.Directories.Add(node.Node);
        if (node.Node.Error is { } error)
            work.Skipped.Add(new SkippedEntry(path, error));
        foreach (var child in node.Children)
            Collect(child, work);
    }

    /// <summary>
    /// Whether a source path, file or folder, would land on one of the names a version folder uses for itself: the
    /// manifest and the markers. Copied there, it would hide the version, be taken for its manifest, or clash with the
    /// run's own marker. Only the root counts: deeper down these names are ordinary files.
    /// </summary>
    private static bool IsReservedRootName(string path) =>
        path.Equals(VersionMarkerNames.Manifest, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(VersionMarkerNames.Pending, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(VersionMarkerNames.Deleting, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes what earlier runs of this plan left behind (<see cref="LeftoverCleaner"/>): folders of runs that never
    /// finished, interrupted deletions, and the ".partial" / ".deleting" folders of older versions. Its warnings go into
    /// the run log.
    /// </summary>
    /// <remarks>
    /// On a network target this can take long (a run that was killed leaves a whole copy), so the files removed are
    /// reported, and cancellation stops between batches of files: the rest stays for the next run.
    /// </remarks>
    private static async Task DeleteLeftoversAsync(IStorage target, BackupPlan plan, RunLogEntry entry,
        IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        var removed = 0;
        var started = false;
        var sinceReport = Stopwatch.StartNew();
        var current = "";
        void Report() => progress?.Report(new BackupProgress(BackupPhase.RemovingLeftovers, removed, 0, 0, 0, current));
        void OnFileDeleted(string name, int count)
        {
            if (count == 0)
            {
                // A folder starts: always reported.
                started = true;
                current = name;
                sinceReport.Restart();
                Report();
                return;
            }
            removed += count;
            if (sinceReport.Elapsed < ProgressInterval)
                return;
            sinceReport.Restart();
            Report();
        }

        // Nothing runs yet whose folder must be spared: the name is reserved after the cleanup, never before it.
        var warnings = await LeftoverCleaner.CleanAsync(target, plan.Id, plan.Name, currentVersionName: null,
            cancellationToken, OnFileDeleted).ConfigureAwait(false);
        entry.Warnings.AddRange(warnings);
        if (started)
            Report();
    }

    /// <summary>
    /// Removes the folder of this run after it failed or was canceled: the manifest if it was written, the files and
    /// folders, the pending marker last. What cannot be removed keeps the marker; a warning says so, and the next run of
    /// the plan removes it.
    /// </summary>
    private static async Task RemoveUnfinishedAsync(IStorage target, string versionName, RunLogEntry entry)
    {
        try
        {
            await VersionRemover.RemoveUnfinishedAsync(target, versionName, onFileDeleted: null, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            entry.Warnings.Add(CoreTexts.English("core.run.unfinishedFailed", ("name", versionName), ("error", ex.Message)));
        }
    }

    /// <summary>Hands the finished version to the version index. Whatever goes wrong there is a warning only.</summary>
    private void AddToIndex(BackupPlan plan, IStorage target, RunLogEntry entry, string versionName, BackupManifest manifest)
    {
        try
        {
            VersionName.TryParseAny(versionName, out var localTime, out _);
            var version = new VersionInfo(versionName, versionName, localTime, VersionOwnership.Owned,
                manifest.FileCount, manifest.TotalBytes);
            _indexSink!.Add(plan.Id, target, version, manifest);
        }
        catch (Exception ex)
        {
            entry.Warnings.Add(CoreTexts.English("core.run.indexFailed", ("error", ex.Message)));
        }
    }

    /// <summary>Deletes the versions the plan's rules no longer keep. Problems become warnings; the run stays successful.</summary>
    private async Task ApplyRetentionAsync(BackupPlan plan, IStorage target, RunLogEntry entry,
        IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        if (ResolveRules(plan, entry, "core.run.retentionRulesUnreadable", "core.run.retentionPlanGone") is not { Count: > 0 } rules)
            return;

        progress?.Report(new BackupProgress(BackupPhase.Retention, entry.FilesCopied, entry.FilesCopied,
            entry.BytesCopied, entry.BytesCopied, ""));

        List<VersionInfo> doomed;
        try
        {
            var versions = await VersionCatalog.ListAsync(target, plan.Id, plan.Name).ConfigureAwait(false);
            doomed = RetentionPlanner.Decide(versions, rules).Where(d => d.Delete).Select(d => d.Version).ToList();
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            entry.Warnings.Add(CoreTexts.English("core.run.retentionFailed", ("error", ex.Message)));
            return;
        }

        foreach (var version in doomed)
        {
            if (cancellationToken.IsCancellationRequested)
                return;   // the next successful run deletes the rest
            if (version.Name.Equals(entry.Version, StringComparison.OrdinalIgnoreCase))
                continue;   // never the version this run just made, whatever the clock or the rules say

            await TryRemoveVersionAsync(plan, target, version, entry, "core.run.retentionDeleteFailed").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The rules a deletion pass applies. A run can take hours and the rules can be changed and saved meanwhile, so
    /// the rules saved right now count when the runner can look them up; otherwise those of the request. Null
    /// means: delete nothing in this pass. The reason is then added as a warning: <paramref name="rulesUnreadableKey"/>
    /// (with the error) or <paramref name="planGoneKey"/>.
    /// </summary>
    private IReadOnlyList<RetentionRule>? ResolveRules(BackupPlan plan, RunLogEntry entry, string rulesUnreadableKey,
        string planGoneKey)
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
            entry.Warnings.Add(CoreTexts.English(rulesUnreadableKey, ("error", ex.Message)));
            return null;
        }

        if (rules is null)
            entry.Warnings.Add(CoreTexts.English(planGoneKey));
        return rules;
    }

    /// <summary>
    /// Removes one version. True when the folder no longer is a version: it is gone, or it was marked as being
    /// deleted and only its remains are left (the next run of the plan removes them). False when nothing was changed;
    /// that is also the answer when the folder or the whole target has vanished, because nothing was deleted then.
    /// </summary>
    /// <remarks>
    /// A removal that has started is not canceled: stopping half-way would only leave remains. Cancellation is
    /// honoured between versions by the callers.
    /// </remarks>
    private async Task<bool> TryRemoveVersionAsync(BackupPlan plan, IStorage target, VersionInfo version, RunLogEntry entry,
        string failureKey)
    {
        try
        {
            await VersionRemover.RemoveAsync(target, version.Path, Marker(plan), ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (VersionRemainsException ex)
        {
            entry.Warnings.Add(CoreTexts.English("core.run.removedRemainsLeft", ("version", version.Name), ("error", ex.Message)));
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            entry.Warnings.Add(CoreTexts.English(failureKey, ("version", version.Name), ("error", ex.Message)));
            return false;
        }
        entry.RetentionDeleted.Add(version.Name);
        return true;
    }

    /// <summary>
    /// What the markers this run writes say: the plan, now, and this computer. A deletion marker names the plan name
    /// the version folder carries instead (<see cref="VersionRemover.RemoveAsync"/>).
    /// </summary>
    private MarkerInfo Marker(BackupPlan plan) =>
        new(VersionMarkers.FormatVersion, plan.Id, plan.Name, _time.GetUtcNow().UtcDateTime, _host);

    /// <summary>
    /// Reserves the folder name for the current minute by creating its <see cref="VersionMarkerNames.Pending"/>
    /// exclusively (spec 6.1 step 1); returns the name. A name is taken when anything is there under it, also a folder
    /// that is being deleted or one with an older transient name; the run then waits for the next minute. The exclusive
    /// create is the final guard against another run that takes the same name at the same moment.
    /// </summary>
    private async Task<string> ReserveVersionNameAsync(BackupPlan plan, IStorage target, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _time.GetLocalNow().DateTime;
            var name = VersionName.Format(now, plan.Name);
            if (!await IsNameTakenAsync(target, name, cancellationToken).ConfigureAwait(false) &&
                await TryWritePendingAsync(target, name, plan, cancellationToken).ConfigureAwait(false))
                return name;

            var nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(1);
            await Task.Delay(nextMinute - now + TimeSpan.FromMilliseconds(50), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether anything is there under the name: the folder in any state, or a ".partial" / ".deleting" folder of it.</summary>
    private static async Task<bool> IsNameTakenAsync(IStorage target, string name, CancellationToken cancellationToken)
    {
        foreach (var path in new[] { name, name + VersionName.PartialSuffix, name + VersionName.DeletingSuffix })
        {
            if (await target.StatAsync(path, cancellationToken).ConfigureAwait(false) is not null)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Creates the pending marker, the first thing written into the folder, so the folder never exists without it.
    /// False when someone else holds the name. When the marker fails otherwise, the folder its creation may have made is
    /// removed again if it is empty.
    /// </summary>
    private async Task<bool> TryWritePendingAsync(IStorage target, string name, BackupPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            await VersionMarkers.WritePendingAsync(target, name, Marker(plan), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (StorageConflictException)
        {
            return false;   // the folder is whoever's marker is in it: never touched
        }
        catch (Exception)   // deliberately broad: whatever failed (OperationCanceledException too) is rethrown below
        {
            try
            {
                // Removes an empty folder only: whatever someone else wrote there meanwhile stays.
                await target.DeleteAsync([name], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is StorageException or ArgumentException)
            {
                // Best effort: an empty folder named like a version of the plan goes at the next run.
            }
            throw;
        }
    }

    /// <summary>
    /// Creates the folders (on storages that have them), copies the files, writes the manifest and removes the pending
    /// marker, which makes the folder a version (spec 6.1 steps 2–4).
    /// </summary>
    /// <remarks>
    /// A name the target cannot hold (<see cref="ArgumentException"/>, e.g. one ending in a blank or a dot that WSL
    /// created) is skipped: a folder with everything in it, as one entry, and a file on its own. Such folders are found
    /// when they are created, so on storages without directories each file in them is skipped by itself.
    /// </remarks>
    private async Task<BackupManifest> CopyAndFinishAsync(BackupWork work, BackupPlan plan, string versionName,
        RunLogEntry entry, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        var target = work.Target;
        var directories = work.Directories.Select(d => d.RelativePath).ToList();
        var unaddressable = new List<string>();
        bool IsInUnaddressable(string path) =>
            unaddressable.Any(folder => path.StartsWith(folder + "/", StringComparison.Ordinal));

        if (target.Capabilities.HasFlag(StorageCapabilities.EmptyDirectories))
        {
            // Creating many folders on a network share takes a while, so it reports progress of its own.
            var foldersTotal = work.Directories.Count;
            var foldersDone = 0;
            var sinceFolderReport = Stopwatch.StartNew();
            directories.Clear();
            progress?.Report(new BackupProgress(BackupPhase.CreatingFolders, 0, foldersTotal, 0, 0, ""));
            foreach (var directory in work.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Parents come before their children, so a folder left out is known before what is in it.
                if (!IsInUnaddressable(directory.RelativePath))
                {
                    try
                    {
                        await target.EnsureDirectoryAsync(StoragePath.Combine(versionName, directory.RelativePath), cancellationToken)
                            .ConfigureAwait(false);
                        directories.Add(directory.RelativePath);
                    }
                    catch (ArgumentException ex)
                    {
                        unaddressable.Add(directory.RelativePath);
                        // A folder the source could not list either has been reported already, with that reason.
                        if (directory.Error is null)
                            entry.AddSkipped(new SkippedEntry(directory.RelativePath,
                                CoreTexts.English("core.file.cannotOpen", ("error", ex.Message))));
                    }
                }
                foldersDone++;
                if (progress is not null && (sinceFolderReport.Elapsed >= ProgressInterval || foldersDone == foldersTotal))
                {
                    sinceFolderReport.Restart();
                    progress.Report(new BackupProgress(BackupPhase.CreatingFolders, foldersDone, foldersTotal, 0, 0,
                        directory.RelativePath));
                }
            }
        }

        var manifest = new BackupManifest
        {
            PlanId = plan.Id,
            PlanName = plan.Name,
            CreatedUtc = _time.GetUtcNow().UtcDateTime,
            Source = work.SourceRoot,
            Directories = directories,
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
            if (IsInUnaddressable(file.RelativePath))
            {
                // Left out with its folder, which is reported.
                filesDone++;
                bytesDone = bytesBefore + file.Size;
                continue;
            }

            var (copied, skipReason, changed) = await CopyFileAsync(work.Source, target, versionName, file, buffer, count =>
            {
                bytesDone += count;
                Report(BackupPhase.Copying, file.RelativePath, force: false);
            }, cancellationToken).ConfigureAwait(false);

            if (copied is null)
            {
                // Every file fails once the whole source is gone (e.g. a drive or share went away): abort, do not skip all.
                if (!await SourceRootExistsAsync(work.Source, cancellationToken).ConfigureAwait(false))
                    throw new BackupAbortException(RunStatus.Error, CoreTexts.English("core.run.sourceGone"));
                entry.AddSkipped(new SkippedEntry(file.RelativePath, skipReason!));
            }
            else
            {
                if (changed)
                {
                    entry.AddSkipped(new SkippedEntry(file.RelativePath,
                        CoreTexts.English("core.skip.changed")));
                }
                manifest.Files.Add(copied);
                entry.FilesCopied++;
                entry.BytesCopied += copied.Size;
            }

            filesDone++;
            bytesDone = bytesBefore + file.Size;   // keeps the bar moving for skipped or changed files
            Report(BackupPhase.Copying, file.RelativePath, force: false);
        }

        // The last report comes before the commit: a throwing progress callback must not undo a finished backup.
        Report(BackupPhase.Finishing, "", force: true);
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        // Durable: the manifest is what makes the folder a version, so it must be on disk before the marker goes.
        var writer = await target.CreateAsync(StoragePath.Combine(versionName, VersionMarkerNames.Manifest),
            new CreateOptions(Durable: true), cancellationToken).ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(writer, manifest, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
            await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await RemovePendingMarkerAsync(target, versionName).ConfigureAwait(false);
        return manifest;
    }

    /// <summary>
    /// Removes the pending marker: from now on the folder is a version (spec 6.1 step 4). Antivirus, indexers or
    /// Explorer can briefly hold a handle on it, so this is retried before giving up. It is not canceled: with the
    /// manifest written, finishing is the shortest way out.
    /// </summary>
    private static async Task RemovePendingMarkerAsync(IStorage target, string versionName)
    {
        var marker = StoragePath.Combine(versionName, VersionMarkerNames.Pending);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await target.DeleteAsync([marker], CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (StorageException ex) when (attempt < MarkerDeleteAttempts && ex is not StorageUnavailableException)
            {
                await Task.Delay(MarkerDeleteRetryDelay * attempt).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Copies one file while hashing it; the copy gets the indexed time of the source. Returns no copy and a reason
    /// when the source cannot be read or the target cannot hold the name; other target errors propagate.
    /// <c>Changed</c> tells that the source's size or time after the copy differs from what was indexed.
    /// </summary>
    private static async Task<(ManifestFile? Copied, string? SkipReason, bool Changed)> CopyFileAsync(IStorage source,
        IStorage target, string versionName, IndexNode node, byte[] buffer, Action<int> onBytes,
        CancellationToken cancellationToken)
    {
        Stream input;
        try
        {
            input = await source.OpenReadAsync(node.RelativePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SourceSkipReason(ex) is { } reason)
        {
            return (null, reason, false);
        }

        var hash = new XxHash64();
        long size = 0;
        await using (input.ConfigureAwait(false))
        {
            // The time is fixed when the file is created: the copy and its manifest entry carry the indexed one.
            StorageWriter writer;
            try
            {
                writer = await target.CreateAsync(StoragePath.Combine(versionName, node.RelativePath),
                    new CreateOptions(ModifiedUtc: node.LastWriteUtc), cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                // A name the target cannot hold, e.g. one ending in a blank or a dot that WSL created.
                return (null, CoreTexts.English("core.file.cannotOpen", ("error", ex.Message)), false);
            }
            await using (writer.ConfigureAwait(false))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read;
                    try
                    {
                        read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    }
                    catch (StorageLockedException)
                    {
                        return (null, CoreTexts.English("core.file.locked"), false);   // the uncommitted copy is discarded
                    }

                    if (read == 0)
                        break;
                    hash.Append(buffer.AsSpan(0, read));
                    await writer.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    size += read;
                    onBytes(read);
                }
                await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // The source is opened shared, so it can change while it is read: compare with what was indexed.
        bool changed;
        try
        {
            var now = await source.StatAsync(node.RelativePath, cancellationToken).ConfigureAwait(false);
            changed = now is null || size != node.Size || now.Size != node.Size || now.ModifiedUtc != node.LastWriteUtc;
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            changed = true;
        }

        return (new ManifestFile(node.RelativePath, size, node.LastWriteUtc,
            "xxh64:" + Convert.ToHexStringLower(hash.GetCurrentHash())), null, changed);
    }

    /// <summary>Whether the source's root is still there; false when it is gone or cannot be reached.</summary>
    private static async Task<bool> SourceRootExistsAsync(IStorage source, CancellationToken cancellationToken)
    {
        try
        {
            return await source.StatAsync("", cancellationToken).ConfigureAwait(false) is { IsDirectory: true };
        }
        catch (StorageException ex)
        {
            return ex is not (StorageNotFoundException or StorageUnavailableException);
        }
    }

    /// <summary>Why a source file that cannot be opened is skipped; null for a failure that does not come from opening it.</summary>
    private static string? SourceSkipReason(Exception exception) => exception switch
    {
        StorageNotFoundException => NoLongerExists,
        StorageAccessDeniedException => CoreTexts.English("core.file.accessDenied"),
        StorageLockedException => CoreTexts.English("core.file.locked"),
        // Opening reads the source only, so any other failure here is the source file's own: e.g. a WSL symlink
        // (Windows cannot open it, error 1920). A source that is gone altogether is caught by the caller.
        StorageException storage => CoreTexts.English("core.file.cannotOpen",
            ("error", storage.InnerException?.Message ?? storage.Message)),
        // A name the storage cannot address, e.g. one ending in a space or a dot that WSL created.
        ArgumentException argument => CoreTexts.English("core.file.cannotOpen", ("error", argument.Message)),
        _ => null,
    };

    private sealed class BackupWork(IStorage source, IStorage target, string sourceRoot)
    {
        public IStorage Source { get; } = source;
        public IStorage Target { get; } = target;

        /// <summary>Where the source is, as the manifest records it.</summary>
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
