using System.Globalization;
using ReBackup.Storage;

namespace ReBackup.Core.Backup;

/// <summary>
/// The marker files of a version folder. A folder is a complete version only while it holds the manifest and neither
/// of the other two: <see cref="Pending"/> while a run is writing it, <see cref="Deleting"/> while it is removed.
/// </summary>
public static class VersionMarkerNames
{
    public const string Manifest = "re-manifest.json", Pending = "re-pending.json", Deleting = "re-deleting.json";
}

/// <summary>Names of version folders in a target: <c>YYYY_MM_DD-hh_mm PlanName</c>.</summary>
public static class VersionName
{
    public const string PartialSuffix = ".partial";
    /// <summary>Suffix of a version folder that retention is removing.</summary>
    public const string DeletingSuffix = ".deleting";
    public const string ManifestFileName = VersionMarkerNames.Manifest;
    private const string TimestampFormat = "yyyy_MM_dd-HH_mm";

    public static string Format(DateTime localTime, string planName) =>
        localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture) + " " + planName;

    /// <summary>True when the folder name is exactly a timestamp, one space and the plan name.</summary>
    public static bool TryParse(string folderName, string planName, out DateTime localTime)
    {
        localTime = default;
        var stampLength = TimestampFormat.Length;
        if (folderName.Length != stampLength + 1 + planName.Length || folderName[stampLength] != ' ')
            return false;
        if (!folderName.AsSpan(stampLength + 1).Equals(planName, StringComparison.OrdinalIgnoreCase))
            return false;
        return DateTime.TryParseExact(folderName.AsSpan(0, stampLength), TimestampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out localTime);
    }

    /// <summary>True when the folder name is a timestamp, one space and any non-empty name.</summary>
    public static bool TryParseAny(string folderName, out DateTime localTime, out string planName)
    {
        localTime = default;
        planName = "";
        var stampLength = TimestampFormat.Length;
        if (folderName.Length <= stampLength + 1 || folderName[stampLength] != ' ')
            return false;
        if (!DateTime.TryParseExact(folderName.AsSpan(0, stampLength), TimestampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out localTime))
            return false;

        planName = folderName[(stampLength + 1)..];
        return true;
    }

    /// <summary>
    /// The full path of the folder <paramref name="versionName"/> directly inside <paramref name="target"/>; null unless
    /// the target is an absolute path and the name is one plain folder name (see <see cref="IsPlainFolderName"/>). Does
    /// not touch the disk.
    /// </summary>
    public static string? FolderIn(string? target, string? versionName)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target) || !IsPlainFolderName(versionName))
            return null;

        var folder = Path.GetFullPath(Path.Combine(target, versionName));
        var parent = Path.GetDirectoryName(folder);
        return parent is not null && string.Equals(Path.TrimEndingDirectorySeparator(parent),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase)
            ? folder
            : null;
    }

    /// <summary>
    /// True for one plain folder name: no separators, no "." or "..", no characters a file name cannot hold, no leading or
    /// trailing blanks or dots.
    /// </summary>
    public static bool IsPlainFolderName([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? versionName) =>
        !string.IsNullOrWhiteSpace(versionName) && versionName is not ("." or "..") &&
        versionName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        versionName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, Path.VolumeSeparatorChar]) < 0 &&
        versionName == versionName.Trim() && !versionName.EndsWith('.');

    /// <summary>
    /// The names of the folders directly under the root of <paramref name="target"/>, read in one listing
    /// (case-insensitive set); empty when there is no target or it does not exist or cannot be read. Can take long on an
    /// unreachable network share.
    /// </summary>
    public static async Task<IReadOnlySet<string>> FolderNamesInAsync(IStorage? target, CancellationToken ct = default)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (target is null)
            return names;
        try
        {
            await foreach (var entry in target.ListAsync("", recursive: false, ct).ConfigureAwait(false))
            {
                if (entry.IsDirectory)
                    names.Add(StoragePath.Name(entry.Path));
            }
        }
        catch (StorageException)
        {
            names.Clear();
        }
        return names;
    }

    /// <summary>
    /// The storage path of the folder <paramref name="versionName"/> directly under the root of <paramref name="target"/>
    /// when the name is one plain folder name (<see cref="IsPlainFolderName"/>) and that folder exists right now;
    /// otherwise (also when the target cannot be read) null.
    /// </summary>
    public static async Task<string?> ExistingFolderInAsync(IStorage target, string? versionName, CancellationToken ct = default)
    {
        if (!IsPlainFolderName(versionName))
            return null;
        try
        {
            return await target.StatAsync(versionName, ct).ConfigureAwait(false) is { IsDirectory: true } ? versionName : null;
        }
        catch (StorageException)
        {
            return null;
        }
    }

    /// <summary>True for folders that are being written (".partial") or removed (".deleting"); they are never versions.</summary>
    public static bool IsTransient(string folderName) =>
        folderName.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase) ||
        folderName.EndsWith(DeletingSuffix, StringComparison.OrdinalIgnoreCase);
}
