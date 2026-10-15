using System.Runtime.CompilerServices;
using System.Text.Json;
using ReBackup.Core.Indexing;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Core.Versions;
using ReBackup.Storage;

namespace ReBackup.Core.Backup;

/// <summary>
/// The run of an incremental plan (spec 4): the target is locked for the whole run, every distinct file content is one
/// blob in the target's pool, and the version folder holds only the manifest that names those blobs.
/// </summary>
public sealed partial class BackupRunner
{
    /// <summary>What a lock holder is doing while it runs a backup (<see cref="LockInfo.Operation"/>).</summary>
    private const string LockOperation = "backup";

    /// <summary>
    /// Checks the target's storage format against the plan before anything is written (spec 3, mode guard). An
    /// incremental plan first takes the target's lock into <paramref name="heldLock"/> and creates the pool's store file on
    /// an empty target; a full plan never touches the lock or the store file, it only refuses a target that holds a pool.
    /// </summary>
    private async Task GuardTargetAsync(BackupPlan plan, IStorage target, StrongBox<TargetLock?> heldLock,
        CancellationToken cancellationToken)
    {
        if (plan.Incremental)
        {
            heldLock.Value = await TargetLock.TryAcquireAsync(target, _host, LockOperation, _time, cancellationToken)
                .ConfigureAwait(false);
            if (heldLock.Value is null)
                throw new BackupAbortException(RunStatus.Error,
                    await TargetLock.LockedMessageAsync(target, cancellationToken).ConfigureAwait(false));
        }

        var outcome = await StoreGuard.CheckAsync(target, plan, createIfMissing: plan.Incremental, cancellationToken)
            .ConfigureAwait(false);
        if (outcome is not (StoreGuardOutcome.Ok or StoreGuardOutcome.Created))
            throw new BackupAbortException(RunStatus.Error, CoreTexts.English(StoreGuard.MessageKey(outcome)));
    }

