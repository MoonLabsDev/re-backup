namespace ReBackup.Core.Backup;

public enum VersionDeletionOutcome
{
    /// <summary>The folder is gone.</summary>
    Deleted,

    /// <summary>The folder no longer is a version, but its ".deleting" remains are left; the next run removes them.</summary>
    RemainsLeft,

    /// <summary>There was no such folder (any more).</summary>
    Gone,

    /// <summary>The folder is not a version of the plan (<see cref="VersionOwnership.Owned"/>): nothing was changed.</summary>
    NotManaged,

    /// <summary>The folder could not be removed: nothing was changed.</summary>
    Failed,
}

/// <summary>What happened to one version a person asked to delete; <see cref="Error"/> for RemainsLeft and Failed.</summary>
public sealed record VersionDeletion(string Name, VersionDeletionOutcome Outcome, string? Error);

/// <summary>Deletes versions a person picked by hand, with the same care retention takes.</summary>
public static class VersionDeleter
{
    /// <summary>
    /// Deletes the plan's versions <paramref name="versionNames"/> (folder names directly in <paramref name="target"/>)
    /// one after the other. Each folder is examined again right before it is removed: only a folder that is still
    /// <see cref="VersionOwnership.Owned"/> by the plan is deleted, whatever the caller listed. Never throws for a
    /// single version; cancellation throws between versions.
    /// </summary>
    public static IReadOnlyList<VersionDeletion> Delete(string target, string planId, IReadOnlyList<string> versionNames,
        ITargetVolume? volume = null, CancellationToken cancellationToken = default)
    {
        volume ??= new PhysicalTargetVolume();
        var results = new List<VersionDeletion>(versionNames.Count);
        foreach (var name in versionNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(DeleteOne(target, planId, name, volume));
        }
        return results;
    }

    private static VersionDeletion DeleteOne(string target, string planId, string name, ITargetVolume volume)
    {
        // Only a plain folder name of a version: nothing that leads elsewhere, no ".partial" or ".deleting" folder.
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name is "." or ".." ||
            VersionName.IsTransient(name) || !VersionName.TryParseAny(name, out _, out _))
            return new VersionDeletion(name, VersionDeletionOutcome.NotManaged, null);

        var path = Path.Combine(target, name);
        try
        {
            if (!Directory.Exists(path))
                return new VersionDeletion(name, VersionDeletionOutcome.Gone, null);
            if (new DirectoryInfo(path).LinkTarget is not null ||
                VersionCatalog.Probe(path, planId).Ownership != VersionOwnership.Owned)
                return new VersionDeletion(name, VersionDeletionOutcome.NotManaged, null);

            VersionRemover.Remove(path, volume);
            return new VersionDeletion(name, VersionDeletionOutcome.Deleted, null);
        }
        catch (VersionRemainsException ex)
        {
            return new VersionDeletion(name, VersionDeletionOutcome.RemainsLeft, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Directory.Exists(path)
                ? new VersionDeletion(name, VersionDeletionOutcome.Failed, ex.Message)
                : new VersionDeletion(name, VersionDeletionOutcome.Gone, null);
        }
    }
}
