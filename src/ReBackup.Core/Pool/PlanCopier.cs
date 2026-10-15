using System.IO.Hashing;
using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Core.Localization;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Shared.Json;
using ReBackup.Storage;

namespace ReBackup.Core.Pool;

/// <summary>Why <see cref="PlanCopier.CheckAsync"/> refuses a new target.</summary>
public enum CopyRefusal
{
    /// <summary>The target can take the copy.</summary>
    None,

    /// <summary>The new target is the plan's target, lies inside it, or holds it.</summary>
    SameOrNestedTarget,

    /// <summary>The new target holds a store file, a pool or version folders.</summary>
    TargetNotEmpty,

    /// <summary>The plan's target is locked by a run, a delete, a verify or another copy.</summary>
    SourceLocked,
}

/// <summary>
/// What a copy did: <paramref name="VersionsCopied"/> versions were written, <paramref name="VersionsSkipped"/> were on the
/// new target already (an earlier copy that stopped). <paramref name="Failures"/> are English texts: whole versions and
/// single files that were not copied, and why the copy could not start or stopped.
/// </summary>
public sealed record PlanCopyResult(int VersionsCopied, int VersionsSkipped, IReadOnlyList<string> Failures, bool Canceled);

/// <summary>
/// Progress of <see cref="PlanCopier.CopyAsync"/>: the <paramref name="Version"/>th (1-based) of <paramref name="VersionCount"/>
/// versions, named <paramref name="VersionName"/> on the new target, and the bytes of its files done so far.
/// </summary>
public readonly record struct PlanCopyProgress(int Version, int VersionCount, string VersionName, long BytesDone, long BytesTotal);

/// <summary>
/// Copy plan (spec 10): copies every version of a plan to a new, empty target in the copy's storage format, so that a full
/// plan becomes incremental or the other way round (or moves to another target). The plan's own target is only read;
/// the only file written there is its lock (<see cref="PoolLayout.LockFile"/>), which is removed again at the end.
/// </summary>
public sealed class PlanCopier
{
    /// <summary>What a lock holder is doing while it copies (<see cref="LockInfo.Operation"/>).</summary>
    private const string LockOperation = "copy";

    private const int BufferSize = 1024 * 1024;

    private readonly IStorageFactory _storages;
    private readonly string _host;
    private readonly TimeProvider _time;
    private readonly Func<string, ConnectionBucket?>? _connections;
    private readonly Func<string, DriveRoot?>? _driveConnections;

    /// <param name="storages">Opens the plan's target and the new target.</param>
    /// <param name="host">This computer, as locks and markers name it.</param>
    /// <param name="time">The clock of locks and markers.</param>
    /// <param name="connections">
    /// Looks an S3 connection's bucket up by id, as for <see cref="PlanValidator.Validate"/>, so that two connections to one
    /// bucket are compared by their prefixes (<see cref="PlanValidator.Overlaps"/>).
    /// </param>
    /// <param name="driveConnections">Looks a Google Drive connection's root up by id, likewise.</param>
    public PlanCopier(IStorageFactory storages, string host, TimeProvider time,
        Func<string, ConnectionBucket?>? connections = null, Func<string, DriveRoot?>? driveConnections = null)
    {
        ArgumentNullException.ThrowIfNull(storages);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(time);
        _storages = storages;
        _host = host;
        _time = time;
        _connections = connections;
        _driveConnections = driveConnections;
    }

