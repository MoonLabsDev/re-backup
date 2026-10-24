using ReBackup.Core.Backup;
using ReBackup.Core.Ignore;
using ReBackup.Core.Plans;
using ReBackup.Core.Versions;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>What the control service reads and changes besides the plan editors. Call on the UI thread unless noted.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The plans' version indexes; queries may run on any thread, beside a sync.</summary>
    internal VersionIndexSet VersionIndexes => _versions.Indexes;

    /// <summary>The app's one storage factory; usable on any thread.</summary>
    internal IStorageFactory Storages => _versions.Storages;

    /// <summary>The plan's run log; readable on any thread.</summary>
    internal RunLog RunLogFor(string planId) => new(_paths.LogFileFor(planId));

    /// <summary>When the scheduler starts the plan next; null when it does not (no triggers, disabled, paused).</summary>
    internal DateTime? NextRunUtc(string planId) => _scheduler.IsPaused ? null : _scheduler.NextRunUtc(planId);

    /// <summary>A location as the user reads it (a folder's path; an S3 or Drive location by its connection).</summary>
    internal string Describe(StorageLocation location) => DescribeLocation(location);

    /// <summary>A copy of the global ignore defaults as they are now.</summary>
    internal IReadOnlyList<string> GlobalIgnoreDefaultsNow() => GlobalIgnoreDefaults().ToList();

    /// <summary>Where integrated <c>.backupignore</c> files go: the same bin the editors' relocation dialog uses.</summary>
    internal IRecycleBin RecycleBin => _recycleBin;

    /// <summary>The queue's position of the plan's waiting job; null when it is not waiting.</summary>
    internal QueuePosition? QueuePositionOf(string planId) => _queue.PositionOf(planId);

    /// <summary>
    /// Claude changed something through the control pipe: the notification's title and message, in the applied language.
    /// The app shows them as a tray notification. Raised on the UI thread.
    /// </summary>
    public event Action<string, string>? ControlNotified;

    /// <summary>
    /// Tells the user about a change made through the control pipe: in the footer and as a notification. The change is
    /// done by then: a subscriber that fails does not undo it or fail the request.
    /// </summary>
    internal void NotifyControl(LocText message)
    {
        SetStatus(message);
        if (ControlNotified is not { } notified)
            return;
        var title = Loc.T("control.notification.title");
        var text = message.ToString();
        foreach (var handler in notified.GetInvocationList().Cast<Action<string, string>>())
        {
            try
            {
                handler(title, text);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"A control notification could not be shown: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Saves the plan with <paramref name="patterns"/> as its ignore patterns, everything else as saved, and shows the saved
    /// state in its editor, which is re-evaluated as after any pattern edit. Only for an editor without unsaved edits: they
    /// would be replaced. A file that cannot be written throws (<see cref="System.IO.IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>) and changes nothing.
    /// </summary>
    internal void SaveIgnorePatterns(PlanEditorViewModel editor, IReadOnlyList<string> patterns)
    {
        if (editor.IsDirty || editor.IsNew)
            throw new InvalidOperationException("The plan has unsaved edits.");
        var plan = editor.SavedPlan();
        plan.Ignore.Patterns = [.. patterns];
        _store.Save(plan);
        editor.ReplaceSaved(plan);
        RevalidateAll();
        SetStatus(LocText.Of("shell.status.saved", ("plan", plan.Name)));
    }

    /// <summary>
    /// The <c>.backupignore</c> in <paramref name="directory"/> of the plan's source was written (<paramref name="lines"/>)
    /// or taken away (null): the Ignore tab's scan and the scan cache take it over without scanning again.
    /// </summary>
    internal void ReplaceNestedIgnoreFile(PlanEditorViewModel editor, BackupPlan plan, string directory, IReadOnlyList<string>? lines)
    {
        if (editor.Preview.CompletedIndex is not null)
            editor.Preview.ReplaceNestedIgnoreFile(directory, lines);   // updates the cache too
        else if (ScanCache.TryGetCompleted(plan.Id, plan.Source) is { } cached)
            ScanCache.Put(plan.Id, IgnorePreviewViewModel.WithNestedIgnoreFile(cached, directory, lines), plan.Source);
    }
}