    /// <summary>
    /// Garbage collection after retention (spec 6), under the run's lock: deletes the blobs no version references any more.
    /// A skip or an abort, and whatever goes wrong, is a warning; the run stays as it is.
    /// </summary>
    private static async Task CollectGarbageAsync(BackupPlan plan, IStorage target, RunLogEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await PoolCollector.CollectAsync(target, plan.Id, plan.Name, cancellationToken).ConfigureAwait(false);
            if (result.SkippedReason is { } reason)
                entry.Warnings.Add(reason);
        }
        catch (OperationCanceledException)
        {
            // The next run collects.
        }
        catch (Exception ex)
        {
            entry.Warnings.Add(CoreTexts.English("core.gc.failed", ("error", ex.Message)));
        }
    }

    /// <summary>
    /// Frees space for an incremental plan (spec 5): deleting a version frees only the content no other version references,
    /// so <paramref name="candidates"/> are deleted in order until their <see cref="PoolUsage.UniqueBytesAsync"/> covers the
    /// shortfall, then the pool is collected and the free space read again. Nothing is deleted when all candidates together
    /// cannot cover it. Returns the free space; <see cref="long.MaxValue"/> when the storage stops telling it.
    /// </summary>
    private async Task<long> FreePoolSpaceAsync(BackupPlan plan, IStorage target, IReadOnlyList<VersionInfo> versions,
        List<VersionInfo> candidates, long required, long free, RunLogEntry entry, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, long> unique;
        try
        {
            unique = await PoolUsage.UniqueBytesAsync(target, versions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            entry.Warnings.Add(CoreTexts.English("core.run.freeSpaceExamineFailed", ("error", ex.Message)));
            return free;
        }

        var shortfall = required - free;
        if (candidates.Sum(v => unique.GetValueOrDefault(v.Name)) < shortfall)
            return free;

        long covered = 0;
        var removed = false;
        foreach (var version in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await TryRemoveVersionAsync(plan, target, version, entry, "core.run.freeSpaceDeleteFailed").ConfigureAwait(false))
                continue;
            removed = true;
            covered += unique.GetValueOrDefault(version.Name);
            if (covered >= shortfall)
                break;
        }
        if (!removed)
            return free;

        await CollectGarbageAsync(plan, target, entry, cancellationToken).ConfigureAwait(false);
        return await target.GetFreeSpaceAsync(cancellationToken).ConfigureAwait(false) ?? long.MaxValue;
    }

    /// <summary>Releases the run's lock. A target that cannot be reached any more leaves the lock in place: a warning, never a failure of the run.</summary>
    private static async Task ReleaseLockAsync(TargetLock held, RunLogEntry entry)
    {
        try
        {
            await held.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            entry.Warnings.Add(CoreTexts.English("core.run.lockReleaseFailed", ("error", ex.Message)));
        }
    }

    /// <summary>
    /// The files of the plan's newest complete version (the newest one <see cref="VersionCatalog"/> lists as owned), by
    /// path; the paths compare as the target compares names. Empty when there is none or its manifest cannot be read:
    /// then every file is hashed, which costs time but never a wrong backup. Entries without a pool hash are left out.
    /// </summary>
    private static async Task<Dictionary<string, ManifestFile>> ReadPreviousManifestAsync(BackupPlan plan, IStorage target,
        CancellationToken cancellationToken)
    {
        var previous = new Dictionary<string, ManifestFile>(target.Capabilities.HasFlag(StorageCapabilities.CaseSensitive)
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase);
        try
        {
            var versions = await VersionCatalog.ListAsync(target, plan.Id, plan.Name, cancellationToken).ConfigureAwait(false);
            if (versions.LastOrDefault(v => v.IsOwned) is not { } newest)
                return previous;
            var stream = await target.OpenReadAsync(StoragePath.Combine(newest.Path, VersionMarkerNames.Manifest),
                cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                ManifestStream.Read(stream, file =>
                {
                    if (PoolLayout.TryParseHex(file.Hash, out _))
                        previous[file.Path] = file;
                }, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            previous.Clear();   // StorageException is an IOException
        }
        return previous;
    }

    /// <summary>
    /// Stores one file into the pool (spec 4 step 5) and returns its manifest entry. In Fast mode a file whose path, size and
    /// time equal the previous version's entry, and whose blob the pool holds, keeps that entry's hash without being opened.
    /// Every other file is read and hashed; content the pool does not hold yet is read a second time and written as its blob.
    /// When the second read gives other content than the first (the file changed in between), nothing is committed and the
    /// file is skipped like a locked one. A file that cannot be opened is skipped as in <see cref="CopyFileAsync"/>.
    /// </summary>
    /// <remarks>
    /// Progress counts the bytes written into the pool; reading a file only to hash it reports no bytes (the caller
    /// counts the file when it is done), so the bar never runs ahead of the indexed sizes.
    /// </remarks>
    private static async Task<FileOutcome> CopyFileToPoolAsync(IStorage source, PoolRun pool, IndexNode node, byte[] buffer,
        Action<int> onBytes, CancellationToken cancellationToken, bool storageFailuresStop)
    {
        if (pool.Detection == ChangeDetection.Fast && pool.IsUnchanged(node, out var previous) && pool.Writer.Contains(previous.Hash))
            return new FileOutcome(new ManifestFile(node.RelativePath, node.Size, node.LastWriteUtc, previous.Hash), null, false, false);

        var locked = new FileOutcome(null, CoreTexts.English("core.file.locked"), false, false);
        var (input, skipReason, denied) = await OpenSourceFileAsync(source, node, cancellationToken, storageFailuresStop)
            .ConfigureAwait(false);
        if (input is null)
            return new FileOutcome(null, skipReason, false, denied);

        string hash;
        long size;
        await using (input.ConfigureAwait(false))
        {
            try
            {
                (hash, size) = await PoolWriter.HashAsync(input, buffer, _ => onBytes(0), cancellationToken).ConfigureAwait(false);
            }
            catch (StorageLockedException)
            {
                return locked;
            }
        }

        if (!pool.Writer.Contains(hash))
        {
            (input, skipReason, denied) = await OpenSourceFileAsync(source, node, cancellationToken, storageFailuresStop)
                .ConfigureAwait(false);
            if (input is null)
                return new FileOutcome(null, skipReason, false, denied);
            await using (input.ConfigureAwait(false))
            {
                try
                {
                    if (await pool.Writer.PutAsync(input, hash, buffer, onBytes, cancellationToken).ConfigureAwait(false)
                        == PutOutcome.ContentChanged)
                        return locked;
                }
                catch (StorageLockedException)
                {
                    return locked;
                }
            }
        }

        var changed = await ChangedAfterReadAsync(source, node, size, cancellationToken).ConfigureAwait(false);
        return new FileOutcome(new ManifestFile(node.RelativePath, size, node.LastWriteUtc, hash), null, changed, false);
    }

    /// <summary>The pool of an incremental run: its blobs, how changes are found, and the previous version's files.</summary>
    private sealed class PoolRun(PoolWriter writer, ChangeDetection detection, Dictionary<string, ManifestFile> previous)
    {
        public PoolWriter Writer { get; } = writer;
        public ChangeDetection Detection { get; } = detection;

        /// <summary>Whether the previous version has the file with the same size and time; <paramref name="entry"/> is its entry then.</summary>
        public bool IsUnchanged(IndexNode node, out ManifestFile entry) =>
            previous.TryGetValue(node.RelativePath, out entry!) && entry.Size == node.Size && entry.MtimeUtc == node.LastWriteUtc;

        /// <summary>The bytes of the files that may need a new blob: all but those unchanged since the previous version.</summary>
        public long BytesToStore(IEnumerable<IndexNode> files) =>
            files.Where(file => !IsUnchanged(file, out _)).Sum(file => file.Size);
    }
}
