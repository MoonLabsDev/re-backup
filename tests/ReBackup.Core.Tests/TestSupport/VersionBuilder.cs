using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReBackup.Core.Backup;
using ReBackup.Shared.Json;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;

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

    /// <summary>
    /// Creates <c>&lt;target&gt;\&lt;minute&gt; Projects</c> with the files and, unless told not to, a manifest.
    /// <paramref name="formatVersion"/> 1 writes the manifest as ReBackup 1.0.5 did: without <c>directories</c>.
    /// </summary>
    public static string Write(string target, string minute, IEnumerable<TestFile> files, bool withManifest = true,
        int formatVersion = 2)
    {
        var folder = Path.Combine(target, $"{minute} {PlanName}");
        Directory.CreateDirectory(folder);
        var manifest = new BackupManifest
        {
            FormatVersion = formatVersion,
            PlanId = PlanId,
            PlanName = PlanName,
            CreatedUtc = Mtime,
            Source = Source,
        };
        var directories = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(file.Content);
            System.IO.File.WriteAllBytes(path, bytes);
            System.IO.File.SetLastWriteTimeUtc(path, file.MtimeUtc);
            manifest.Files.Add(new ManifestFile(file.Path, bytes.Length, file.MtimeUtc,
                "xxh64:" + Convert.ToHexStringLower(XxHash64.Hash(bytes))));
            for (var dir = StoragePath.Parent(file.Path); dir.Length > 0; dir = StoragePath.Parent(dir))
                directories.Add(dir);
        }
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        manifest.Directories = [.. directories];
        if (withManifest)
            System.IO.File.WriteAllText(Path.Combine(folder, VersionName.ManifestFileName), Serialize(manifest));
        return folder;
    }

    /// <summary>The manifest as JSON; a format 1 manifest leaves <c>directories</c> out, as 1.0.5 did.</summary>
    public static string Serialize(BackupManifest manifest)
    {
        if (manifest.FormatVersion >= 2)
            return JsonSerializer.Serialize(manifest, JsonDefaults.Options);
        var node = JsonSerializer.SerializeToNode(manifest, JsonDefaults.Options)!.AsObject();
        node.Remove("directories");
        return node.ToJsonString(JsonDefaults.Options);
    }

    /// <summary>A file system storage on the target folder.</summary>
    public static IStorage TargetStorage(string target) => new FileSystemStorage(target);

    /// <summary>The target's versions as the app lists them.</summary>
    public static Task<IReadOnlyList<VersionInfo>> ListAsync(string target) =>
        VersionCatalog.ListAsync(TargetStorage(target), PlanId, PlanName);
}
