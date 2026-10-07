using System.Text;
using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Shared.Json;
using ReBackup.Storage;

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
        File.WriteAllText(Path.Combine(path, VersionName.ManifestFileName),
            ManifestJson(name, planId, bytes, withTotals, manifestPlanName));
        return path;
    }

    /// <summary>
    /// <see cref="Create(string, string, string, long, bool, string?)"/> in a storage: the folder <paramref name="name"/>
    /// directly under its root. Returns the storage path of the folder.
    /// </summary>
    public static async Task<string> CreateAsync(IStorage target, string name, string planId, long bytes = 10,
        bool withTotals = true, string? manifestPlanName = null)
    {
        await WriteAsync(target, StoragePath.Combine(name, "data.bin"), new byte[bytes]);
        await WriteAsync(target, StoragePath.Combine(name, VersionName.ManifestFileName),
            Encoding.UTF8.GetBytes(ManifestJson(name, planId, bytes, withTotals, manifestPlanName)));
        return name;
    }

    /// <summary>Writes and commits one file of a storage, replacing what is there.</summary>
    public static async Task WriteAsync(IStorage storage, string path, byte[] content)
    {
        await using var writer = await storage.CreateAsync(path, new CreateOptions(Overwrite: true), CancellationToken.None);
        await writer.WriteAsync(content);
        await writer.CommitAsync(CancellationToken.None);
    }

    private static string ManifestJson(string name, string planId, long bytes, bool withTotals, string? manifestPlanName)
    {
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
        return JsonSerializer.Serialize(manifest, JsonDefaults.Options);
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
