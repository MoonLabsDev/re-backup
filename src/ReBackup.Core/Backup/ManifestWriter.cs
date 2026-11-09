using System.Text.Json;
using ReBackup.Shared.Json;
using ReBackup.Storage;

namespace ReBackup.Core.Backup;

/// <summary>Replaces the manifest of a finished version on the target (a retry adds files, a cleanup removes them).</summary>
internal static class ManifestWriter
{
    /// <summary>
    /// Replaces a version's manifest with <paramref name="manifest"/>, conditionally on <paramref name="expectedStamp"/> (the stamp it
    /// had when it was read; <see cref="StorageConflictException"/> when it was replaced meanwhile). Durable, because the
    /// manifest is what the version is; written to a temporary file and moved over the old one on commit.
    /// </summary>
    internal static async Task ReplaceAsync(IStorage target, string manifestPath, BackupManifest manifest,
        string? expectedStamp, CancellationToken cancellationToken)
    {
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
        var writer = await target.CreateAsync(manifestPath,
            new CreateOptions(Overwrite: true, Durable: true, ExpectedStamp: expectedStamp), cancellationToken)
            .ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(writer, manifest, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
            await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
