using System.Text.Json;
using ReBackup.Storage;

namespace ReBackup.Core.Backup;

public enum VersionOwnership
{
    /// <summary>
    /// The manifest carries the plan's id and the folder still has the name it was created under: retention
    /// manages this version.
    /// </summary>
    Owned,

    /// <summary>Named like a version of the plan, but there is no manifest.</summary>
    NoManifest,

    /// <summary>Named like a version of the plan, but the manifest carries another plan's id.</summary>
    Foreign,

    /// <summary>Named like a version of the plan, but the manifest cannot be read.</summary>
    Unreadable,

    /// <summary>
    /// The manifest carries the plan's id, but the folder name is not the one the version was created under
    /// (renamed or copied by hand).
    /// </summary>
    Renamed,
}

/// <summary>
/// A version folder in a target. <paramref name="Path"/> is the folder's storage path, relative to the target's root
/// (for the direct children the catalog lists, the same as <paramref name="Name"/>). Only
/// <see cref="VersionOwnership.Owned"/> versions are ever deleted.
/// </summary>
public sealed record VersionInfo(string Name, string Path, DateTime LocalTime, VersionOwnership Ownership,
    int? FileCount, long? TotalBytes)
{
    public bool IsOwned => Ownership == VersionOwnership.Owned;
}

