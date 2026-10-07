namespace ReBackup.Core.Backup;

/// <summary>
/// Content of <c>re-manifest.json</c> in a version folder. Format 1 (up to 1.0.5) has no <see cref="Directories"/>;
/// format 2 adds them. Readers accept both.
/// </summary>
public sealed class BackupManifest
{
    /// <summary>The format this program writes.</summary>
    public const int CurrentFormatVersion = 2;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string PlanId { get; set; } = "";
    public string PlanName { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Source { get; set; } = "";
    /// <summary>Number of entries in <see cref="Files"/>. Stands in front of the list so that it can be read without it.</summary>
    public int? FileCount { get; set; }

    /// <summary>Sum of the sizes in <see cref="Files"/>.</summary>
    public long? TotalBytes { get; set; }

    public List<ManifestFile> Files { get; set; } = [];

    /// <summary>
    /// Every included folder of the version, relative and with forward slashes, so that empty folders can be restored
    /// from storages that have no real folders. Empty for format 1. Stands behind <see cref="Files"/>: readers of the
    /// header never have to get through it.
    /// </summary>
    public List<string> Directories { get; set; } = [];
}

/// <summary><paramref name="Path"/> is relative to the version folder, with forward slashes. <paramref name="Hash"/> is <c>xxh64:</c> plus 16 hex digits.</summary>
public sealed record ManifestFile(string Path, long Size, DateTime MtimeUtc, string Hash);
