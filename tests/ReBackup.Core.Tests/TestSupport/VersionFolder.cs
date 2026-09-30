using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Core.Json;

namespace ReBackup.Core.Tests.TestSupport;

public static class VersionFolder
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Creates a version folder holding one file of <paramref name="bytes"/> bytes and a manifest for
    /// <paramref name="planId"/>. The manifest records the plan name the folder is named after (the part behind the
    /// timestamp, without ".partial" / ".deleting"), as a real run does; <paramref name="manifestPlanName"/>
    /// overrides that, e.g. for a folder that was copied or renamed by hand.
    /// </summary>
    public static string Create(string target, string name, string planId, long bytes = 10, bool withTotals = true,
        string? manifestPlanName = null)
    {
        var path = Path.Combine(target, name);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[bytes]);

        var manifest = new BackupManifest
        {
            PlanId = planId,
            PlanName = manifestPlanName ?? NamePart(name),
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

    private static string NamePart(string folderName)
    {
        foreach (var suffix in new[] { VersionName.PartialSuffix, VersionName.DeletingSuffix })
        {
            if (folderName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                folderName = folderName[..^suffix.Length];
                break;
            }
        }
        return VersionName.TryParseAny(folderName, out _, out var planName) ? planName : "Any";
    }
}
