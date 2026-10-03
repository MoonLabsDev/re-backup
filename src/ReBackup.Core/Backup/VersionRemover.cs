namespace ReBackup.Core.Backup;

/// <summary>The version was renamed to its ".deleting" name, but deleting the remains failed.</summary>
public sealed class VersionRemainsException(string remainsPath, Exception inner)
    : IOException(inner.Message, inner)
{
    public string RemainsPath { get; } = remainsPath;
}

/// <summary>Removes version folders in a way that never leaves something that still looks like a complete version.</summary>
public static class VersionRemover
{
    /// <summary>
    /// Renames the folder to <c>&lt;name&gt;.deleting</c> and then deletes it. When the rename fails, nothing has
    /// changed. When the deletion fails half-way, the remains keep the ".deleting" name and are cleaned up by the
    /// next run of the plan.
    /// </summary>
    /// <exception cref="VersionRemainsException">The folder no longer is a version, but its remains could not be deleted.</exception>
    /// <exception cref="IOException">The folder is a link, is in use or is gone, or the disk reports an error: nothing was changed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied: nothing was changed.</exception>
    /// <param name="onFileDeleted">Called after every deleted file (the manifest aside), on the calling thread.</param>
    public static void Remove(string versionPath, ITargetVolume volume, Action<int>? onFileDeleted = null)
    {
        RefuseLink(versionPath);
        var doomed = versionPath + VersionName.DeletingSuffix;
        volume.MoveDirectory(versionPath, doomed);
        try
        {
            RemoveRemains(doomed, volume, onFileDeleted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VersionRemainsException(doomed, ex);
        }
    }

    /// <summary>
    /// Deletes a folder that already has the ".deleting" name. The manifest goes last: while it exists, the remains
    /// can still be attributed to a plan. Links inside the folder are removed as links; what they point to stays.
    /// </summary>
    /// <param name="onFileDeleted">Called with 1 after every deleted file (the manifest aside), on the calling thread.</param>
    public static void RemoveRemains(string doomedPath, ITargetVolume volume, Action<int>? onFileDeleted = null)
    {
        RefuseLink(doomedPath);
        foreach (var entry in new DirectoryInfo(doomedPath).EnumerateFileSystemInfos().ToList())
        {
            if (entry is DirectoryInfo directory)
            {
                RemoveTree(directory, volume, onFileDeleted);
            }
            else if (!entry.Name.Equals(VersionName.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                entry.Delete();
                onFileDeleted?.Invoke(1);
            }
        }
        volume.DeleteDirectory(doomedPath);
    }

    /// <summary>
    /// Deletes a folder that is not a version (e.g. an unfinished ".partial" copy) with everything in it, file by file;
    /// links inside it are removed as links. The folder itself must not be a link.
    /// </summary>
    /// <param name="onFileDeleted">Called with 1 after every deleted file, on the calling thread.</param>
    public static void RemoveFolder(string path, ITargetVolume volume, Action<int>? onFileDeleted = null)
    {
        RefuseLink(path);
        RemoveTree(new DirectoryInfo(path), volume, onFileDeleted);
    }

    /// <summary>
    /// Deletes a folder file by file in one pass, so that the progress can be reported from the first file on; each
    /// folder is handed to the volume once it is empty. A junction or directory link is removed as a link and never
    /// entered: what it points to stays. (A recursive deletion would not follow it either, but fails on a junction
    /// further down: .NET then reports "access denied" after removing the junction.)
    /// </summary>
    private static void RemoveTree(DirectoryInfo directory, ITargetVolume volume, Action<int>? onFileDeleted)
    {
        // The attributes come with the listing; only reparse points need the extra look at their link target.
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 && directory.LinkTarget is not null)
        {
            Directory.Delete(directory.FullName);   // not recursive: the link only
            return;
        }

        foreach (var entry in directory.EnumerateFileSystemInfos().ToList())
        {
            if (entry is DirectoryInfo child)
            {
                RemoveTree(child, volume, onFileDeleted);
            }
            else
            {
                entry.Delete();
                onFileDeleted?.Invoke(1);
            }
        }
        volume.DeleteDirectory(directory.FullName);
    }

    /// <summary>A link would lead the deletion out of the target: its content is not ours to delete.</summary>
    private static void RefuseLink(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException($"\"{Path.GetFileName(path)}\" is a link and is not removed.");
    }
}
