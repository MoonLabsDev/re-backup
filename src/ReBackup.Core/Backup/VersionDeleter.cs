using System.Diagnostics;
using System.Text.Json;

namespace ReBackup.Core.Backup;

public enum VersionDeletionOutcome
{
    /// <summary>The folder is gone.</summary>
    Deleted,

    /// <summary>The folder no longer is a version, but its ".deleting" remains are left; the next run removes them.</summary>
    RemainsLeft,

    /// <summary>There was no such folder (any more).</summary>
    Gone,

    /// <summary>The folder is not a version of the plan (<see cref="VersionOwnership.Owned"/>): nothing was changed.</summary>
    NotManaged,

    /// <summary>The folder could not be removed: nothing was changed.</summary>
    Failed,
}

/// <summary>What happened to one version a person asked to delete; <see cref="Error"/> for RemainsLeft and Failed.</summary>
public sealed record VersionDeletion(string Name, VersionDeletionOutcome Outcome, string? Error);

/// <summary>
/// A deletion's progress: the version being deleted (1-based <see cref="Current"/> of <see cref="VersionCount"/>) and
/// the files deleted so far over all versions, of the total their manifests list.
/// </summary>
public readonly record struct VersionDeletionProgress(int Current, int VersionCount, string VersionName, long FilesDone,
    long FilesTotal)
{
    /// <summary>0..1 by files; by versions when the manifests list no files.</summary>
    public double Fraction => FilesTotal > 0
        ? Math.Clamp((double)FilesDone / FilesTotal, 0, 1)
        : VersionCount > 0 ? Math.Clamp((double)(Current - 1) / VersionCount, 0, 1) : 1;
}

/// <summary>Deletes versions a person picked by hand, with the same care retention takes.</summary>
public static class VersionDeleter
{
    /// <summary>
    /// Deletes the plan's versions <paramref name="versionNames"/> (folder names directly in <paramref name="target"/>)
    /// one after the other. Each folder is examined again right before it is removed: only a folder that is still
    /// <see cref="VersionOwnership.Owned"/> by the plan is deleted, whatever the caller listed. Never throws for a
    /// single version. Cancellation stops between versions: the versions not started yet are left out of the result.
    /// </summary>
    /// <param name="progress">
    /// Gets a report when each version starts, then at most every <see cref="ProgressInterval"/> while its files are
    /// deleted, and when it ends; on the deleting thread.
    /// </param>
    public static IReadOnlyList<VersionDeletion> Delete(string target, string planId, IReadOnlyList<string> versionNames,
        ITargetVolume? volume = null, IProgress<VersionDeletionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        volume ??= new PhysicalTargetVolume();
        var filesTotal = progress is null ? 0 : versionNames.Sum(name => FileCountOf(target, name));
        long filesDone = 0;
        var results = new List<VersionDeletion>(versionNames.Count);
        for (var i = 0; i < versionNames.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            var name = versionNames[i];
            var current = i + 1;
            progress?.Report(new VersionDeletionProgress(current, versionNames.Count, name, filesDone, filesTotal));
            var sinceReport = Stopwatch.StartNew();
            results.Add(DeleteOne(target, planId, name, volume, progress is null ? null : count =>
            {
                filesDone += count;
                if (sinceReport.Elapsed < ProgressInterval)
                    return;
                sinceReport.Restart();
                progress.Report(new VersionDeletionProgress(current, versionNames.Count, name, filesDone, filesTotal));
            }));
            progress?.Report(new VersionDeletionProgress(current, versionNames.Count, name, filesDone, filesTotal));
        }
        return results;
    }

    /// <summary>Reports while files are deleted come at most this often.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>The number of files the version's manifest lists; 0 when that is not known. Never throws.</summary>
    private static long FileCountOf(string target, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name))
            return 0;
        try
        {
            var manifest = Path.Combine(target, name, VersionName.ManifestFileName);
            return ManifestReader.ReadHeader(manifest).FileCount ?? ManifestReader.ReadTotals(manifest).FileCount;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException
                                       or ArgumentException)
        {
            return 0;
        }
    }

    private static VersionDeletion DeleteOne(string target, string planId, string name, ITargetVolume volume,
        Action<int>? onFileDeleted)
    {
        // Only a plain folder name of a version: nothing that leads elsewhere, no ".partial" or ".deleting" folder.
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name is "." or ".." ||
            VersionName.IsTransient(name) || !VersionName.TryParseAny(name, out _, out _))
            return new VersionDeletion(name, VersionDeletionOutcome.NotManaged, null);

        var path = Path.Combine(target, name);
        try
        {
            if (!Directory.Exists(path))
                return new VersionDeletion(name, VersionDeletionOutcome.Gone, null);
            if (new DirectoryInfo(path).LinkTarget is not null ||
                VersionCatalog.Probe(path, planId).Ownership != VersionOwnership.Owned)
                return new VersionDeletion(name, VersionDeletionOutcome.NotManaged, null);

            VersionRemover.Remove(path, volume, onFileDeleted);
            return new VersionDeletion(name, VersionDeletionOutcome.Deleted, null);
        }
        catch (VersionRemainsException ex)
        {
            return new VersionDeletion(name, VersionDeletionOutcome.RemainsLeft, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Directory.Exists(path)
                ? new VersionDeletion(name, VersionDeletionOutcome.Failed, ex.Message)
                : new VersionDeletion(name, VersionDeletionOutcome.Gone, null);
        }
    }
}
