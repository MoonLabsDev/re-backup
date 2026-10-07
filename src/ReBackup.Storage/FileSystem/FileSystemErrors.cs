namespace ReBackup.Storage.FileSystem;

/// <summary>Maps System.IO errors to <see cref="StorageException"/> subtypes; the HResult checks the backup code used to do itself.</summary>
internal static class FileSystemErrors
{
    private const int ErrorNotReady = 21;
    private const int ErrorBadNetPath = 53;
    private const int ErrorUnexpNetError = 59;
    private const int ErrorNetNameDeleted = 64;
    private const int ErrorBadNetName = 67;
    private const int ErrorSemTimeout = 121;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorDiskFull = 112;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorDirNotEmpty = 145;

    /// <summary>
    /// The storage exception for <paramref name="ex"/>, or <c>null</c> when it is not one this storage maps (the caller then rethrows the original).
    /// <paramref name="path"/> is the storage-relative path of the failed call, <paramref name="rootPath"/> the absolute root of the storage.
    /// </summary>
    public static StorageException? Map(Exception ex, string path, string rootPath)
    {
        switch (ex)
        {
            case StorageException:
                return null;
            case FileNotFoundException or DirectoryNotFoundException:
                return IsRootReachable(rootPath)
                    ? new StorageNotFoundException(path, inner: ex)
                    : new StorageUnavailableException(path, UnavailableText(rootPath), ex);
            case UnauthorizedAccessException:
                return new StorageAccessDeniedException(path, inner: ex);
            case IOException io:
                return (io.HResult & 0xFFFF) switch
                {
                    ErrorDiskFull or ErrorHandleDiskFull => new StorageFullException(path, inner: io),
                    ErrorSharingViolation or ErrorLockViolation => new StorageLockedException(path, inner: io),
                    ErrorFileExists or ErrorAlreadyExists => new StorageConflictException(path, inner: io),
                    ErrorDirNotEmpty => new StorageConflictException(path, $"The directory '{path}' is not empty.", io),
                    ErrorNotReady or ErrorBadNetPath or ErrorUnexpNetError or ErrorNetNameDeleted or ErrorBadNetName
                        or ErrorSemTimeout or ErrorDeviceNotConnected => new StorageUnavailableException(path, UnavailableText(rootPath), io),
                    _ => IsRootReachable(rootPath)
                        ? new StorageIOException(path, inner: io)
                        : new StorageUnavailableException(path, UnavailableText(rootPath), io),
                };
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether the drive or share the root lives on exists (the root folder itself may not yet, e.g. a plan that has not run yet).
    /// Touches the disk.
    /// </summary>
    public static bool IsRootReachable(string rootPath)
    {
        try
        {
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(rootPath));
            return !string.IsNullOrEmpty(driveRoot) && Directory.Exists(driveRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Throws <see cref="StorageUnavailableException"/> when the drive or share of the root is gone.</summary>
    public static void ThrowIfRootUnreachable(string path, string rootPath)
    {
        if (!IsRootReachable(rootPath)) throw new StorageUnavailableException(path, UnavailableText(rootPath));
    }

    private static string UnavailableText(string rootPath) => $"The drive or share of '{rootPath}' is not available.";
}
