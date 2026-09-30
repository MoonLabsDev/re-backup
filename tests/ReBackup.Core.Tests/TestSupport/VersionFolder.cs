using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Core.Json;

namespace ReBackup.Core.Tests.TestSupport;

public static class VersionFolder
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Creates a version folder holding one file of <paramref name="bytes"/> bytes and a manifest for <paramref name="planId"/>.</summary>
    public static string Create(string target, string name, string planId, long bytes = 10, bool withTotals = true)
    {
        var path = Path.Combine(target, name);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[bytes]);

        var manifest = new BackupManifest
        {
            PlanId = planId,
            PlanName = "Any",
            CreatedUtc = Stamp,
            Source = @"C:\source",
            Files = [new ManifestFile("data.bin", bytes, Stamp, "xxh64:0000000000000000")],
        };
        if (withTotals)
        {
            manifest.FileCount = 1;
            manifest.TotalBytes = bytes;
        }
        File.WriteAllText(Path.Combine(path, VersionName.ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        return path;
    }
}
