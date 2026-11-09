using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Shared.Json;
using ReBackup.Storage;

namespace ReBackup.Core.Cleanup;

/// <summary>
/// What <see cref="VersionMarkerNames.Pruning"/> in a version folder says: a cleanup of plan <paramref name="PlanId"/>
/// on computer <paramref name="Host"/>, started at <paramref name="StartedUtc"/>, deletes <paramref name="Files"/> and
/// then the folders <paramref name="Folders"/> (deepest first) from the version. The paths are relative to the version
/// folder, with '/' separators.
/// </summary>
public sealed record PruningInfo(int FormatVersion, string PlanId, string Host, DateTime StartedUtc,
    IReadOnlyList<string> Files, IReadOnlyList<string> Folders);

/// <summary>
/// Writes, reads and removes the marker of a full-plan cleanup (spec 6.2). It is written before the version's manifest
/// is shortened and removed once the listed paths are deleted; a marker left behind tells the next start what is still
/// to delete.
/// </summary>
public static class PruningMarker
{
    /// <summary>The marker format this build writes.</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// Writes <c>&lt;version&gt;/re-pruning.json</c> durably, replacing one that is there: once this returns, the marker
    /// survives a crash, so the manifest may be shortened.
    /// </summary>
    public static async Task WriteAsync(IStorage target, string versionPath, PruningInfo info, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(info, JsonDefaults.Options);
        var writer = await target.CreateAsync(PathOf(versionPath), new CreateOptions(Overwrite: true, Durable: true), ct)
            .ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteAsync(bytes, ct).ConfigureAwait(false);
            await writer.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The marker of the version; <c>null</c> when there is none or it is not one this build can read (not JSON, no plan
    /// id, a format newer than <see cref="FormatVersion"/>). Other storage errors propagate.
    /// </summary>
    public static async Task<PruningInfo?> TryReadAsync(IStorage target, string versionPath, CancellationToken ct)
    {
        try
        {
            var stream = await target.OpenReadAsync(PathOf(versionPath), ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var info = await JsonSerializer.DeserializeAsync<PruningInfo>(stream, JsonDefaults.Options, ct)
                    .ConfigureAwait(false);
                // A newer format may mean something this build does not know: never act on it.
                return string.IsNullOrEmpty(info?.PlanId) || info.FormatVersion > FormatVersion ? null : info;
            }
        }
        catch (StorageNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Deletes the version's marker; a marker that is not there is fine.</summary>
    public static Task RemoveAsync(IStorage target, string versionPath, CancellationToken ct) =>
        target.DeleteAsync([PathOf(versionPath)], ct);

    private static string PathOf(string versionPath) => StoragePath.Combine(versionPath, VersionMarkerNames.Pruning);
}