    /// <summary>
    /// A new plan with all settings and retention rules of <paramref name="source"/>, a new id, the given name, target and
    /// mode, and created disabled: only the user enables it.
    /// </summary>
    public static BackupPlan MakeCopy(BackupPlan source, string name, StorageLocation target, bool incremental)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var copy = source.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = name;
        copy.Target = target;
        copy.Incremental = incremental;
        copy.Enabled = false;
        return copy;
    }

    /// <summary>
    /// Whether <paramref name="newTarget"/> can take a copy of <paramref name="source"/>: not the plan's target, not inside
    /// it and not holding it (<see cref="PlanValidator.Overlaps"/>); empty (no store file, no pool, no folder named like a
    /// version); and the plan's target not locked. Nothing is written.
    /// </summary>
    /// <exception cref="StorageException">A target cannot be read.</exception>
    public async Task<CopyRefusal> CheckAsync(BackupPlan source, StorageLocation newTarget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(newTarget);
        if (Overlap(source.Target, newTarget))
            return CopyRefusal.SameOrNestedTarget;
        if (!await IsEmptyAsync(_storages.Open(newTarget), ct).ConfigureAwait(false))
            return CopyRefusal.TargetNotEmpty;
        return await IsLockedAsync(_storages.Open(source.Target), ct).ConfigureAwait(false)
            ? CopyRefusal.SourceLocked
            : CopyRefusal.None;
    }

    /// <summary>
    /// Copies the versions <see cref="VersionCatalog"/> lists as owned by <paramref name="source"/>, oldest first, to the
    /// target of <paramref name="copy"/> in its format. The plan's target is locked ("copy") for the whole job, so no
    /// garbage collection removes content meanwhile; the new target too when the copy is incremental. The new target must
    /// suit the copy (<see cref="StoreGuard"/>, the pool's store file is created on an empty target); what an earlier copy
    /// left half-written is cleaned up (<see cref="LeftoverCleaner"/>), and versions that are complete there already are
    /// skipped, so a copy that stopped is continued by calling this again with the same plans.
    /// </summary>
    /// <remarks>
    /// Per version: pending marker, then every file is read through <see cref="VersionViews"/> and verified against the
    /// plan's manifest while it is written (a pool: <see cref="PoolWriter"/>, xxh128 known from a first read unless the
    /// manifest names it already; full: a plain file with its modified time, xxh64 while streaming). A file that cannot be
    /// read or does not match is a failure and left out of the version; the version is still written. Then the manifest,
    /// with the version's original time in its folder name and <see cref="BackupManifest.CreatedUtc"/> and the copy's
    /// id and name, and the marker is removed. A folder name keeps the original one when it carries the copy's name;
    /// otherwise it is the original time with the copy's name.
    /// </remarks>
    public async Task<PlanCopyResult> CopyAsync(BackupPlan source, BackupPlan copy, IProgress<PlanCopyProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(copy);
        var job = new Job();
        if (Overlap(source.Target, copy.Target))
        {
            job.Failures.Add(CoreTexts.English("core.copy.overlaps"));
            return job.Result(canceled: false);
        }
        // The name goes into every version folder's name: one that cannot be a folder name stops before anything is written.
        if (PlanValidator.NameErrors(copy.Name) is [var problem, ..])
        {
            job.Failures.Add(CoreTexts.English("core.copy.failed", ("error", CoreTexts.English(problem))));
            return job.Result(canceled: false);
        }

        var from = _storages.Open(source.Target);
        var to = _storages.Open(copy.Target);
        var canceled = false;
        try
        {
            await LockedCopyAsync(from, to, source, copy, job, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            canceled = true;
        }
        catch (Exception ex) when (ex is StorageException or JsonException)
        {
            job.Failures.Add(CoreTexts.English("core.copy.failed", ("error", ex.Message)));
        }
        finally
        {
            await ReleaseAsync(job.TargetLock, job).ConfigureAwait(false);
            await ReleaseAsync(job.SourceLock, job).ConfigureAwait(false);
        }
        return job.Result(canceled);
    }

    /// <summary>Takes the locks into <paramref name="job"/>, checks the new target and copies; a refusal is a failure.</summary>
    private async Task LockedCopyAsync(IStorage from, IStorage to, BackupPlan source, BackupPlan copy, Job job,
        IProgress<PlanCopyProgress>? progress, CancellationToken ct)
    {
        job.SourceLock = await TargetLock.TryAcquireAsync(from, _host, LockOperation, _time, ct).ConfigureAwait(false);
        if (job.SourceLock is null)
        {
            job.Failures.Add(await TargetLock.LockedMessageAsync(from, ct).ConfigureAwait(false));
            return;
        }
        if (copy.Incremental)
        {
            job.TargetLock = await TargetLock.TryAcquireAsync(to, _host, LockOperation, _time, ct).ConfigureAwait(false);
            if (job.TargetLock is null)
            {
                job.Failures.Add(await TargetLock.LockedMessageAsync(to, ct).ConfigureAwait(false));
                return;
            }
        }

        var outcome = await StoreGuard.CheckAsync(to, copy, createIfMissing: true, ct).ConfigureAwait(false);
        if (outcome is not (StoreGuardOutcome.Ok or StoreGuardOutcome.Created))
        {
            job.Failures.Add(CoreTexts.English(StoreGuard.MessageKey(outcome)));
            return;
        }
        // What an earlier copy left unfinished. Its warnings name folders that are left alone; a version that needs
        // such a folder reports it.
        await LeftoverCleaner.CleanAsync(to, copy.Id, copy.Name, null, ct, machineName: _host).ConfigureAwait(false);
        await CopyVersionsAsync(from, to, source, copy, job, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Copies the plan's versions one after the other; a version that fails is reported and the next one follows.</summary>
    /// <exception cref="StorageUnavailableException">A target cannot be reached any more: the copy stops.</exception>
    private async Task CopyVersionsAsync(IStorage from, IStorage to, BackupPlan source, BackupPlan copy, Job job,
        IProgress<PlanCopyProgress>? progress, CancellationToken ct)
    {
        var listed = await VersionCatalog.ListAsync(from, source.Id, source.Name, ct).ConfigureAwait(false);
        foreach (var unreadable in listed.Where(v => v.Ownership == VersionOwnership.Unreadable))
            job.Failures.Add(CoreTexts.English("core.copy.manifestUnreadable", ("version", unreadable.Name)));
        // Renamed by hand, another plan's, or without a manifest: not versions of the plan, but named like them.
        var notOwned = listed.Count(v => v.Ownership is VersionOwnership.Renamed or VersionOwnership.Foreign or
            VersionOwnership.NoManifest);
        if (notOwned > 0)
            job.Failures.Add(CoreTexts.English("core.copy.notOwned", ("count", notOwned)));
        var versions = listed.Where(v => v.IsOwned).ToList();
        var complete = (await VersionCatalog.ListAsync(to, copy.Id, copy.Name, ct).ConfigureAwait(false))
            .Where(v => v.IsOwned)
            .Select(v => v.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pool = copy.Incremental ? new PoolWriter(to) : null;
        if (pool is not null)
            await pool.LoadKnownAsync(ct).ConfigureAwait(false);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[BufferSize];
        for (var i = 0; i < versions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var version = versions[i];
            var name = TargetName(version, copy.Name);
            var total = version.TotalBytes ?? 0;
            progress?.Report(new PlanCopyProgress(i + 1, versions.Count, name, 0, total));
            var first = names.Add(name);   // two versions of one minute under one name: only the first is copied
            if (first && complete.Contains(name))
            {
                job.Skipped++;
                continue;
            }
            if (!first || await to.StatAsync(name, ct).ConfigureAwait(false) is not null)
            {
                job.Failures.Add(CoreTexts.English("core.copy.folderTaken", ("version", version.Name), ("folder", name)));
                continue;
            }

            try
            {
                await CopyVersionAsync(from, to, version, name, copy, pool, buffer, job,
                    (done, all) => progress?.Report(new PlanCopyProgress(i + 1, versions.Count, name, done, all)), ct)
                    .ConfigureAwait(false);
                job.Copied++;
            }
            catch (Exception ex) when (ex is StorageException or JsonException or ArgumentException &&
                                       ex is not StorageUnavailableException)
            {
                // Its pending folder stays until the next copy cleans it up.
                job.Failures.Add(CoreTexts.English("core.copy.versionFailed", ("version", version.Name), ("error", ex.Message)));
            }
        }
    }

    /// <summary>One version: pending marker, folders, files, manifest, marker removed.</summary>
    private async Task CopyVersionAsync(IStorage from, IStorage to, VersionInfo version, string name, BackupPlan copy,
        PoolWriter? pool, byte[] buffer, Job job, Action<long, long> report, CancellationToken ct)
    {
        var files = new List<ManifestFile>();
        var directories = new List<string>();
        ManifestSummary summary;
        var stream = await from.OpenReadAsync(StoragePath.Combine(version.Path, VersionMarkerNames.Manifest), ct)
            .ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
            summary = ManifestStream.Read(stream, files.Add, directories.Add, ct);
        var view = await VersionViews.OpenAsync(from, version.Path, ct).ConfigureAwait(false);

        await VersionMarkers.WritePendingAsync(to, name,
            new MarkerInfo(VersionMarkers.FormatVersion, copy.Id, copy.Name, _time.GetUtcNow().UtcDateTime, _host), ct)
            .ConfigureAwait(false);
        var manifest = new BackupManifest
        {
            FormatVersion = pool is null ? BackupManifest.CurrentFormatVersion : BackupManifest.PoolFormatVersion,
            PlanId = copy.Id,
            PlanName = copy.Name,
            CreatedUtc = summary.CreatedUtc,
            Source = summary.Source,
        };
        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                StoragePath.Validate(directory);
            }
            catch (ArgumentException)
            {
                continue;   // not a folder of the version: never written, never listed
            }
            if (pool is null && to.Capabilities.HasFlag(StorageCapabilities.EmptyDirectories))
                await to.EnsureDirectoryAsync(StoragePath.Combine(name, directory), ct).ConfigureAwait(false);
            manifest.Directories.Add(directory);
        }

        var total = files.Sum(f => f.Size);
        long done = 0;
        report(0, total);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var before = done;
            try
            {
                if (IsReserved(file.Path))
                    throw new ArgumentException(CoreTexts.English("core.skip.reservedName"));
                StoragePath.Validate(file.Path);
                var source = StoragePath.Combine(version.Path, file.Path);
                manifest.Files.Add(pool is null
                    ? await CopyToFolderAsync(view, source, to, StoragePath.Combine(name, file.Path), file, buffer, OnBytes, ct)
                        .ConfigureAwait(false)
                    : await CopyToPoolAsync(view, source, pool, file, buffer, OnBytes, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is StorageException or ArgumentException && ex is not StorageUnavailableException)
            {
                job.Failures.Add(CoreTexts.English("core.copy.fileFailed", ("path", file.Path), ("version", version.Name),
                    ("error", ReasonOf(ex))));
            }
            done = before + file.Size;
            report(done, total);

            void OnBytes(int count)
            {
                done = Math.Min(done + count, before + file.Size);
                report(done, total);
            }
        }

        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        // Durable: the manifest is what makes the folder a version, so it must be on disk before the marker goes.
        var writer = await to.CreateAsync(StoragePath.Combine(name, VersionMarkerNames.Manifest), new CreateOptions(Durable: true), ct)
            .ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(writer, manifest, JsonDefaults.Options, ct).ConfigureAwait(false);
            await writer.CommitAsync(ct).ConfigureAwait(false);
        }
        await BackupRunner.RemovePendingMarkerAsync(to, name).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one file into the version folder with its manifest time (where the target can set times), hashing it (xxh64)
    /// while it streams; committed only when it matches the plan's manifest (else <see cref="ContentMismatchException"/>).
    /// </summary>
    private static async Task<ManifestFile> CopyToFolderAsync(IStorage view, string source, IStorage to, string target,
        ManifestFile file, byte[] buffer, Action<int> onBytes, CancellationToken ct)
    {
        var input = await view.OpenReadAsync(source, ct).ConfigureAwait(false);
        await using (input.ConfigureAwait(false))
        {
            var verifier = Restorer.HasherFor(file.Hash);
            var hash = verifier as XxHash64 ?? new XxHash64();
            var modified = file.MtimeUtc != default && to.Capabilities.HasFlag(StorageCapabilities.SetModifiedTime)
                ? file.MtimeUtc
                : (DateTime?)null;
            var output = await to.CreateAsync(target, new CreateOptions(ModifiedUtc: modified), ct).ConfigureAwait(false);
            await using (output.ConfigureAwait(false))
            {
                long size = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(verifier, hash))
                        verifier?.Append(buffer.AsSpan(0, read));
                    hash.Append(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    size += read;
                    onBytes(read);
                }
                ThrowIfMismatch(verifier, file.Hash, source);
                await output.CommitAsync(ct).ConfigureAwait(false);
                return new ManifestFile(file.Path, size, file.MtimeUtc, Restorer.HashText(hash));
            }
        }
    }

    /// <summary>
    /// Puts one file into the pool. A manifest that names its pool hash already needs no first read: the pool has the blob,
    /// or <see cref="PoolWriter.PutAsync"/> verifies the content while it writes. Any other file is read once to verify it
    /// and learn its pool hash, and a second time to write it when the pool does not have it yet.
    /// </summary>
    private static async Task<ManifestFile> CopyToPoolAsync(IStorage view, string source, PoolWriter pool, ManifestFile file,
        byte[] buffer, Action<int> onBytes, CancellationToken ct)
    {
        if (PoolLayout.TryParseHex(file.Hash, out var hex))
        {
            var known = PoolLayout.HashPrefix + hex.ToLowerInvariant();
            if (!pool.Contains(known))
                await PutAsync(view, source, pool, known, buffer, onBytes, ct).ConfigureAwait(false);
            return file with { Hash = known };
        }

        var verifier = Restorer.HasherFor(file.Hash);
        var hash = new XxHash128();
        long size = 0;
        var input = await view.OpenReadAsync(source, ct).ConfigureAwait(false);
        await using (input.ConfigureAwait(false))
        {
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                verifier?.Append(buffer.AsSpan(0, read));
                hash.Append(buffer.AsSpan(0, read));
                size += read;
                onBytes(read);
            }
        }
        ThrowIfMismatch(verifier, file.Hash, source);
        var poolHash = PoolLayout.Format(hash.GetCurrentHashAsUInt128());
        if (!pool.Contains(poolHash))
            await PutAsync(view, source, pool, poolHash, buffer, null, ct).ConfigureAwait(false);
        return new ManifestFile(file.Path, size, file.MtimeUtc, poolHash);
    }

    /// <summary>Writes the blob; content that does not hash to <paramref name="hash"/> is not committed and fails the file.</summary>
    private static async Task PutAsync(IStorage view, string source, PoolWriter pool, string hash, byte[] buffer,
        Action<int>? onBytes, CancellationToken ct)
    {
        var input = await view.OpenReadAsync(source, ct).ConfigureAwait(false);
        await using (input.ConfigureAwait(false))
        {
            if (await pool.PutAsync(input, hash, buffer, onBytes, ct).ConfigureAwait(false) == PutOutcome.ContentChanged)
                throw new ContentMismatchException(source);
        }
    }

    private static void ThrowIfMismatch(NonCryptographicHashAlgorithm? verifier, string expected, string source)
    {
        if (verifier is not null && !string.Equals(Restorer.HashText(verifier), expected, StringComparison.OrdinalIgnoreCase))
            throw new ContentMismatchException(source);
    }

    /// <summary>
    /// The version's folder name on the new target: the original one when it carries the copy's name, else the original
    /// time with the copy's name (a folder name always carries the plan name its manifest records).
    /// </summary>
    private static string TargetName(VersionInfo version, string copyName) =>
        VersionName.TryParseAny(version.Name, out _, out var planName) && string.Equals(planName, copyName, StringComparison.Ordinal)
            ? version.Name
            : VersionName.Format(version.LocalTime, copyName);

    /// <summary>Whether the two locations overlap, with the connection lookups this copier was given.</summary>
    private bool Overlap(StorageLocation a, StorageLocation b) =>
        PlanValidator.Overlaps(a, b, _connections, _driveConnections);

    private static bool IsReserved(string path) =>
        path.Equals(VersionMarkerNames.Manifest, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(VersionMarkerNames.Pending, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(VersionMarkerNames.Deleting, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a file failed, as the restore words it: the provider's own message where there is one.</summary>
    private static string ReasonOf(Exception exception) => exception switch
    {
        ContentMismatchException => exception.Message,
        StorageNotFoundException => CoreTexts.English("core.file.noLongerExists"),
        StorageException { InnerException: { } inner } => inner.Message,
        ArgumentException { ParamName: { } name } => exception.Message.Replace($" (Parameter '{name}')", "", StringComparison.Ordinal),
        _ => exception.Message,
    };

    /// <summary>
    /// Whether a target holds anything of a plan: the store file, a pool folder, or a folder named like a version (also
    /// one being written or deleted). A target that does not exist yet is empty.
    /// </summary>
    private static async Task<bool> IsEmptyAsync(IStorage target, CancellationToken ct)
    {
        if (await target.StatAsync(PoolLayout.StoreFile, ct).ConfigureAwait(false) is not null)
            return false;
        try
        {
            await foreach (var entry in target.ListAsync("", recursive: false, ct).ConfigureAwait(false))
            {
                if (!entry.IsDirectory)
                    continue;
                var name = StoragePath.Name(entry.Path);
                foreach (var suffix in (ReadOnlySpan<string>)[VersionName.PartialSuffix, VersionName.DeletingSuffix])
                    if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        name = name[..^suffix.Length];
                if (name.Equals(PoolLayout.PoolFolder, StringComparison.OrdinalIgnoreCase) ||
                    VersionName.TryParseAny(name, out _, out _))
                    return false;
            }
        }
        catch (StorageNotFoundException)
        {
            // Not created yet.
        }
        return true;
    }

    /// <summary>Whether the target has a lock file; one that cannot be parsed is held by someone too.</summary>
    private static async Task<bool> IsLockedAsync(IStorage target, CancellationToken ct)
    {
        try
        {
            return await TargetLock.ReadAsync(target, ct).ConfigureAwait(false) is not null;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Releases a lock; a target that cannot be written any more leaves it in place, which is reported.</summary>
    private static async Task ReleaseAsync(TargetLock? held, Job job)
    {
        if (held is null)
            return;
        try
        {
            await held.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            job.Failures.Add(CoreTexts.English("core.run.lockReleaseFailed", ("error", ex.Message)));
        }
    }

    /// <summary>What a copy has done so far.</summary>
    private sealed class Job
    {
        public int Copied;
        public int Skipped;
        public TargetLock? SourceLock;
        public TargetLock? TargetLock;
        public List<string> Failures { get; } = [];

        public PlanCopyResult Result(bool canceled) => new(Copied, Skipped, Failures.ToList(), canceled);
    }
}
