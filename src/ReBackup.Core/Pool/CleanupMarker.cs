using System.Text;
using System.Text.Json;
using ReBackup.Shared.Json;
using ReBackup.Storage;

namespace ReBackup.Core.Pool;

/// <summary>The content of a cleanup marker: which computer left a cleanup pending, since when.</summary>
public sealed record CleanupInfo(string Host, DateTime StartedUtc);

/// <summary>
/// <c>re-cleanup.&lt;host&gt;.json</c> in an incremental plan's target root, one per computer: versions were deleted by
/// hand there, and the pool still has to be collected. It is written under the "delete" lock before the first version is
/// deleted and removed only once the pool was collected; when it is still there at that computer's next app start (the
/// app was quit, crashed, or the target dropped), that computer finishes the cleanup (<see cref="Backup.DeletionResumer"/>).
/// Each computer only ever writes, reads and removes its own file: another computer's marker is never touched.
/// </summary>
public static class CleanupMarker
{
    private const string Prefix = "re-cleanup.";
    private const string Suffix = ".json";

    /// <summary>
    /// The marker's file name for <paramref name="host"/>: the host in lower case (markers compare host names whatever the
    /// case), every character other than a letter, a digit, '-' or '_' as '_'.
    /// </summary>
    public static string FileNameFor(string host)
    {
        var name = new StringBuilder(host.Length);
        foreach (var c in host.ToLowerInvariant())
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return Prefix + (name.Length == 0 ? "_" : name.ToString()) + Suffix;
    }

    /// <summary>Writes the marker of <paramref name="host"/>, replacing its own earlier one.</summary>
    /// <exception cref="StorageException">The target cannot be written.</exception>
    public static async Task WriteAsync(IStorage target, string host, DateTime startedUtc, CancellationToken ct)
    {
        var writer = await target.CreateAsync(FileNameFor(host), new CreateOptions(Overwrite: true, Durable: true), ct)
            .ConfigureAwait(false);
        await using (writer.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(writer, new CleanupInfo(host, startedUtc), JsonDefaults.Options, ct).ConfigureAwait(false);
            await writer.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Whether <paramref name="host"/> left a cleanup pending (its marker is there, whatever it holds).</summary>
    /// <exception cref="StorageException">The target cannot be read.</exception>
    public static async Task<bool> ExistsAsync(IStorage target, string host, CancellationToken ct)
    {
        try
        {
            return await target.StatAsync(FileNameFor(host), ct).ConfigureAwait(false) is { IsDirectory: false };
        }
        catch (StorageNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Removes the marker of <paramref name="host"/>; none is fine.</summary>
    /// <exception cref="StorageException">The target cannot be read or written.</exception>
    public static async Task RemoveOwnAsync(IStorage target, string host, CancellationToken ct)
    {
        if (await ExistsAsync(target, host, ct).ConfigureAwait(false))
            await target.DeleteAsync([FileNameFor(host)], ct).ConfigureAwait(false);
    }
}
