using ReBackup.Core.Backup;

namespace ReBackup.Core.Tests.TestSupport;

/// <summary>
/// Works on the real disk, with a switch to dictate the free space. Faults in removing versions are scripted on the
/// target's storage instead (<see cref="FaultyStorage"/>).
/// </summary>
public sealed class ScriptedVolume : ITargetVolume
{
    private readonly PhysicalTargetVolume _inner = new();

    /// <summary>Free space to report; the real value when null.</summary>
    public Func<long>? FreeSpace { get; init; }

    public long GetAvailableFreeSpace(string directory) => FreeSpace?.Invoke() ?? _inner.GetAvailableFreeSpace(directory);

    public Stream CreateFile(string path) => _inner.CreateFile(path);

    public void MoveDirectory(string source, string destination) => _inner.MoveDirectory(source, destination);

    public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);
}
