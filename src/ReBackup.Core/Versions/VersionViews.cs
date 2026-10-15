using System.Text.Json;
using ReBackup.Core.Pool;
using ReBackup.Storage;

namespace ReBackup.Core.Versions;

/// <summary>
/// Where a version's files are read from (spec 7, the <c>IVersionSource</c>): expressed as an <see cref="IStorage"/>
/// in which the version looks like a version folder of a full plan, so that the <see cref="Restorer"/>, file opening
/// and plan copy read every kind of version the same way.
/// </summary>
public static class VersionViews
{
    private const string PoolFormat = "pool";

    /// <summary>
    /// A read-only <see cref="PoolVersionView"/> of <paramref name="versionName"/> when <paramref name="target"/> holds a
    /// pool (its <see cref="PoolLayout.StoreFile"/> says so); otherwise <paramref name="target"/> itself, whose version
    /// folders hold the files.
    /// </summary>
    /// <exception cref="StorageException">The target cannot be read, or its store file is not a descriptor (<see cref="StorageIOException"/>).</exception>
    public static async Task<IStorage> OpenAsync(IStorage target, string versionName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        StoreDescriptor? store;
        try
        {
            store = await StoreGuard.ReadAsync(target, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new StorageIOException(PoolLayout.StoreFile, ex.Message, ex) { NotReadable = true };
        }
        return store is { Format: PoolFormat } ? new PoolVersionView(target, versionName) : target;
    }
}
