using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Core.Json;

namespace ReBackup.Core.Tests.TestSupport;

/// <summary>A file of a test version: relative path (forward slashes), content, last write time (UTC).</summary>
public sealed record TestFile(string Path, string Content, DateTime MtimeUtc);

/// <summary>Writes version folders with real files and a manifest that lists them with their xxHash64.</summary>
public static class VersionBuilder
{
    public const string PlanId = "plan1";
    public const string PlanName = "Projects";
    public const string Source = @"C:\source";
    public static readonly DateTime Mtime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TestFile File(string path, string content, DateTime? mtimeUtc = null) =>
        new(path, content, mtimeUtc ?? Mtime);

    /// <summary>Creates <c>&lt;target&gt;\&lt;minute&gt; Projects</c> with the files and, unless told not to, a manifest.</summary>
    public static string Write(string target, string minute, IEnumerable<TestFile> files, bool withManifest = true)
    {
        var folder = Path.Combine(target, $"{minute} {PlanName}");
        Directory.CreateDirectory(folder);
        var manifest = new BackupManifest
        {
            PlanId = PlanId,
            PlanName = PlanName,
            CreatedUtc = Mtime,
            Source = Source,
        };
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(file.Content);
            System.IO.File.WriteAllBytes(path, bytes);
            System.IO.File.SetLastWriteTimeUtc(path, file.MtimeUtc);
            manifest.Files.Add(new ManifestFile(file.Path, bytes.Length, file.MtimeUtc,
                "xxh64:" + Convert.ToHexStringLower(XxHash64.Hash(bytes))));
        }
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        if (withManifest)
        {
            System.IO.File.WriteAllText(Path.Combine(folder, VersionName.ManifestFileName),
                JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        }
        return folder;
    }

    /// <summary>The target's versions as the app lists them.</summary>
    public static IReadOnlyList<VersionInfo> List(string target) => VersionCatalog.List(target, PlanId, PlanName);
}
