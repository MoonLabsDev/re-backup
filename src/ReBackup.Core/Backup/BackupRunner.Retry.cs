using System.Diagnostics;
using System.Text.Json;
using ReBackup.Core.Backup.Retry;
using ReBackup.Core.Indexing;
using ReBackup.Core.Localization;
using ReBackup.Core.Pool;
using ReBackup.Shared.Json;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.Core.Backup;

/// <summary>
/// A retry (spec 4.4): copies only the files a finished run skipped and adds them to the version that run created. The
/// version is finished and visible all along; the retry changes it by replacing its manifest, conditionally on the
/// manifest being the one it read.
/// </summary>
public sealed partial class BackupRunner
{
    /// <summary>Why a retry of an incremental plan ends at once: its blobs and lock are not handled yet.</summary>
    private const string IncrementalRetryUnsupported = "Retry of incremental plans is not implemented yet.";

    /// <summary>The key of the reason a file that is gone gives (<see cref="SourceSkipReason"/>).</summary>
    private const string NoLongerExistsKey = "core.file.noLongerExists";

    /// <summary>A retried file that is gone from the source: nothing to copy and nothing to report.</summary>
    private static readonly FileOutcome Resolved = new(null, null, false, false);

    /// <remarks>
    /// Each file is read, hashed and checked for changes as in a regular run, from what the source says about it now. A
    /// file the source no longer has is resolved: it leaves the list and is not counted (also when only opening it finds
    /// that out). A file copied cleanly counts in <see cref="RunLogEntry.FilesCopied"/>. A file copied but changed again
    /// meanwhile replaces its copy and its manifest entry all the same (the entry describes the new copy), yet stays open
    /// with <c>core.skip.changed</c> and is not counted as copied: it is not recovered. Every other file stays open with
    /// what stopped it this time. The manifest is written, and the version handed to the index, only when at least one
    /// entry changed.
    /// Each copy goes straight over the version's file, so an attempt that stops part-way (Error, Full, Canceled) still
    /// writes the manifest with what it copied so far (once, conditionally on the same stamp, not cancelable; a failure of
    /// that write is ignored) and hands it to the index: a replaced copy never stays under its old hash, where a restore
    /// would refuse it. Its entry still has no files copied and every requested file open again, so a retry state built
    /// from it keeps them all and the next attempt copies them again (overwriting is harmless). What remains is a crash, or
    /// a failed manifest write, between overwriting a copy and replacing the manifest (spec 6): the copy is then newer
    /// than its entry until an attempt copies it again. A manifest replaced meanwhile by someone else
    /// (<see cref="StorageConflictException"/>) is never written over, also not then.
    /// </remarks>
    public async Task<RunLogEntry> RetryAsync(RetryRequest request, IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = _time.GetTimestamp();
        var entry = new RunLogEntry
        {
            RunId = Guid.NewGuid().ToString("N"),
            Trigger = RunTrigger.Retry,
            StartUtc = _time.GetUtcNow().UtcDateTime,
            Version = request.Version,
            RetryOf = request.RunId,
            Attempt = request.Attempt,
            Attempts = request.Attempts,
        };

        try
        {
            if (request.Plan.Incremental)
                throw new BackupAbortException(RunStatus.Error, IncrementalRetryUnsupported);
            await Task.Run(() => RetryIntoFullCopyAsync(request, entry, progress, cancellationToken), cancellationToken);
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
            // Also a manifest that was replaced meanwhile (StorageConflictException): it is kept, and so is the list.
            entry.Status = RunStatus.Error;
            entry.Reason = ex.Message;
        }

        if (!entry.VersionGone && entry.Status is not (RunStatus.Completed or RunStatus.CompletedWithWarnings))
            KeepAllOpen(entry, request.Files);

        entry.DurationMs = (long)_time.GetElapsedTime(started).TotalMilliseconds;
        entry.EndUtc = entry.StartUtc.AddMilliseconds(entry.DurationMs);
        return entry;
    }

