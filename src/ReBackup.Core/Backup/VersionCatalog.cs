using System.Text.Json;

namespace ReBackup.Core.Backup;

public enum VersionOwnership
{
    /// <summary>The manifest carries the plan's id: retention manages this version.</summary>
    Owned,

    /// <summary>Named like a version of the plan, but there is no manifest.</summary>
    NoManifest,

    /// <summary>Named like a version of the plan, but the manifest carries another plan's id.</summary>
    Foreign,

    /// <summary>Named like a version of the plan, but the manifest cannot be read.</summary>
    Unreadable,
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
    /// The versions of a plan, oldest first. A folder belongs to the plan when its name starts with a timestamp
    /// and its manifest carries the plan's id, whatever plan name the folder ends with: versions made before a
    /// rename stay with the plan. Folders that only carry the plan's current name are listed as not owned.
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

            var (ownership, header) = Probe(directory, planId);
            if (ownership != VersionOwnership.Owned && !folderPlanName.Equals(planName, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>Whether the manifest in a folder carries the plan's id. Never throws. The header is returned for owned folders only.</summary>
    public static (VersionOwnership Ownership, ManifestHeader? Header) Probe(string versionDirectory, string planId)
    {
        try
        {
            var header = ManifestReader.ReadHeader(System.IO.Path.Combine(versionDirectory, VersionName.ManifestFileName));
            return planId.Length > 0 && string.Equals(header.PlanId, planId, StringComparison.OrdinalIgnoreCase)
                ? (VersionOwnership.Owned, header)
                : (VersionOwnership.Foreign, null);
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
}