/// <summary>Finds the versions of a plan in its target.</summary>
public static class VersionCatalog
{
    /// <summary>
    /// The versions of a plan, oldest first. A folder belongs to the plan when its manifest carries the plan's id
    /// and its name is a timestamp followed by the plan name recorded in that manifest, i.e. the name the version
    /// was created under: versions made before the plan was renamed stay with the plan, while a folder that was
    /// copied or renamed by hand is listed as <see cref="VersionOwnership.Renamed"/> and is not owned. Folders
    /// that only carry the plan's current name are listed as not owned too. Folders that are links are left out, and
    /// so are folders that are being written (<see cref="VersionMarkerNames.Pending"/>) or removed
    /// (<see cref="VersionMarkerNames.Deleting"/>, or the older ".partial" / ".deleting" names), and folders whose name
    /// is not a plain folder name (<see cref="VersionName.IsPlainFolderName"/>).
    /// </summary>
    /// <returns>An empty list when the target's root does not exist (yet).</returns>
    /// <exception cref="StorageUnavailableException">The target cannot be reached.</exception>
    /// <exception cref="StorageException">The target cannot be listed.</exception>
    public static async Task<IReadOnlyList<VersionInfo>> ListAsync(IStorage target, string planId, string planName,
        CancellationToken ct = default)
    {
        var versions = new List<VersionInfo>();
        var candidates = new List<(string Path, string Name, DateTime LocalTime, string FolderPlanName)>();
        try
        {
            await foreach (var entry in target.ListAsync("", recursive: false, ct).ConfigureAwait(false))
            {
                // A junction or symbolic link is never a version: removing it would reach into the link's target.
                if (!entry.IsDirectory || entry.IsLink)
                    continue;
                var name = StoragePath.Name(entry.Path);
                // A name with a trailing blank or dot (WSL, a share) is never one a run wrote, and a storage may not
                // be able to address what is inside it.
                if (!VersionName.IsPlainFolderName(name) || VersionName.IsTransient(name) ||
                    !VersionName.TryParseAny(name, out var localTime, out var folderPlanName))
                    continue;
                candidates.Add((entry.Path, name, localTime, folderPlanName));
            }
        }
        catch (StorageNotFoundException)
        {
            return versions;   // a plan that has not run yet: its first run creates the target
        }

        foreach (var (path, name, localTime, folderPlanName) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var (ownership, header) = await ProbeAsync(target, path, planId, rethrowUnavailable: true, ct).ConfigureAwait(false);
            // A folder whose manifest is ours is always listed; any other only when it is named like the plan.
            if (ownership is not (VersionOwnership.Owned or VersionOwnership.Renamed) &&
                !folderPlanName.Equals(planName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (await IsInTransitAsync(target, path, ct).ConfigureAwait(false))
                continue;

            var fileCount = header?.FileCount;
            var totalBytes = header?.TotalBytes;
            if (ownership == VersionOwnership.Owned && (fileCount is null || totalBytes is null))
            {
                try
                {
                    (fileCount, totalBytes) = await ManifestReader.ReadTotalsAsync(target,
                        StoragePath.Combine(path, VersionMarkerNames.Manifest), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is StorageException or JsonException)
                {
                    // The version is still ours; its size is just not known.
                }
            }

            versions.Add(new VersionInfo(name, path, localTime, ownership, fileCount, totalBytes));
        }

        versions.Sort((a, b) => a.LocalTime != b.LocalTime
            ? a.LocalTime.CompareTo(b.LocalTime)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return versions;
    }

    /// <summary>
    /// Whether a folder is a version of the plan: its manifest carries the plan's id and the folder is named after the
    /// plan name in that manifest (a ".partial" or ".deleting" suffix aside). A missing manifest (or folder) is
    /// <see cref="VersionOwnership.NoManifest"/>; a manifest that cannot be read, for whatever reason, is
    /// <see cref="VersionOwnership.Unreadable"/>. Throws only for a path that is not a valid storage path or on
    /// cancellation. The header is returned for owned folders only.
    /// </summary>
    /// <param name="versionPath">The folder's storage path, relative to the root of <paramref name="target"/>.</param>
    public static Task<(VersionOwnership Ownership, ManifestHeader? Header)> ProbeAsync(IStorage target,
        string versionPath, string planId, CancellationToken ct = default) =>
        ProbeAsync(target, versionPath, planId, rethrowUnavailable: false, ct);

    /// <summary><see cref="ProbeAsync(IStorage, string, string, CancellationToken)"/>; an unreachable target propagates when asked to.</summary>
    private static async Task<(VersionOwnership Ownership, ManifestHeader? Header)> ProbeAsync(IStorage target,
        string versionPath, string planId, bool rethrowUnavailable, CancellationToken ct)
    {
        try
        {
            var header = await ManifestReader.ReadHeaderAsync(target,
                StoragePath.Combine(versionPath, VersionMarkerNames.Manifest), ct).ConfigureAwait(false);
            if (planId.Length == 0 || !string.Equals(header.PlanId, planId, StringComparison.OrdinalIgnoreCase))
                return (VersionOwnership.Foreign, null);

            return HasNameOf(versionPath, header.PlanName)
                ? (VersionOwnership.Owned, header)
                : (VersionOwnership.Renamed, null);
        }
        catch (StorageNotFoundException)
        {
            return (VersionOwnership.NoManifest, null);
        }
        catch (Exception ex) when ((ex is StorageException or JsonException) &&
                                   !(rethrowUnavailable && ex is StorageUnavailableException))
        {
            return (VersionOwnership.Unreadable, null);
        }
    }

    /// <summary>
    /// True while the folder carries the marker of a run that is writing it or of a deletion in progress. A marker that
    /// cannot be looked at counts as absent: the probe has already told what the folder is.
    /// </summary>
    /// <exception cref="StorageUnavailableException">The target cannot be reached.</exception>
    private static async Task<bool> IsInTransitAsync(IStorage target, string versionPath, CancellationToken ct) =>
        await HasMarkerAsync(target, StoragePath.Combine(versionPath, VersionMarkerNames.Pending), ct).ConfigureAwait(false) ||
        await HasMarkerAsync(target, StoragePath.Combine(versionPath, VersionMarkerNames.Deleting), ct).ConfigureAwait(false);

    private static async Task<bool> HasMarkerAsync(IStorage target, string markerPath, CancellationToken ct)
    {
        try
        {
            return await target.StatAsync(markerPath, ct).ConfigureAwait(false) is { IsDirectory: false };
        }
        catch (StorageException ex) when (ex is not StorageUnavailableException)
        {
            return false;
        }
    }

    /// <summary>True when the folder's name is a timestamp followed by exactly <paramref name="manifestPlanName"/>.</summary>
    private static bool HasNameOf(string versionPath, string manifestPlanName)
    {
        var name = StoragePath.Name(versionPath);
        foreach (var suffix in (ReadOnlySpan<string>)[VersionName.PartialSuffix, VersionName.DeletingSuffix])
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return VersionName.TryParseAny(name, out _, out var folderPlanName) &&
               folderPlanName.Equals(manifestPlanName, StringComparison.OrdinalIgnoreCase);
    }
}
