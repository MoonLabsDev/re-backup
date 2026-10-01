using System.Globalization;

namespace ReBackup.Core.Backup;

/// <summary>Names of version folders in a target: <c>YYYY_MM_DD-hh_mm PlanName</c>.</summary>
public static class VersionName
{
    public const string PartialSuffix = ".partial";
    /// <summary>Suffix of a version folder that retention is removing.</summary>
    public const string DeletingSuffix = ".deleting";
    public const string ManifestFileName = "re-manifest.json";
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
    /// the target is an absolute path and the name is one plain folder name (no separators, no "." or "..", no
    /// characters a file name cannot hold, no leading or trailing blanks or dots). Does not touch the disk.
    /// </summary>
    public static string? FolderIn(string? target, string? versionName)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target))
            return null;
        if (string.IsNullOrWhiteSpace(versionName) || versionName is "." or ".." ||
            versionName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            versionName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, Path.VolumeSeparatorChar]) >= 0 ||
            versionName != versionName.Trim() || versionName.EndsWith('.'))
            return null;

        var folder = Path.GetFullPath(Path.Combine(target, versionName));
        var parent = Path.GetDirectoryName(folder);
        return parent is not null && string.Equals(Path.TrimEndingDirectorySeparator(parent),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase)
            ? folder
            : null;
    }

    /// <summary>
    /// The names of the folders directly inside <paramref name="target"/>, read in one listing (case-insensitive set);
    /// empty when the target is not an absolute path, does not exist or cannot be read. Can block for a long time on an
    /// unreachable network share: call it off the UI thread.
    /// </summary>
    public static IReadOnlySet<string> FolderNamesIn(string? target)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target))
            return names;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(target))
                names.Add(Path.GetFileName(directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            names.Clear();
        }
        return names;
    }

    /// <summary><see cref="FolderIn"/> when that folder exists right now; otherwise null.</summary>
    public static string? ExistingFolderIn(string? target, string? versionName) =>
        FolderIn(target, versionName) is { } folder && Directory.Exists(folder) ? folder : null;

    /// <summary>True for folders that are being written (".partial") or removed (".deleting"); they are never versions.</summary>
    public static bool IsTransient(string folderName) =>
        folderName.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase) ||
        folderName.EndsWith(DeletingSuffix, StringComparison.OrdinalIgnoreCase);
}
