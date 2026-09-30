using System.Text.Json;

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

/// <summary>A version folder in a target. Only <see cref="VersionOwnership.Owned"/> versions are ever deleted.</summary>
public sealed record VersionInfo(string Name, string Path, DateTime LocalTime, VersionOwnership Ownership,
    int? FileCount, long? TotalBytes)
{
    public bool IsOwned => Ownership == VersionOwnership.Owned;
}

/// <summary>Finds the versions of a plan in its target folder.</summary>
public static class VersionCatalog
{
    /// <summary>
    /// The versions of a plan, oldest first. A folder belongs to the plan when its manifest carries the plan's id
    /// and its name is a timestamp followed by the plan name recorded in that manifest, i.e. the name the version
    /// was created under: versions made before the plan was renamed stay with the plan, while a folder that was
    /// copied or renamed by hand is listed as <see cref="VersionOwnership.Renamed"/> and is not owned. Folders
    /// that only carry the plan's current name are listed as not owned too. Folders that are links are left out.
    /// </summary>
    /// <exception cref="IOException">The target cannot be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the target is denied.</exception>
    public static IReadOnlyList<VersionInfo> List(string target, string planId, string planName,
        CancellationToken cancellationToken = default)
    {
        var versions = new List<VersionInfo>();
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
            return versions;

        foreach (var directory in Directory.EnumerateDirectories(target))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileName(directory);
            if (VersionName.IsTransient(name) || !VersionName.TryParseAny(name, out var localTime, out var folderPlanName))
                continue;

            // A junction or symbolic link is never a version: removing it would reach into the link's target.
            if (new DirectoryInfo(directory).LinkTarget is not null)
                continue;

            var (ownership, header) = Probe(directory, planId);
            // A folder whose manifest is ours is always listed; any other only when it is named like the plan.
            if (ownership is not (VersionOwnership.Owned or VersionOwnership.Renamed) &&
                !folderPlanName.Equals(planName, StringComparison.OrdinalIgnoreCase))
                continue;

            var fileCount = header?.FileCount;
            var totalBytes = header?.TotalBytes;
            if (ownership == VersionOwnership.Owned && (fileCount is null || totalBytes is null))
            {
                try
                {
                    (fileCount, totalBytes) = ManifestReader.ReadTotals(
                        System.IO.Path.Combine(directory, VersionName.ManifestFileName));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // The version is still ours; its size is just not known.
                }
            }

            versions.Add(new VersionInfo(name, directory, localTime, ownership, fileCount, totalBytes));
        }

        versions.Sort((a, b) => a.LocalTime != b.LocalTime
            ? a.LocalTime.CompareTo(b.LocalTime)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return versions;
    }

    /// <summary>
    /// Whether a folder is a version of the plan: its manifest carries the plan's id and the folder is named after
    /// the plan name in that manifest (a ".partial" or ".deleting" suffix aside). Never throws. The header is
    /// returned for owned folders only.
    /// </summary>
    public static (VersionOwnership Ownership, ManifestHeader? Header) Probe(string versionDirectory, string planId)
    {
        try
        {
            var header = ManifestReader.ReadHeader(System.IO.Path.Combine(versionDirectory, VersionName.ManifestFileName));
            if (planId.Length == 0 || !string.Equals(header.PlanId, planId, StringComparison.OrdinalIgnoreCase))
                return (VersionOwnership.Foreign, null);

            return HasNameOf(versionDirectory, header.PlanName)
                ? (VersionOwnership.Owned, header)
                : (VersionOwnership.Renamed, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (VersionOwnership.NoManifest, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return (VersionOwnership.Unreadable, null);
        }
    }

    /// <summary>True when the folder's name is a timestamp followed by exactly <paramref name="manifestPlanName"/>.</summary>
    private static bool HasNameOf(string versionDirectory, string manifestPlanName)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(versionDirectory));
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
