using ReBackup.Storage.FileSystem;

namespace ReBackup.Storage;

/// <summary>Turns a <see cref="StorageLocation"/> into a storage bound to it.</summary>
public interface IStorageFactory
{
    /// <summary>
    /// The storage for <paramref name="location"/>. Nothing is touched yet, so an unreachable root surfaces on first use.
    /// Throws <see cref="NotSupportedException"/> for a kind this build does not know.
    /// </summary>
    IStorage Open(StorageLocation location);
}

/// <summary>Knows file system locations (<c>"fs"</c>) only.</summary>
public sealed class StorageFactory : IStorageFactory
{
    /// <inheritdoc />
    public IStorage Open(StorageLocation location) =>
        location.IsFileSystem
            ? new FileSystemStorage(location.Path)
            : throw new NotSupportedException($"Storage kind \"{location.Kind}\" is not supported.");
}