    /// <summary>
    /// The retry of a full plan: copies the files into the version folder, overwriting a copy that is there, and replaces
    /// the manifest. Sets <see cref="RunLogEntry.VersionGone"/> and changes nothing when the version is no longer one of the
    /// plan's (deleted, being deleted, or never finished).
    /// </summary>
    private async Task RetryIntoFullCopyAsync(RetryRequest request, RunLogEntry entry, IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = request.Plan;
        var source = _storages.Open(plan.Source);
        var target = _storages.Open(plan.Target);

        // Being written or deleted (a marker), deleted, or renamed by hand: the catalog does not list it as owned.
        var versions = await VersionCatalog.ListAsync(target, plan.Id, plan.Name, cancellationToken).ConfigureAwait(false);
        if (versions.FirstOrDefault(v => v.IsOwned && v.Name.Equals(request.Version, StringComparison.OrdinalIgnoreCase))
            is not { } version)
        {
            entry.VersionGone = true;
            return;
        }
        // Copies never go into a pool, whatever happened to the target since the run.
        var guard = await StoreGuard.CheckAsync(target, plan, createIfMissing: false, cancellationToken).ConfigureAwait(false);
        if (guard != StoreGuardOutcome.Ok)
            throw new BackupAbortException(RunStatus.Error, CoreTexts.English(StoreGuard.MessageKey(guard)));

        // The stamp is taken before the manifest is read: one replaced in between fails the commit, it is never overwritten.
        var manifestPath = StoragePath.Combine(version.Path, VersionMarkerNames.Manifest);
        if (await target.StatAsync(manifestPath, cancellationToken).ConfigureAwait(false) is not { } manifestEntry)
        {
            entry.VersionGone = true;   // removed since it was listed
            return;
        }
        BackupManifest manifest;
        var input = await target.OpenReadAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        await using (input.ConfigureAwait(false))
        {
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(input, JsonDefaults.Options, cancellationToken)
                .ConfigureAwait(false) ?? throw new JsonException($"The manifest of {version.Name} is empty.");
        }

        // Where each path is in the manifest, compared as the target compares names: on a target that ignores case,
        // "foo.txt" is the copy that "Foo.txt" names, and its entry is replaced, not doubled.
        var positions = new Dictionary<string, int>(manifest.Files.Count,
            target.Capabilities.HasFlag(StorageCapabilities.CaseSensitive) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < manifest.Files.Count; index++)
            positions.TryAdd(manifest.Files[index].Path, index);

        var buffer = new byte[BufferSize];
        var sinceReport = Stopwatch.StartNew();
        var filesDone = 0;
        long bytesDone = 0;
        void Report(BackupPhase phase, string currentFile, bool force)
        {
            if (progress is null || (!force && sinceReport.Elapsed < ProgressInterval))
                return;
            sinceReport.Restart();
            // The sizes are only known file by file, so the bar goes by files.
            progress.Report(new BackupProgress(phase, filesDone, request.Files.Count, bytesDone, 0, currentFile));
        }

        Report(BackupPhase.Copying, "", force: true);
        var replaced = 0;   // manifest entries added or replaced: each one is a copy already written into the version
        try
        {
            foreach (var file in request.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var done = await RetryFileAsync(source, target, version.Path, file.Path, buffer, count =>
                {
                    bytesDone += count;
                    Report(BackupPhase.Copying, file.Path, force: false);
                }, cancellationToken).ConfigureAwait(false);

                if (done.Copied is not { } copied)
                {
                    // Every file fails, or seems gone, once the whole source is: that ends the attempt, the files stay listed.
                    if (!await SourceRootExistsAsync(source, cancellationToken).ConfigureAwait(false))
                        throw new BackupAbortException(RunStatus.Error, CoreTexts.English("core.run.sourceGone"));
                    if (done.SkipReason is { } reason)
                        entry.AddSkipped(new SkippedEntry(file.Path, reason, done.SkipKey));
                }
                else
                {
                    if (positions.TryGetValue(copied.Path, out var position))
                        manifest.Files[position] = copied;
                    else
                    {
                        positions[copied.Path] = manifest.Files.Count;
                        manifest.Files.Add(copied);
                    }
                    replaced++;
                    if (done.Changed)
                        entry.AddSkipped(Skip(copied.Path, "core.skip.changed"));
                    else
                    {
                        entry.FilesCopied++;
                        entry.BytesCopied += copied.Size;
                    }
                }

                filesDone++;
                Report(BackupPhase.Copying, file.Path, force: false);
            }

            Report(BackupPhase.Finishing, "", force: true);
            if (replaced > 0)
                await WriteRetriedManifestAsync(target, manifestPath, manifest, manifestEntry.Stamp, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when (replaced > 0 && ex is not StorageConflictException)
        {
            // The copies written so far are already in the version: the manifest must describe them, or a restore refuses
            // a replaced copy whose old hash it still lists. One try, not cancelable; the attempt fails as it did anyway.
            var committed = false;
            try
            {
                await WriteRetriedManifestAsync(target, manifestPath, manifest, manifestEntry.Stamp, CancellationToken.None)
                    .ConfigureAwait(false);
                committed = true;
            }
            catch (Exception)
            {
                // Deliberately broad: the copies stay newer than their entries until an attempt copies them again (spec 6).
            }
            // The version did change: the index learns it as after a finished attempt.
            if (committed && _indexSink is not null)
                AddToIndex(plan, target, entry, version.Name, manifest);
            throw;
        }

        if (replaced > 0 && _indexSink is not null)
            AddToIndex(plan, target, entry, version.Name, manifest);
        entry.Status = entry.SkippedCount > 0 ? RunStatus.CompletedWithWarnings : RunStatus.Completed;
    }

    /// <summary>
    /// Replaces a version's manifest with the retried one, conditionally on <paramref name="expectedStamp"/> (the stamp it
    /// had when it was read; <see cref="StorageConflictException"/> when it was replaced meanwhile). Durable, because the
    /// manifest is what the version is; written to a temporary file and moved over the old one on commit.
    /// </summary>
    private static async Task WriteRetriedManifestAsync(IStorage target, string manifestPath, BackupManifest manifest,
        string? expectedStamp, CancellationToken cancellationToken)
    {
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        var writer = await target.CreateAsync(manifestPath,
            new CreateOptions(Overwrite: true, Durable: true, ExpectedStamp: expectedStamp), cancellationToken)
            .ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(writer, manifest, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
            await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Copies one file of a retry from what the source says about it now, overwriting the copy in the version if there is
    /// one. A file the source no longer has gives neither a copy nor a reason: it is resolved. A file the source cannot
    /// examine or open gives the reason, as in <see cref="CopyFileAsync"/>; a source that cannot be reached propagates.
    /// </summary>
    private static async Task<FileOutcome> RetryFileAsync(IStorage source, IStorage target, string versionPath, string path,
        byte[] buffer, Action<int> onBytes, CancellationToken cancellationToken)
    {
        StorageEntry? now;
        try
        {
            now = await source.StatAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not StorageUnavailableException && SourceSkipReason(ex) is (var reason, var key))
        {
            return key == NoLongerExistsKey ? Resolved : new FileOutcome(null, reason, false, false, key);
        }
        // A folder there now: the file it was is gone as well.
        if (now is null || now.IsDirectory)
            return Resolved;

        var (copied, skipReason, changed, denied, skipKey) = await CopyFileAsync(source, target, versionPath,
            IndexNode.ForFile(path, now), buffer, onBytes, cancellationToken, overwrite: true).ConfigureAwait(false);
        // Gone between the look and the open (ruling R2): resolved like a file that was not there at all.
        return skipKey == NoLongerExistsKey ? Resolved : new FileOutcome(copied, skipReason, changed, denied, skipKey);
    }

    /// <summary>
    /// What an attempt that did not complete leaves in its entry: nothing copied into the version, every requested file
    /// open with the reason it had.
    /// </summary>
    private static void KeepAllOpen(RunLogEntry entry, IReadOnlyList<SkippedEntry> files)
    {
        entry.FilesCopied = 0;
        entry.BytesCopied = 0;
        entry.SkippedCount = 0;
        entry.RetryableCount = 0;
        entry.Skipped.Clear();
        entry.RetryCandidates.Clear();
        foreach (var file in files)
            entry.AddSkipped(file);
    }
}
