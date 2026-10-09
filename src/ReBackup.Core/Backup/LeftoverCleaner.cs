using System.Globalization;
using ReBackup.Core.Localization;
using ReBackup.Storage;

namespace ReBackup.Core.Backup;

/// <summary>
/// What a cleanup left for the run log: its <paramref name="Warnings"/> (in English) and the folders it left because
/// another computer marked them (<paramref name="ForeignMarked"/>, also among the warnings).
/// </summary>
public sealed record LeftoverCleanup(IReadOnlyList<string> Warnings, IReadOnlyList<ForeignMarkedFolder> ForeignMarked);

/// <summary>
/// Removes, at the start of a run, what earlier runs of the plan left behind in its target (spec 6.4): folders of runs
/// that never finished, deletions that were interrupted, and the ".partial" / ".deleting" folders of ReBackup 1.0.x.
/// </summary>
public static class LeftoverCleaner
{
    /// <summary>The markers a folder is looked at for, in the order the cleanup looks at them.</summary>
    private static readonly (string Name, MarkerKind Kind)[] MarkersInOrder =
        [(VersionMarkerNames.Deleting, MarkerKind.Deleting), (VersionMarkerNames.Pending, MarkerKind.Pending)];

    /// <summary>How the run log (English) writes the time a marker of another computer was written.</summary>
    private const string MarkerTimeFormat = "yyyy-MM-dd HH:mm 'UTC'";

