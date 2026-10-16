using System.Text.Json;
using ReBackup.Shared.Json;
using ReBackup.Storage;

namespace ReBackup.Core.Pool;

/// <summary>The content of <see cref="CleanupMarker.FileName"/>: which computer left a cleanup pending, since when.</summary>
public sealed record CleanupInfo(string Host, DateTime StartedUtc)
{
    /// <summary>Whether <paramref name="machineName"/> wrote it (whatever the case).</summary>
    public bool IsOwn(string machineName) => string.Equals(Host, machineName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// <c>re-cleanup.json</c> in an incremental plan's target root: versions were deleted by hand, and the pool still has to be
/// collected. It is written under the "delete" lock before the first version is deleted and removed only once the pool
/// was collected; when it is still there at the next app start (the app was quit, crashed, or the target dropped), that
/// computer finishes the cleanup (<see cref="Backup.DeletionResumer"/>). A marker of another computer is never acted on.
/// </summary>
public static class CleanupMarker
{
    public const string FileName = "re-cleanup.json";

    /// <summary>Writes the marker for <paramref name="host"/>, replacing one that is there.</summary>
    /// <exception cref="StorageException">The target cannot be written.</exception>
    public static async Task WriteAsync(IStorage target, string host, DateTime startedUtc, CancellationToken ct)
    {
        var writer = await target.CreateAsync(FileName, new CreateOptions(Overwrite: true, Durable: true), ct).ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(writer, new CleanupInfo(host, startedUtc), JsonDefaults.Options, ct).ConfigureAwait(false);
            await writer.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The marker; null when there is none. One that cannot be read as a marker counts as nobody's (host ""), so it is
    /// never acted on automatically.
    /// </summary>
    /// <exception cref="StorageException">The target cannot be read.</exception>
    public static async Task<CleanupInfo?> ReadAsync(IStorage target, CancellationToken ct)
    {
        if (await target.StatAsync(FileName, ct).ConfigureAwait(false) is null)
            return null;
        try
        {
            var stream = await target.OpenReadAsync(FileName, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var info = await JsonSerializer.DeserializeAsync<CleanupInfo>(stream, JsonDefaults.Options, ct).ConfigureAwait(false);
                return info is { Host: not null } ? info : new CleanupInfo("", DateTime.MinValue);
            }
        }
        catch (StorageNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            return new CleanupInfo("", DateTime.MinValue);
        }
    }

    /// <summary>Removes the marker when <paramref name="host"/> wrote it; a marker of another computer stays.</summary>
    /// <exception cref="StorageException">The target cannot be read or written.</exception>
    public static async Task RemoveOwnAsync(IStorage target, string host, CancellationToken ct)
    {
        if (await ReadAsync(target, ct).ConfigureAwait(false) is { } info && info.IsOwn(host))
            await target.DeleteAsync([FileName], ct).ConfigureAwait(false);
    }
}
