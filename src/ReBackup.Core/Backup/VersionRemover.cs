namespace ReBackup.Core.Backup;

/// <summary>Removes version folders in a way that never leaves something that still looks like a complete version.</summary>
public static class VersionRemover
{
    /// <summary>
    /// Renames the folder to <c>&lt;name&gt;.deleting</c> and then deletes it. When the rename fails, nothing has
    /// changed. When the deletion fails half-way, the remains keep the ".deleting" name and are cleaned up by the
    /// next run of the plan.
    /// </summary>
    /// <exception cref="IOException">The folder is in use or the disk reports an error.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static void Remove(string versionPath, ITargetVolume volume)
    {
        RefuseLink(versionPath);
        var doomed = versionPath + VersionName.DeletingSuffix;
        volume.MoveDirectory(versionPath, doomed);
        RemoveRemains(doomed, volume);
    }

    /// <summary>
    /// Deletes a folder that already has the ".deleting" name. The manifest goes last: while it exists, the remains
    /// can still be attributed to a plan.
    /// </summary>
    public static void RemoveRemains(string doomedPath, ITargetVolume volume)
    {
        RefuseLink(doomedPath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(doomedPath).ToList())
        {
            if (Directory.Exists(entry))
                volume.DeleteDirectory(entry);
            else if (!Path.GetFileName(entry).Equals(VersionName.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                File.Delete(entry);
        }
        volume.DeleteDirectory(doomedPath);
    }

    /// <summary>A link would lead the deletion out of the target: its content is not ours to delete.</summary>
    private static void RefuseLink(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException($"\"{Path.GetFileName(path)}\" is a link and is not removed.");
    }
}