    /// <summary>
    /// Looks at the direct children of the target's root that are folders (never links); markers are looked for only in
    /// folders named like a version:
    /// <list type="bullet">
    /// <item>a folder with <see cref="VersionMarkerNames.Deleting"/> of this plan: the deletion is finished;</item>
    /// <item>a folder with <see cref="VersionMarkerNames.Pending"/> of this plan that is not <paramref name="currentVersionName"/>:
    /// it is deleted, the marker last;</item>
    /// <item>a ".partial" folder named like a version of the plan (<paramref name="planName"/>) without a manifest: it is
    /// deleted; with a manifest it is left alone with a warning, and so is one that cannot be examined (silently, as before);</item>
    /// <item>a ".deleting" folder that is provably the plan's remains (its manifest is <see cref="VersionOwnership.Owned"/>, or it
    /// is empty and named like the plan): it is deleted;</item>
    /// <item>a marker another computer wrote (<see cref="MarkerInfo.IsOwn"/> is false for <paramref name="machineName"/>):
    /// the folder is left alone with a warning and listed in <see cref="LeftoverCleanup.ForeignMarked"/>, whatever plan it
    /// names, as that computer may still be writing or deleting it;</item>
    /// <item>a marker of another plan or one that cannot be read, or a marker of this plan in a folder that was renamed
    /// (named after neither <paramref name="planName"/> nor the marker's plan name): the folder is left alone with a warning;</item>
    /// <item>an empty folder without markers named like a version of the plan (a deletion whose very last step failed): it is deleted.</item>
    /// </list>
    /// A removal that fails is a warning (".partial" folders aside, as before); the next run tries again.
    /// </summary>
    /// <returns>The warnings for the run log, in English, and the folders other computers marked.</returns>
    /// <exception cref="StorageUnavailableException">The target cannot be reached.</exception>
    /// <exception cref="StorageException">The target's root cannot be listed.</exception>
    /// <exception cref="OperationCanceledException">Canceled; what was not removed yet stays for the next run.</exception>
    /// <param name="onFileDeleted">
    /// Called with the folder's name and 0 when its removal starts, then with the batch size after every batch of files.
    /// </param>
    /// <param name="machineName">This computer, as markers name it; <see cref="Environment.MachineName"/> when null.</param>
    public static async Task<LeftoverCleanup> CleanAsync(IStorage target, string planId, string planName,
        string? currentVersionName, CancellationToken ct, Action<string, int>? onFileDeleted = null, string? machineName = null)
    {
        machineName ??= Environment.MachineName;
        var warnings = new List<string>();
        var foreign = new List<ForeignMarkedFolder>();
        var folders = new List<string>();
        try
        {
            await foreach (var entry in target.ListAsync("", recursive: false, ct).ConfigureAwait(false))
            {
                // A link would lead the deletion out of the target: its content is not ours to delete.
                if (entry.IsDirectory && !entry.IsLink)
                    folders.Add(entry.Path);
            }
        }
        catch (StorageNotFoundException)
        {
            return new LeftoverCleanup(warnings, foreign);   // the target has not been created yet
        }

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            var name = StoragePath.Name(folder);
            Action<int>? progress = onFileDeleted is null ? null : count => onFileDeleted(name, count);
            if (name.EndsWith(VersionName.PartialSuffix, StringComparison.OrdinalIgnoreCase))
                await CleanLegacyPartialAsync(target, folder, planName, warnings, onFileDeleted, progress, ct).ConfigureAwait(false);
            else if (name.EndsWith(VersionName.DeletingSuffix, StringComparison.OrdinalIgnoreCase))
                await CleanLegacyDeletingAsync(target, folder, planId, planName, warnings, onFileDeleted, progress, ct).ConfigureAwait(false);
            else
                await CleanMarkedAsync(target, folder, planId, planName, currentVersionName, machineName, warnings, foreign,
                    onFileDeleted, progress, reportUnexamined: false, ct).ConfigureAwait(false);
        }
        return new LeftoverCleanup(warnings, foreign);
    }

    /// <summary>
    /// Takes over a folder another computer marked (a person decided that its run or deletion no longer goes on there):
    /// the folder is cleaned up as if this computer had written the marker, by the rules of <see cref="CleanAsync"/> for a
    /// folder named like a version, so a marker of another plan or a renamed folder is still left alone. The marker is
    /// read again first: when it is gone or no longer the one <paramref name="folder"/> describes (that computer started
    /// again), nothing is changed.
    /// </summary>
    /// <returns>The warnings, in English; empty when the folder was cleaned up.</returns>
    /// <exception cref="StorageUnavailableException">The target cannot be reached.</exception>
    /// <exception cref="OperationCanceledException">Canceled between batches; the marker stays, so the folder is left as before.</exception>
    public static async Task<IReadOnlyList<string>> TakeOverAsync(IStorage target, string planId, string planName,
        ForeignMarkedFolder folder, CancellationToken ct, Action<int>? onFileDeleted = null)
    {
        var warnings = new List<string>();
        var markerName = folder.Kind == MarkerKind.Pending ? VersionMarkerNames.Pending : VersionMarkerNames.Deleting;
        if (await ReadMarkerAsync(target, StoragePath.Combine(folder.Name, markerName), ct).ConfigureAwait(false) is not { } marker ||
            !string.Equals(marker.Host, folder.Host, StringComparison.OrdinalIgnoreCase) ||
            marker.StartedUtc != folder.StartedUtc)
        {
            warnings.Add(CoreTexts.English("core.run.takeOverChanged", ("name", folder.Name)));
            return warnings;
        }

        await CleanMarkedAsync(target, folder.Name, planId, planName, currentVersionName: null, machineName: folder.Host,
            warnings, foreign: [], onFileDeleted: null, progress: onFileDeleted, reportUnexamined: true, ct: ct).ConfigureAwait(false);
        return warnings;
    }

    /// <summary>
    /// The marker in a folder named like a version (the deletion marker before the pending one, as the cleanup looks at
    /// them) when another computer wrote it; null for any other folder or when that cannot be found out.
    /// </summary>
    internal static async Task<ForeignMarkedFolder?> ForeignMarkerOfAsync(IStorage target, string folder, string machineName,
        Func<MarkerInfo, string, bool> concerns, CancellationToken ct)
    {
        var name = StoragePath.Name(folder);
        if (!VersionName.TryParseAny(name, out _, out var folderPlanName))
            return null;
        foreach (var (markerName, kind) in MarkersInOrder)
        {
            var path = StoragePath.Combine(folder, markerName);
            switch (await ExistsAsync(target, path, ct).ConfigureAwait(false))
            {
                case null:
                    return null;
                case false:
                    continue;
            }
            return await ReadMarkerAsync(target, path, ct).ConfigureAwait(false) is { } marker &&
                   !marker.IsOwn(machineName) && concerns(marker, folderPlanName)
                ? new ForeignMarkedFolder(name, marker.Host, marker.StartedUtc, kind)
                : null;
        }
        return null;
    }

    /// <summary>
    /// A folder named like a version: a deletion to finish, a run that never finished, or the empty folder a deletion
    /// could not remove at its very end. Markers are trusted only in a folder that still carries the name of the plan
    /// it was written for: a folder renamed by hand is a person's, whatever marker it holds. A marker of another
    /// computer is never acted on: its run or deletion may still go on there.
    /// </summary>
    private static async Task CleanMarkedAsync(IStorage target, string folder, string planId, string planName,
        string? currentVersionName, string machineName, List<string> warnings, List<ForeignMarkedFolder> foreign,
        Action<string, int>? onFileDeleted, Action<int>? progress, bool reportUnexamined, CancellationToken ct)
    {
        var name = StoragePath.Name(folder);
        void Unexamined()
        {
            // The cleanup leaves such a folder silently for the next run; a take-over must say it did nothing.
            if (reportUnexamined)
                warnings.Add(CoreTexts.English("core.run.takeOverUnexamined", ("name", name)));
        }

        if (!VersionName.TryParseAny(name, out _, out var folderPlanName))
            return;   // not named like a version: not ours to look into

        var deleting = StoragePath.Combine(folder, VersionMarkerNames.Deleting);
        var pending = StoragePath.Combine(folder, VersionMarkerNames.Pending);
        string markerPath;
        if (await ExistsAsync(target, deleting, ct).ConfigureAwait(false) is not { } hasDeleting)
        {
            Unexamined();
            return;   // cannot be examined: left alone
        }
        if (hasDeleting)
        {
            markerPath = deleting;
        }
        else
        {
            switch (await ExistsAsync(target, pending, ct).ConfigureAwait(false))
            {
                case null:
                    Unexamined();
                    return;   // cannot be examined: left alone
                case false when string.Equals(name, currentVersionName, StringComparison.OrdinalIgnoreCase):
                    return;   // the folder the current run reserved, its marker maybe not visible yet: never remains
                case false:
                    await CleanEmptyRemainsAsync(target, folder, planName, folderPlanName, warnings, ct).ConfigureAwait(false);
                    return;
            }
            if (string.Equals(name, currentVersionName, StringComparison.OrdinalIgnoreCase))
                return;   // the folder the current run is writing
            markerPath = pending;
        }

        if (await ReadMarkerAsync(target, markerPath, ct).ConfigureAwait(false) is not { } marker)
        {
            warnings.Add(CoreTexts.English("core.run.leftoverUnreadable", ("name", name)));
            return;
        }
        if (!marker.IsOwn(machineName))
        {
            warnings.Add(CoreTexts.English(hasDeleting ? "core.run.leftoverOtherHostDeleting" : "core.run.leftoverOtherHostPending",
                ("name", name), ("host", marker.Host),
                ("time", FormatMarkerTime(marker.StartedUtc))));
            foreign.Add(new ForeignMarkedFolder(name, marker.Host, marker.StartedUtc,
                hasDeleting ? MarkerKind.Deleting : MarkerKind.Pending));
            return;
        }
        if (!string.Equals(marker.PlanId, planId, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(CoreTexts.English("core.run.leftoverForeign", ("name", name)));
            return;
        }
        if (!folderPlanName.Equals(planName, StringComparison.OrdinalIgnoreCase) &&
            !folderPlanName.Equals(marker.PlanName, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(CoreTexts.English("core.run.leftoverRenamed", ("name", name)));
            return;
        }

        onFileDeleted?.Invoke(name, 0);
        try
        {
            if (hasDeleting)
                await VersionRemover.FinishRemovalAsync(target, folder, progress, ct).ConfigureAwait(false);
            else
                await VersionRemover.RemoveUnfinishedAsync(target, folder, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is StorageException or ArgumentException) && ex is not StorageUnavailableException)
        {
            warnings.Add(CoreTexts.English(hasDeleting ? "core.run.remainsFailed" : "core.run.unfinishedFailed",
                ("name", name), ("error", ex.Message)));
        }
    }

    /// <summary>
    /// A folder without markers that is named like a version of the plan and holds nothing at all: what a deletion
    /// leaves when only the folder itself could not be removed. It is removed, as empty ".deleting" remains of the plan
    /// are. A folder with any content (a version, or anything a person put there) is left alone.
    /// </summary>
    private static async Task CleanEmptyRemainsAsync(IStorage target, string folder, string planName, string folderPlanName,
        List<string> warnings, CancellationToken ct)
    {
        if (!folderPlanName.Equals(planName, StringComparison.OrdinalIgnoreCase) ||
            await ExistsAsync(target, StoragePath.Combine(folder, VersionMarkerNames.Manifest), ct).ConfigureAwait(false) is not false ||
            !await IsEmptyAsync(target, folder, ct).ConfigureAwait(false))
            return;
        try
        {
            await target.DeleteAsync([folder], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is StorageException or ArgumentException) && ex is not StorageUnavailableException)
        {
            warnings.Add(CoreTexts.English("core.run.remainsFailed", ("name", StoragePath.Name(folder)), ("error", ex.Message)));
        }
    }

    /// <summary>
    /// An unfinished copy of ReBackup 1.0.x. Its manifest was written last, right before the rename: a folder that has one
    /// is a finished version of a plan whose name ends in ".partial" (or a crash just before the rename). Leaving it is the
    /// safe choice, and so is leaving a folder that cannot be examined.
    /// </summary>
    private static async Task CleanLegacyPartialAsync(IStorage target, string folder, string planName, List<string> warnings,
        Action<string, int>? onFileDeleted, Action<int>? progress, CancellationToken ct)
    {
        var name = StoragePath.Name(folder);
        if (!VersionName.TryParse(name[..^VersionName.PartialSuffix.Length], planName, out _))
            return;
        switch (await ExistsAsync(target, StoragePath.Combine(folder, VersionMarkerNames.Manifest), ct).ConfigureAwait(false))
        {
            case true:
                warnings.Add(CoreTexts.English("core.run.leftoverPartialWithManifest", ("name", name)));
                return;
            case null:
                return;
        }

        onFileDeleted?.Invoke(name, 0);
        try
        {
            await VersionRemover.RemoveLegacyFolderAsync(target, folder, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is StorageException or ArgumentException) && ex is not StorageUnavailableException)
        {
            // Best effort, as before; the next run of the plan tries again.
        }
    }

    /// <summary>The remains of a removal by ReBackup 1.0.x; removed only when they are provably the plan's own.</summary>
    private static async Task CleanLegacyDeletingAsync(IStorage target, string folder, string planId, string planName,
        List<string> warnings, Action<string, int>? onFileDeleted, Action<int>? progress, CancellationToken ct)
    {
        var name = StoragePath.Name(folder);
        if (!VersionName.TryParseAny(name[..^VersionName.DeletingSuffix.Length], out _, out var folderPlanName) ||
            !await IsOwnRemainsAsync(target, folder, planId, planName, folderPlanName, ct).ConfigureAwait(false))
            return;

        onFileDeleted?.Invoke(name, 0);
        try
        {
            await VersionRemover.RemoveLegacyFolderAsync(target, folder, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is StorageException or ArgumentException) && ex is not StorageUnavailableException)
        {
            warnings.Add(CoreTexts.English("core.run.remainsFailed", ("name", name), ("error", ex.Message)));
        }
    }

    /// <summary>
    /// Remains belong to the plan by their manifest. The manifest is deleted last, so remains without one that still
    /// hold anything are not ours by construction; empty ones are removed when the name matches. Everything else is
    /// left alone, also remains of a folder that was copied or renamed by hand (<see cref="VersionOwnership.Renamed"/>):
    /// retention never renamed that folder, a person did.
    /// </summary>
    private static async Task<bool> IsOwnRemainsAsync(IStorage target, string folder, string planId, string planName,
        string folderPlanName, CancellationToken ct)
    {
        var (ownership, _) = await VersionCatalog.ProbeAsync(target, folder, planId, ct).ConfigureAwait(false);
        return ownership switch
        {
            VersionOwnership.Owned => true,
            VersionOwnership.NoManifest =>
                folderPlanName.Equals(planName, StringComparison.OrdinalIgnoreCase) &&
                await IsEmptyAsync(target, folder, ct).ConfigureAwait(false),
            _ => false,
        };
    }

    /// <summary>False when the folder holds anything or cannot be examined.</summary>
    private static async Task<bool> IsEmptyAsync(IStorage target, string folder, CancellationToken ct)
    {
        try
        {
            await foreach (var _ in target.ListAsync(folder, recursive: false, ct).ConfigureAwait(false))
                return false;
            return true;
        }
        catch (StorageException ex) when (ex is not StorageUnavailableException)
        {
            return false;
        }
    }

    /// <summary>Whether a file is there; null when that cannot be found out. An unreachable target propagates.</summary>
    private static async Task<bool?> ExistsAsync(IStorage target, string path, CancellationToken ct)
    {
        try
        {
            return await target.StatAsync(path, ct).ConfigureAwait(false) is { IsDirectory: false };
        }
        catch (Exception ex) when ((ex is StorageException or ArgumentException) && ex is not StorageUnavailableException)
        {
            return null;
        }
    }

    /// <summary>The time a marker was written, for the run log; a time without a kind is taken as UTC, as markers write it.</summary>
    private static string FormatMarkerTime(DateTime startedUtc) =>
        (startedUtc.Kind == DateTimeKind.Local ? startedUtc.ToUniversalTime() : startedUtc)
        .ToString(MarkerTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>The marker; null when it cannot be read, for whatever reason short of an unreachable target.</summary>
    private static async Task<MarkerInfo?> ReadMarkerAsync(IStorage target, string path, CancellationToken ct)
    {
        try
        {
            return await VersionMarkers.TryReadAsync(target, path, ct).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex is not StorageUnavailableException)
        {
            return null;
        }
    }
}
