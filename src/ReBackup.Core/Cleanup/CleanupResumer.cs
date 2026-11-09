using System.Diagnostics;
using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Shared.Json;
using ReBackup.Storage;

namespace ReBackup.Core.Cleanup;

/// <summary>
/// Finishes, at app start, the full-plan cleanups this computer did not finish (spec 6.2): the version folders that carry
/// this computer's <see cref="VersionMarkerNames.Pruning"/> of the plan. Only the paths the version's manifest no longer
/// lists are deleted, so a marker whose manifest was never shortened deletes nothing. Markers of other computers are never
/// touched.
/// </summary>
public static class CleanupResumer
{
    /// <summary>
    /// The version folders in the root of <paramref name="target"/> (named like a version, never links, transient
    /// ".partial" / ".deleting" folders left out, as <see cref="DeletionResumer.FindAsync"/> looks at them) whose
    /// <see cref="VersionMarkerNames.Pruning"/> names plan <paramref name="planId"/> and computer <paramref name="host"/>
    /// (ignoring case, as other markers compare hosts). A target that does not exist yet has none.
    /// </summary>
    /// <exception cref="StorageException">The target cannot be read.</exception>
    public static async Task<IReadOnlyList<string>> FindAsync(IStorage target, string planId, string host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var folders = new List<string>();
        try
        {
            await foreach (var entry in target.ListAsync("", recursive: false, ct).ConfigureAwait(false))
            {
                if (!entry.IsDirectory || entry.IsLink)
                    continue;
                var name = StoragePath.Name(entry.Path);
                if (!VersionName.IsPlainFolderName(name) || VersionName.IsTransient(name) || !VersionName.TryParseAny(name, out _, out _))
                    continue;
                if (await PruningMarker.TryReadAsync(target, entry.Path, ct).ConfigureAwait(false) is { } marker &&
                    marker.PlanId == planId && string.Equals(marker.Host, host, StringComparison.OrdinalIgnoreCase))
                    folders.Add(entry.Path);
            }
        }
        catch (StorageNotFoundException)
        {
            return [];   // the target has not been created yet
        }
        return folders;
    }

    /// <summary>
    /// Finishes the cleanups of <paramref name="folders"/> (from <see cref="FindAsync"/>), one folder after the other. Per
    /// folder the marker and the manifest are read; the marker's files the manifest no longer lists are deleted in batches
    /// (a file already missing is fine), then its folders the manifest no longer lists, deepest first, where they are
    /// empty; then the marker is removed. Paths are compared as the target compares names: ignoring case unless it is
    /// <see cref="StorageCapabilities.CaseSensitive"/>. A folder whose marker is gone is skipped; one whose manifest cannot
    /// be read deletes nothing and keeps its marker (traced), as does one whose deletion fails, so the next start tries
    /// again.
    /// </summary>
    /// <returns>The number of files deleted (those the marker listed that the manifest no longer lists).</returns>
    /// <remarks>
    /// <paramref name="ct"/> is looked at only before a folder starts: a folder that was started is finished, the rest
    /// stays for the next start.
    /// </remarks>
    public static async Task<int> ResumeAsync(IStorage target, IReadOnlyList<string> folders, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(folders);
        var comparer = CleanupRunner.PathComparerOf(target);
        var deleted = 0;
        foreach (var folder in folders)
        {
            if (ct.IsCancellationRequested)
                break;
            try
            {
                deleted += await ResumeFolderAsync(target, folder, comparer).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is StorageException or ArgumentException)
            {
                Trace.TraceWarning($"The cleanup of {StoragePath.Name(folder)} could not be finished: {ex.Message}");
            }
        }
        return deleted;
    }

    /// <summary>One folder of <see cref="ResumeAsync"/>; not cancelable, as what it starts it finishes.</summary>
    private static async Task<int> ResumeFolderAsync(IStorage target, string folder, StringComparer comparer)
    {
        var ct = CancellationToken.None;
        if (await PruningMarker.TryReadAsync(target, folder, ct).ConfigureAwait(false) is not { } marker)
            return 0;   // removed meanwhile, or not one this build can read

        if (await TryReadManifestAsync(target, folder, ct).ConfigureAwait(false) is not { } manifest)
        {
            // Without the manifest nothing tells which listed paths are still the version's: delete nothing.
            Trace.TraceWarning($"The cleanup of {StoragePath.Name(folder)} is not finished: its manifest cannot be read.");
            return 0;
        }

        var listedFiles = manifest.Files.Select(f => f.Path).ToHashSet(comparer);
        var listedFolders = manifest.Directories.ToHashSet(comparer);
        var doomed = marker with
        {
            Files = [.. marker.Files.Where(path => !listedFiles.Contains(path))],
            Folders = [.. marker.Folders.Where(path => !listedFolders.Contains(path))],
        };
        await CleanupRunner.DeleteAsync(target, folder, doomed, ct).ConfigureAwait(false);
        await PruningMarker.RemoveAsync(target, folder, ct).ConfigureAwait(false);
        return doomed.Files.Count;
    }

    /// <summary>The folder's manifest; null when it is not there or is not a manifest. Other storage errors propagate.</summary>
    private static async Task<BackupManifest?> TryReadManifestAsync(IStorage target, string folder, CancellationToken ct)
    {
        try
        {
            var input = await target.OpenReadAsync(StoragePath.Combine(folder, VersionMarkerNames.Manifest), ct)
                .ConfigureAwait(false);
            await using (input.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<BackupManifest>(input, JsonDefaults.Options, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (StorageNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
