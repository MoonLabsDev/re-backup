using ReBackup.Core.Backup;
using System.Text.Json;
using ReBackup.Core.Versions;
using ReBackup.Storage;

namespace ReBackup.Core.Pool;

/// <summary>What <see cref="PoolManifests.TryReadAsync"/> found in a folder.</summary>
internal enum ManifestRead
{
    /// <summary>The folder has no manifest (or a name the storage cannot address): it is not a version.</summary>
    None,

    /// <summary>The manifest was read to the end.</summary>
    Read,

    /// <summary>The manifest could not be looked at or read, for whatever reason.</summary>
    Unreadable,
}

/// <summary>How the collector and the verifier find the versions of a pool and read what their manifests reference.</summary>
internal static class PoolManifests
{
    /// <summary>The folders directly in the target's root that may be versions: no links, not the pool.</summary>
    public static async Task<List<string>> RootFoldersAsync(IStorage target, CancellationToken ct)
    {
        var folders = new List<string>();
        try
        {
            await foreach (var entry in target.ListAsync("", recursive: false, ct).ConfigureAwait(false))
            {
                if (entry.IsDirectory && !entry.IsLink &&
                    !StoragePath.Name(entry.Path).Equals(PoolLayout.PoolFolder, StringComparison.OrdinalIgnoreCase))
                    folders.Add(entry.Path);
            }
        }
        catch (StorageNotFoundException)
        {
            // An empty target.
        }
        return folders;
    }

    /// <summary>
    /// Hands every file entry of the <see cref="VersionMarkerNames.Manifest"/> in <paramref name="folder"/> to
    /// <paramref name="onFile"/>. <paramref name="onFile"/> may already have seen entries when the result is
    /// <see cref="ManifestRead.Unreadable"/>.
    /// </summary>
    public static async Task<ManifestRead> TryReadAsync(IStorage target, string folder, Action<ManifestFile> onFile,
        CancellationToken ct)
    {
        string manifest;
        try
        {
            manifest = StoragePath.Combine(folder, VersionMarkerNames.Manifest);
            if (await target.StatAsync(manifest, ct).ConfigureAwait(false) is null)
                return ManifestRead.None;
        }
        catch (ArgumentException)
        {
            return ManifestRead.None;
        }
        catch (StorageException)
        {
            return ManifestRead.Unreadable;
        }

        try
        {
            var stream = await target.OpenReadAsync(manifest, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
                ManifestStream.Read(stream, onFile, ct);
            return ManifestRead.Read;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            return ManifestRead.Unreadable;   // StorageException is an IOException; a manifest that vanished meanwhile is unreadable too
        }
    }
}
