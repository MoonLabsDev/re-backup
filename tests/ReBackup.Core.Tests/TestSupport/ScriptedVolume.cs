using ReBackup.Core.Backup;

namespace ReBackup.Core.Tests.TestSupport;

/// <summary>Works on the real disk, with switches to make renames or deletions fail and to dictate the free space.</summary>
public sealed class ScriptedVolume : ITargetVolume
{
    private readonly PhysicalTargetVolume _inner = new();

    /// <summary>Free space to report; the real value when null.</summary>
    public Func<long>? FreeSpace { get; init; }

    /// <summary>Gets the source path of a rename; true makes it fail.</summary>
    public Func<string, bool> FailMove { get; init; } = _ => false;

    /// <summary>Gets the path of a folder to delete; true makes it fail.</summary>
    public Func<string, bool> FailDelete { get; init; } = _ => false;

    public long GetAvailableFreeSpace(string directory) => FreeSpace?.Invoke() ?? _inner.GetAvailableFreeSpace(directory);

    public Stream CreateFile(string path) => _inner.CreateFile(path);

    public void MoveDirectory(string source, string destination)
    {
        if (FailMove(source))
            throw new IOException("the folder is in use");
        _inner.MoveDirectory(source, destination);
    }

    public void DeleteDirectory(string path)
    {
        if (FailDelete(path))
            throw new IOException("a file is in use");
        _inner.DeleteDirectory(path);
    }
}
