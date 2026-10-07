namespace ReBackup.Storage;

/// <summary>
/// A place backups are written to and read from, bound to one root location. All paths are relative to that root,
/// <c>/</c>-separated, without a leading <c>/</c>; <c>""</c> is the root. Native errors surface as <see cref="StorageException"/> subtypes.
/// </summary>
public interface IStorage
{
    /// <summary>What this storage can do beyond the basics.</summary>
    StorageCapabilities Capabilities { get; }

    /// <summary>The entry at <paramref name="path"/>, or <c>null</c> when nothing is there. Uncommitted files are not visible.</summary>
    Task<StorageEntry?> StatAsync(string path, CancellationToken ct);

    /// <summary>
    /// The entries below <paramref name="folder"/> (direct children, or everything below when <paramref name="recursive"/>), in no guaranteed order.
    /// Entries carry full storage-relative paths. Throws <see cref="StorageNotFoundException"/> when the folder does not exist (the root always does).
    /// The folder is validated and the view taken when enumeration starts, so errors surface on the first <c>MoveNextAsync</c>; the results may or may not reflect changes made during the enumeration.
    /// </summary>
    IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive, CancellationToken ct);

    /// <summary>Opens a file for reading. Throws <see cref="StorageNotFoundException"/> when it does not exist.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);

    /// <summary>
    /// Starts writing a file. With <see cref="CreateOptions.Overwrite"/> false the call throws <see cref="StorageConflictException"/> when the path exists,
    /// and the commit throws it too when someone committed the path meanwhile. With overwrite, the old content stays visible until the commit.
    /// </summary>
    Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct);

    /// <summary>
    /// Deletes files and empty directories; never recursively. Missing paths are not an error.
    /// Throws <see cref="StorageConflictException"/> for a directory that is not empty.
    /// Not atomic across the batch: paths deleted before one that throws stay deleted.
    /// </summary>
    Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary>Makes sure the directory (and its parents) exists; a no-op on storages without real directories.</summary>
    Task EnsureDirectoryAsync(string path, CancellationToken ct);

    /// <summary>The free bytes, or <c>null</c> when unknown.</summary>
    Task<long?> GetFreeSpaceAsync(CancellationToken ct);
}

/// <summary>What a storage supports beyond the basics.</summary>
[Flags]
public enum StorageCapabilities
{
    None = 0,
    FreeSpace = 1,
    Links = 2,
    EmptyDirectories = 4,
    SetModifiedTime = 8,
    CaseSensitive = 16,
}

/// <summary>
/// One file or directory in a storage. <see cref="Stamp"/> is an opaque change token (<c>null</c> for directories):
/// it differs whenever the file's content changed.
/// </summary>
public sealed record StorageEntry(string Path, bool IsDirectory, long Size, DateTime ModifiedUtc, bool IsLink, string? Stamp);

/// <summary>How <see cref="IStorage.CreateAsync"/> creates a file.</summary>
public sealed record CreateOptions(bool Overwrite = false, DateTime? ModifiedUtc = null);
