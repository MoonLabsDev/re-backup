using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Core.Pool;
using ReBackup.Core.Versions;
using ReBackup.Shared.Json;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.TestSupport;

/// <summary>Writes pool versions (blobs plus a format 3 manifest) into a storage and reads them back.</summary>
public static class PoolVersions
{
    public static readonly DateTime Mtime = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The pool hash of <paramref name="text"/>'s UTF-8 bytes.</summary>
    public static string HashOf(string text) => PoolLayout.Format(XxHash128.HashToUInt128(Bytes(text)));

    /// <summary>The blob path of <paramref name="text"/>.</summary>
    public static string BlobOf(string text) => PoolLayout.BlobPath(HashOf(text));

    /// <summary>Puts the blob of <paramref name="text"/> into the pool (whether a manifest names it or not).</summary>
    public static void AddBlob(InMemoryStorage target, string text) => target.AddFile(BlobOf(text), Bytes(text));

    /// <summary>
    /// Writes the version <paramref name="versionName"/> of plan <paramref name="planId"/>: a blob for each content and a
    /// manifest that lists the files. <paramref name="hashCase"/> rewrites the hashes in the manifest (e.g. to upper case).
    /// <paramref name="directories"/> is the manifest's folder list (e.g. with empty folders).
    /// </summary>
    public static void Write(InMemoryStorage target, string versionName, string planId, string planName,
        IEnumerable<(string Path, string Content)> files, Func<string, string>? hashCase = null,
        IEnumerable<string>? directories = null)
    {
        var manifest = new BackupManifest
        {
            FormatVersion = BackupManifest.PoolFormatVersion,
            PlanId = planId,
            PlanName = planName,
            CreatedUtc = Mtime,
            Source = "C:\\source",
        };
        foreach (var (path, content) in files)
        {
            AddBlob(target, content);
            var hash = HashOf(content);
            manifest.Files.Add(new ManifestFile(path, Bytes(content).Length, Mtime, hashCase?.Invoke(hash) ?? hash));
        }
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        manifest.Directories = [.. directories ?? []];
        target.AddFile(StoragePath.Combine(versionName, VersionMarkerNames.Manifest),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonDefaults.Options)));
    }

    /// <summary>The blob paths in the pool, sorted.</summary>
    public static List<string> Blobs(InMemoryStorage target) =>
        target.Files.Where(f => f.StartsWith(PoolLayout.PoolFolder + "/", StringComparison.Ordinal)).Order().ToList();

    /// <summary>
    /// Restores a version of the pool in <paramref name="target"/> through <see cref="PoolVersionView"/> and the
    /// <see cref="Restorer"/> (which verifies every file against its hash) into memory, and returns the files by path.
    /// Throws when anything is missing or does not match.
    /// </summary>
    public static Task<Dictionary<string, string>> RestoreAsync(IStorage target, string versionPath) =>
        RestoreFromAsync(new PoolVersionView(target, versionPath), versionPath);

    /// <summary>Like <see cref="RestoreAsync"/>, reading the version through the view <paramref name="versions"/>.</summary>
    public static async Task<Dictionary<string, string>> RestoreFromAsync(IStorage versions, string versionPath)
    {
        var destination = new InMemoryStorage();
        var plan = await Restorer.PlanAsync(versions, versionPath, [""], destination, RestoreMode.Original);
        var result = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite);
        if (result.Failures.Count > 0)
            throw new InvalidDataException($"'{versionPath}' does not restore: " +
                string.Join("; ", result.Failures.Select(f => $"{f.Path}: {f.Reason}")));
        return destination.Files.ToDictionary(path => path, path => Encoding.UTF8.GetString(destination.ReadAllBytes(path)),
            StringComparer.Ordinal);
    }
}
