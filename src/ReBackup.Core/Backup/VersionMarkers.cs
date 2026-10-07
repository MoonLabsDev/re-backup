using System.Text.Json;
using ReBackup.Shared.Json;
using ReBackup.Storage;

namespace ReBackup.Core.Backup;

/// <summary>
/// What a marker in a version folder says: which plan's run (<see cref="VersionMarkerNames.Pending"/>) or deletion
/// (<see cref="VersionMarkerNames.Deleting"/>) it belongs to, since when and on which computer.
/// </summary>
public sealed record MarkerInfo(int FormatVersion, string PlanId, string PlanName, DateTime StartedUtc, string Host);

/// <summary>Writes and reads the marker files that keep a folder from counting as a version while it is written or deleted.</summary>
public static class VersionMarkers
{
    /// <summary>The marker format this build writes.</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// Creates <c>&lt;version&gt;/re-pending.json</c> exclusively: the folder name is reserved for the run.
    /// </summary>
    /// <exception cref="StorageConflictException">The marker exists already (another run holds the name).</exception>
    public static Task WritePendingAsync(IStorage target, string versionPath, MarkerInfo marker, CancellationToken ct) =>
        WriteAsync(target, StoragePath.Combine(versionPath, VersionMarkerNames.Pending), marker, overwrite: false, ct);

    /// <summary>Writes <c>&lt;version&gt;/re-deleting.json</c>, replacing an earlier one (a deletion that is retried).</summary>
    public static Task WriteDeletingAsync(IStorage target, string versionPath, MarkerInfo marker, CancellationToken ct) =>
        WriteAsync(target, StoragePath.Combine(versionPath, VersionMarkerNames.Deleting), marker, overwrite: true, ct);

    /// <summary>
    /// The marker at <paramref name="markerPath"/>; <c>null</c> when there is none or it is not a marker this build can
    /// read (not JSON, no plan id). Other storage errors propagate.
    /// </summary>
    public static async Task<MarkerInfo?> TryReadAsync(IStorage target, string markerPath, CancellationToken ct)
    {
        try
        {
            var stream = await target.OpenReadAsync(markerPath, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var marker = await JsonSerializer.DeserializeAsync<MarkerInfo>(stream, JsonDefaults.Options, ct).ConfigureAwait(false);
                return string.IsNullOrEmpty(marker?.PlanId) ? null : marker;
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

    private static async Task WriteAsync(IStorage target, string path, MarkerInfo marker, bool overwrite, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(marker, JsonDefaults.Options);
        var writer = await target.CreateAsync(path, new CreateOptions(Overwrite: overwrite), ct).ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteAsync(bytes, ct).ConfigureAwait(false);
            await writer.CommitAsync(ct).ConfigureAwait(false);
        }
    }
}
