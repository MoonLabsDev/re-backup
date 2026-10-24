using ReBackup.Core.Backup;
using ReBackup.Core.Versions;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>What the control service reads besides the plan editors. Call on the UI thread unless noted.</summary>
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
}
