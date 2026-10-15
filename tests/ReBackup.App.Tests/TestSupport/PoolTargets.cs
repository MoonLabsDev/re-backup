using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using ReBackup.App.Services;
using ReBackup.Core.Backup;
using ReBackup.Core.Pool;
using ReBackup.Shared.Json;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests.TestSupport;

/// <summary>A file of a test pool version; <paramref name="Size"/> overrides the manifest's size (the blob stays small).</summary>
public sealed record PoolFile(string Path, string Content, long? Size = null);

/// <summary>Writes the pool of an incremental plan (store file, blobs, format 3 manifests) into an in-memory target.</summary>
public static class PoolTargets
{
    public static readonly DateTime Mtime = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    /// <summary>Writes <c>re-store.json</c> naming <paramref name="planId"/>.</summary>
    public static void WriteStore(InMemoryStorage target, string planId) =>
        target.AddFile(PoolLayout.StoreFile,
            Encoding.UTF8.GetBytes($$"""{"format":"pool","hash":"xxh128","version":1,"planId":"{{planId}}"}"""));

    /// <summary>The pool hash of <paramref name="content"/>'s UTF-8 bytes.</summary>
    public static string HashOf(string content) => PoolLayout.Format(XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(content)));

    /// <summary>Writes the version <paramref name="versionName"/>: a blob per content and a manifest listing the files.</summary>
    public static void WriteVersion(InMemoryStorage target, string versionName, string planId, string planName,
        params PoolFile[] files) => WriteVersion(target, versionName, planId, planName, @"C:\source", files);

    /// <inheritdoc cref="WriteVersion(InMemoryStorage, string, string, string, PoolFile[])"/>
    /// <param name="source">The manifest's source folder (where "restore to the original location" writes).</param>
    public static void WriteVersion(InMemoryStorage target, string versionName, string planId, string planName,
        string source, params PoolFile[] files)
    {
        var manifest = new BackupManifest
        {
            FormatVersion = BackupManifest.PoolFormatVersion,
            PlanId = planId,
            PlanName = planName,
            CreatedUtc = Mtime,
            Source = source,
        };
        var directories = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var bytes = Encoding.UTF8.GetBytes(file.Content);
            var hash = HashOf(file.Content);
            target.AddFile(PoolLayout.BlobPath(hash), bytes);
            manifest.Files.Add(new ManifestFile(file.Path, file.Size ?? bytes.Length, Mtime, hash));
            for (var dir = StoragePath.Parent(file.Path); dir.Length > 0; dir = StoragePath.Parent(dir))
                directories.Add(dir);
        }
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        manifest.Directories = [.. directories];
        target.AddFile(StoragePath.Combine(versionName, VersionMarkerNames.Manifest),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonDefaults.Options)));
    }
}

/// <summary>Records what the Versions tab asks of the temp opener; opens nothing.</summary>
public sealed class RecordingTempOpener : ITempOpener
{
    public List<(IStorage Versions, string Version, string Path)> Files { get; } = [];

    public List<(IStorage Versions, string Version, string Path)> Folders { get; } = [];

    public List<TimeSpan> CleanUps { get; } = [];

    /// <summary>What the opens return.</summary>
    public bool Result { get; set; } = true;

    public Task<bool> OpenFileAsync(IStorage versions, string versionName, string relativePath, CancellationToken ct)
    {
        Files.Add((versions, versionName, relativePath));
        return Task.FromResult(Result);
    }

    public Task<bool> OpenFolderAsync(IStorage versions, string versionName, string relativePath, CancellationToken ct)
    {
        Folders.Add((versions, versionName, relativePath));
        return Task.FromResult(Result);
    }

    public void CleanUp(TimeSpan olderThan) => CleanUps.Add(olderThan);
}
