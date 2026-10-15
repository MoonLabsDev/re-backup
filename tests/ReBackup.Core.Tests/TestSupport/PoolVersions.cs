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
    /// </summary>
    public static void Write(InMemoryStorage target, string versionName, string planId, string planName,
        IEnumerable<(string Path, string Content)> files, Func<string, string>? hashCase = null)
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
        target.AddFile(StoragePath.Combine(versionName, VersionMarkerNames.Manifest),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonDefaults.Options)));
    }

    /// <summary>The blob paths in the pool, sorted.</summary>
    public static List<string> Blobs(InMemoryStorage target) =>
        target.Files.Where(f => f.StartsWith(PoolLayout.PoolFolder + "/", StringComparison.Ordinal)).Order().ToList();

    /// <summary>
    /// Restores a version the plain way (until Task 7's verified restore): reads its manifest from the target and every
    /// blob it names, checks each blob against its hash, and returns the files by path. Throws when anything is missing
    /// or does not match.
    /// </summary>
    public static async Task<Dictionary<string, string>> RestoreAsync(IStorage target, string versionPath)
    {
        var entries = new List<ManifestFile>();
        await using (var stream = await target.OpenReadAsync(StoragePath.Combine(versionPath, VersionMarkerNames.Manifest),
                         CancellationToken.None))
            ManifestStream.Read(stream, entries.Add);

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            await using var blob = await target.OpenReadAsync(PoolLayout.BlobPath(entry.Hash), CancellationToken.None);
            using var copy = new MemoryStream();
            await blob.CopyToAsync(copy);
            var bytes = copy.ToArray();
            var actual = PoolLayout.Format(XxHash128.HashToUInt128(bytes));
            if (!string.Equals(actual, entry.Hash, StringComparison.OrdinalIgnoreCase) || bytes.Length != entry.Size)
                throw new InvalidDataException($"The blob of '{entry.Path}' in '{versionPath}' does not match its manifest.");
            files[entry.Path] = Encoding.UTF8.GetString(bytes);
        }
        return files;
    }
}
