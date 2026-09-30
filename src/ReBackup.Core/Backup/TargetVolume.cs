namespace ReBackup.Core.Backup;

/// <summary>The part of the file system a backup writes to. Replaced in tests to simulate a full disk.</summary>
public interface ITargetVolume
{
    /// <summary>Free bytes available for the folder; <see cref="long.MaxValue"/> when it cannot be determined.</summary>
    long GetAvailableFreeSpace(string directory);

    /// <summary>Creates a new file for writing; fails when it already exists.</summary>
    Stream CreateFile(string path);

    /// <summary>Renames a folder on the target.</summary>
    void MoveDirectory(string source, string destination);

    /// <summary>Removes a folder and everything in it.</summary>
    void DeleteDirectory(string path);
}

public sealed class PhysicalTargetVolume : ITargetVolume
{
    private const int BufferSize = 1024 * 1024;

    public long GetAvailableFreeSpace(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Network shares (UNC paths) have no drive letter: skip the preflight; a full disk is still caught while copying.
            return long.MaxValue;
        }
    }

    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

    public Stream CreateFile(string path) =>
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);
}
