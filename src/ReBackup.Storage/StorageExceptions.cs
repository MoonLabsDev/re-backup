namespace ReBackup.Storage;

/// <summary>
/// Base of every error a storage reports. Implementations map their native errors (System.IO, HTTP, SDK) to one of the
/// subtypes, so callers catch only these types and never raw provider exceptions.
/// </summary>
public abstract class StorageException : IOException
{
    /// <summary>Creates the exception for the storage-relative <paramref name="path"/> the failed call was about.</summary>
    protected StorageException(string path, string message, Exception? inner)
        : base(message, inner)
    {
        Path = path;
    }

    /// <summary>The storage-relative path the failed call was about (<c>""</c> for the root).</summary>
    public string Path { get; }
}

/// <summary>The path or the root does not exist.</summary>
public sealed class StorageNotFoundException : StorageException
{
    public StorageNotFoundException(string path, string? message = null, Exception? inner = null)
        : base(path, message ?? $"Not found: '{path}'.", inner) { }
}

/// <summary>The caller lacks the permission for the call.</summary>
public sealed class StorageAccessDeniedException : StorageException
{
    public StorageAccessDeniedException(string path, string? message = null, Exception? inner = null)
        : base(path, message ?? $"Access denied: '{path}'.", inner) { }
}

/// <summary>The storage has no space left.</summary>
public sealed class StorageFullException : StorageException
{
    public StorageFullException(string path, string? message = null, Exception? inner = null)
        : base(path, message ?? $"No space left for '{path}'.", inner) { }
}

/// <summary>The root cannot be reached (drive or share offline, no connection).</summary>
public sealed class StorageUnavailableException : StorageException
{
    public StorageUnavailableException(string path, string? message = null, Exception? inner = null)
        : base(path, message ?? "The storage is not available.", inner) { }
}

/// <summary>An exclusive create hit an existing path, or a directory to delete is not empty.</summary>
public sealed class StorageConflictException : StorageException
{
    public StorageConflictException(string path, string? message = null, Exception? inner = null)
        : base(path, message ?? $"Conflict at '{path}'.", inner) { }
}

/// <summary>The file is in use by someone else.</summary>
public sealed class StorageLockedException : StorageException
{
    public StorageLockedException(string path, string? message = null, Exception? inner = null)
        : base(path, message ?? $"'{path}' is in use.", inner) { }
}
