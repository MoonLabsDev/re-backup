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
    public static void Remove(string versionPath, ITargetVolume volume)
    {
        RefuseLink(versionPath);
        var doomed = versionPath + VersionName.DeletingSuffix;
        volume.MoveDirectory(versionPath, doomed);
        try
        {
            RemoveRemains(doomed, volume);
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
    public static void RemoveRemains(string doomedPath, ITargetVolume volume)
    {
        RefuseLink(doomedPath);
        RemoveLinks(doomedPath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(doomedPath).ToList())
        {
            if (Directory.Exists(entry))
                volume.DeleteDirectory(entry);
            else if (!Path.GetFileName(entry).Equals(VersionName.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                File.Delete(entry);
        }
        volume.DeleteDirectory(doomedPath);
    }

    /// <summary>
    /// Removes every junction and directory link below the folder without touching its target, so that the
    /// recursive deletion afterwards only meets real folders. (That deletion would not follow a link either, but
    /// it fails on a junction further down: .NET then reports "access denied" after removing the junction.)
    /// </summary>
    private static void RemoveLinks(string directory)
    {
        foreach (var child in Directory.EnumerateDirectories(directory).ToList())
        {
            if (new DirectoryInfo(child).LinkTarget is not null)
                Directory.Delete(child);   // not recursive: the link only
            else
                RemoveLinks(child);
        }
    }

    /// <summary>A link would lead the deletion out of the target: its content is not ours to delete.</summary>
    private static void RefuseLink(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException($"\"{Path.GetFileName(path)}\" is a link and is not removed.");
    }
}
