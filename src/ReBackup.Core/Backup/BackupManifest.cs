namespace ReBackup.Core.Backup;

/// <summary>Content of <c>re-manifest.json</c> in a version folder.</summary>
public sealed class BackupManifest
{
    public int FormatVersion { get; set; } = 1;
    public string PlanId { get; set; } = "";
    public string PlanName { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Source { get; set; } = "";
    /// <summary>Number of entries in <see cref="Files"/>. Stands in front of the list so that it can be read without it.</summary>
    public int? FileCount { get; set; }

    /// <summary>Sum of the sizes in <see cref="Files"/>.</summary>
    public long? TotalBytes { get; set; }

    public List<ManifestFile> Files { get; set; } = [];
}

/// <summary><paramref name="Path"/> is relative to the version folder, with forward slashes. <paramref name="Hash"/> is <c>xxh64:</c> plus 16 hex digits.</summary>
public sealed record ManifestFile(string Path, long Size, DateTime MtimeUtc, string Hash);
