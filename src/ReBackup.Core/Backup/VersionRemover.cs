using ReBackup.Storage;

namespace ReBackup.Core.Backup;

/// <summary>
/// The version was marked as being deleted (<see cref="VersionMarkerNames.Deleting"/>) and is no longer a version, but
/// deleting its remains failed. <see cref="RemainsPath"/> is the folder's storage path.
/// </summary>
public sealed class VersionRemainsException(string remainsPath, Exception inner)
    : IOException(inner.Message, inner)
{
    public string RemainsPath { get; } = remainsPath;
}

/// <summary>Removes version folders in a way that never leaves something that still looks like a complete version.</summary>
/// <remarks>
/// Links are never followed: a link inside a folder is deleted as a link (listings do not descend into links), and a
/// folder that is a link itself is refused.
/// </remarks>
public static class VersionRemover
{
    /// <summary>Paths per <see cref="IStorage.DeleteAsync"/> call; progress and cancellation are handled between batches.</summary>
    public const int BatchSize = 1000;

    /// <summary>
    /// Deletes a version (spec 6.3): writes <see cref="VersionMarkerNames.Deleting"/> (1), deletes the manifest, which
    /// makes the folder no longer a version (2), deletes the other files in batches (3) and the directories bottom-up (4),
    /// then the marker (5) and the folder itself. When step 1 fails, nothing has changed. When a later step fails, the
    /// remains keep the marker and the next run of the plan finishes the deletion (<see cref="FinishRemovalAsync"/>).
    /// </summary>
    /// <remarks>
    /// Cancellation is honoured between batches and propagates as <see cref="OperationCanceledException"/>, not wrapped:
    /// once the marker is written the remains keep it, so the next run of the plan finishes the deletion. Callers that
    /// must not leave remains pass <see cref="CancellationToken.None"/>.
    /// </remarks>
    /// <exception cref="VersionRemainsException">The folder no longer is a version, but its remains could not be deleted.</exception>
    /// <exception cref="StorageException">The folder is gone or a link, or the marker could not be written: nothing was changed.</exception>
    /// <exception cref="ArgumentException"><paramref name="versionPath"/> is the root or not a valid storage path.</exception>
    /// <param name="onFileDeleted">Called with the batch size after every batch of deleted files (manifest and marker aside).</param>
    public static async Task RemoveAsync(IStorage target, string versionPath, MarkerInfo marker, Action<int>? onFileDeleted = null,
        CancellationToken ct = default)
    {
        await RefuseMissingOrLinkAsync(target, versionPath, ct).ConfigureAwait(false);
        await VersionMarkers.WriteDeletingAsync(target, versionPath, marker, ct).ConfigureAwait(false);
        try
        {
            await DeleteFolderAsync(target, versionPath, VersionMarkerNames.Manifest, VersionMarkerNames.Deleting, onFileDeleted, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            throw new VersionRemainsException(versionPath, ex);
        }
    }

    /// <summary>
    /// Finishes a deletion that was started before (spec 6.3 steps 2–5): the folder carries <see cref="VersionMarkerNames.Deleting"/>.
    /// Failures propagate; the marker then stays for the next attempt.
    /// </summary>
    /// <exception cref="StorageException">The folder is gone or a link, or a deletion failed.</exception>
    /// <param name="onFileDeleted">Called with the batch size after every batch of deleted files (manifest and marker aside).</param>
    public static async Task FinishRemovalAsync(IStorage target, string versionPath, Action<int>? onFileDeleted = null,
        CancellationToken ct = default)
    {
        await RefuseMissingOrLinkAsync(target, versionPath, ct).ConfigureAwait(false);
        await DeleteFolderAsync(target, versionPath, VersionMarkerNames.Manifest, VersionMarkerNames.Deleting, onFileDeleted, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the folder of a run that never finished (it carries <see cref="VersionMarkerNames.Pending"/>): a manifest
    /// first, if the run got that far, then the files and directories, and the marker last.
    /// </summary>
    /// <exception cref="StorageException">The folder is gone or a link, or a deletion failed.</exception>
    internal static async Task RemoveUnfinishedAsync(IStorage target, string folderPath, Action<int>? onFileDeleted,
        CancellationToken ct)
    {
        await RefuseMissingOrLinkAsync(target, folderPath, ct).ConfigureAwait(false);
        await DeleteFolderAsync(target, folderPath, VersionMarkerNames.Manifest, VersionMarkerNames.Pending, onFileDeleted, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a folder with an older transient name (<c>.partial</c> or <c>.deleting</c>, as ReBackup 1.0.x left them):
    /// the files first and the manifest last, so that while anything is left the remains can still be attributed to a
    /// plan; then the folder itself.
    /// </summary>
    /// <exception cref="StorageException">The folder is gone or a link, or a deletion failed.</exception>
    /// <param name="onFileDeleted">Called with the batch size after every batch of deleted files (the manifest aside).</param>
    public static async Task RemoveLegacyFolderAsync(IStorage target, string folderPath, Action<int>? onFileDeleted = null,
        CancellationToken ct = default)
    {
        await RefuseMissingOrLinkAsync(target, folderPath, ct).ConfigureAwait(false);
        await DeleteFolderAsync(target, folderPath, first: null, last: VersionMarkerNames.Manifest, onFileDeleted, ct)
            .ConfigureAwait(false);
    }

    /// <summary>The folder must exist and must not be a link: its content would not be ours to delete. The root is never removed.</summary>
    private static async Task RefuseMissingOrLinkAsync(IStorage target, string folderPath, CancellationToken ct)
    {
        if (StoragePath.Validate(folderPath).Length == 0)
            throw new ArgumentException("The root of a storage is never removed.", nameof(folderPath));
        var entry = await target.StatAsync(folderPath, ct).ConfigureAwait(false);
        if (entry is null)
            throw new StorageNotFoundException(folderPath);
        if (entry.IsLink || !entry.IsDirectory)
            throw new StorageIOException(folderPath, $"\"{StoragePath.Name(folderPath)}\" is not a folder (or is a link) and is not removed.");
    }

    /// <summary>
    /// Deletes the top-level file <paramref name="first"/>, then every other file (and every link) below the folder in
    /// batches, then the directories bottom-up, then the top-level file <paramref name="last"/>, then the folder.
    /// Directories are deleted only on storages that have them; elsewhere they vanish with their last file.
    /// </summary>
    /// <remarks>
    /// The directories go before <paramref name="last"/>: remains interrupted there still carry the file that tells whose
    /// they are, so the next run can finish them. <paramref name="first"/> and <paramref name="last"/> are matched by the
    /// storage's case rule, so a manifest written as "RE-MANIFEST.json" by hand still goes last. Cancellation is checked
    /// between the calls only: a batch that has started is finished, so <c>onFileDeleted</c> always reports what is gone.
    /// </remarks>
    private static async Task DeleteFolderAsync(IStorage target, string folder, string? first, string last,
        Action<int>? onFileDeleted, CancellationToken ct)
    {
        var comparison = target.Capabilities.HasFlag(StorageCapabilities.CaseSensitive)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var firstPath = first is null ? null : StoragePath.Combine(folder, first);
        var lastPath = StoragePath.Combine(folder, last);
        if (firstPath is not null)
            await target.DeleteAsync([firstPath], CancellationToken.None).ConfigureAwait(false);

        var firstLeft = new List<string>();
        var lastFound = new List<string>();
        var leaves = new List<string>();
        var directories = new List<string>();
        await foreach (var entry in target.ListAsync(folder, recursive: true, ct).ConfigureAwait(false))
        {
            if (entry.IsDirectory && !entry.IsLink)
                directories.Add(entry.Path);
            else if (firstPath is not null && string.Equals(entry.Path, firstPath, comparison))
                firstLeft.Add(entry.Path);   // spelled differently on a storage that tells case apart in its paths
            else if (string.Equals(entry.Path, lastPath, comparison))
                lastFound.Add(entry.Path);
            else
                leaves.Add(entry.Path);   // a file, or a link: deleted as a link, never entered
        }
        if (firstLeft.Count > 0)
            await target.DeleteAsync(firstLeft, CancellationToken.None).ConfigureAwait(false);

        for (var start = 0; start < leaves.Count; start += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = leaves.GetRange(start, Math.Min(BatchSize, leaves.Count - start));
            await target.DeleteAsync(batch, CancellationToken.None).ConfigureAwait(false);
            onFileDeleted?.Invoke(batch.Count);
        }

        var hasDirectories = target.Capabilities.HasFlag(StorageCapabilities.EmptyDirectories);
        if (hasDirectories)
        {
            var bottomUp = directories.OrderByDescending(path => path.Count(c => c == '/')).ToList();
            for (var start = 0; start < bottomUp.Count; start += BatchSize)
            {
                ct.ThrowIfCancellationRequested();
                await target.DeleteAsync(bottomUp.GetRange(start, Math.Min(BatchSize, bottomUp.Count - start)), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        ct.ThrowIfCancellationRequested();
        await target.DeleteAsync(lastFound.Count > 0 ? lastFound : [lastPath], CancellationToken.None).ConfigureAwait(false);
        if (hasDirectories)
            await target.DeleteAsync([folder], CancellationToken.None).ConfigureAwait(false);
    }
}
