using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.App.Services;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.App.ViewModels;

/// <summary>Editable copy of one plan. Edits stay in memory (per plan) until saved or reverted.</summary>
public sealed partial class PlanEditorViewModel : ObservableObject
{
    private readonly Func<IEnumerable<BackupPlan>> _allPlans;
    private readonly IFolderOpener _folders;
    private readonly S3Connections _s3;
    private readonly DriveConnections _drive;
    private BackupPlan _saved;
    private bool _loading;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _target = "";

    /// <summary>The source is an S3 location (<see cref="SourceS3"/>), not the folder <see cref="Source"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceIsFolder))]
    private bool _sourceIsS3;

    /// <summary>The target is an S3 location (<see cref="TargetS3"/>), not the folder <see cref="Target"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetIsFolder), nameof(ShowFreeSpaceOption))]
    private bool _targetIsS3;

    /// <summary>The source is a Google Drive location (<see cref="SourceDrive"/>); never together with <see cref="SourceIsS3"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceIsFolder))]
    private bool _sourceIsDrive;

    /// <summary>The target is a Google Drive location (<see cref="TargetDrive"/>); never together with <see cref="TargetIsS3"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetIsFolder), nameof(ShowFreeSpaceOption))]
    private bool _targetIsDrive;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _freeSpaceByRetention;
    [ObservableProperty] private string _ignorePatternsText = "";
    [ObservableProperty] private bool _useGlobalIgnoreDefaults;
    [ObservableProperty] private bool _honorNestedIgnoreFiles;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayName))] private bool _isDirty;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private IReadOnlyList<string> _errors = [];
    [ObservableProperty] private IReadOnlyList<string> _nextRuns = [];
    [ObservableProperty] private string _nextRunsNote = "";

    /// <summary>Whether the scheduler is paused; set by the main view model, shown in the note under the next runs.</summary>
    [ObservableProperty] private bool _schedulerPaused;

    /// <param name="s3">The saved S3 connections: the pickers, validation and "Test".</param>
    /// <param name="drive">The saved Google Drive connections: the pickers, validation and "Test".</param>
    public PlanEditorViewModel(BackupPlan plan, bool isNew, Func<IEnumerable<BackupPlan>> allPlans,
        Func<IReadOnlyList<string>> globalIgnoreDefaults, IFolderOpener folders, VersionsContext versions, S3Connections s3,
        DriveConnections drive)
    {
        _saved = plan.Clone();
        _allPlans = allPlans;
        _folders = folders;
        _s3 = s3;
        _drive = drive;
        SourceS3 = new S3LocationViewModel(s3, versions.Dialogs, isTarget: false, () => SourceEdited(LocationKind.S3));
        TargetS3 = new S3LocationViewModel(s3, versions.Dialogs, isTarget: true, () => TargetEdited(LocationKind.S3));
        SourceDrive = new DriveLocationViewModel(drive, versions.Dialogs, isTarget: false, () => SourceEdited(LocationKind.Drive));
        TargetDrive = new DriveLocationViewModel(drive, versions.Dialogs, isTarget: true, () => TargetEdited(LocationKind.Drive));
        Run = new PlanRunViewModel(versions.Storages);
        Preview = new IgnorePreviewViewModel(SourceLocation, CurrentIgnoreSettings, globalIgnoreDefaults, versions.Storages);
        RetentionPreview = new RetentionPreviewViewModel(ToPlan, () => Preview.LastEvaluatedIncludedSize, folders, versions.Storages,
            () => Run.Entries);
        Versions = new VersionsViewModel(() => IsNew ? null : SavedPlan(), () => Run.IsActive, folders, versions, () => Run.Entries);
        Versions.VersionsDeleted += () => RetentionPreview.ReloadIfLoaded();
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IgnorePreviewViewModel.SelectedNode))
            {
                IgnoreSelectedCommand.NotifyCanExecuteChanged();
                IgnoreSelectedExtensionCommand.NotifyCanExecuteChanged();
                UnignoreSelectedCommand.NotifyCanExecuteChanged();
            }
        };
        IsNew = isNew;
        LoadFrom(_saved);
        IsDirty = isNew;
        Validate();
    }

    /// <summary>Index and preview of this plan's source; cached for the session.</summary>
    public IgnorePreviewViewModel Preview { get; }

    /// <summary>The S3 connection and prefix of the source; used while <see cref="SourceIsS3"/>.</summary>
    public S3LocationViewModel SourceS3 { get; }

    /// <summary>The S3 connection and prefix of the target; used while <see cref="TargetIsS3"/>.</summary>
    public S3LocationViewModel TargetS3 { get; }

    /// <summary>The Drive connection and path of the source; used while <see cref="SourceIsDrive"/>.</summary>
    public DriveLocationViewModel SourceDrive { get; }

    /// <summary>The Drive connection and path of the target; used while <see cref="TargetIsDrive"/>.</summary>
    public DriveLocationViewModel TargetDrive { get; }

    // The Folder segments of the switches; a segment that is unchecked leaves the kind as it is.
    public bool SourceIsFolder
    {
        get => !SourceIsS3 && !SourceIsDrive;
        set { if (value) SourceKind = LocationKind.Folder; }
    }

    public bool TargetIsFolder
    {
        get => !TargetIsS3 && !TargetIsDrive;
        set { if (value) TargetKind = LocationKind.Folder; }
    }

    /// <summary>The kind of a side's location as the switch shows it.</summary>
    private enum LocationKind { Folder, S3, Drive }

    private LocationKind SourceKind
    {
        get => SourceIsS3 ? LocationKind.S3 : SourceIsDrive ? LocationKind.Drive : LocationKind.Folder;
        set
        {
            // The kind that is left first, so the side never is two kinds at once.
            if (value != LocationKind.S3) SourceIsS3 = false;
            if (value != LocationKind.Drive) SourceIsDrive = false;
            if (value == LocationKind.S3) SourceIsS3 = true;
            if (value == LocationKind.Drive) SourceIsDrive = true;
        }
    }

    private LocationKind TargetKind
    {
        get => TargetIsS3 ? LocationKind.S3 : TargetIsDrive ? LocationKind.Drive : LocationKind.Folder;
        set
        {
            if (value != LocationKind.S3) TargetIsS3 = false;
            if (value != LocationKind.Drive) TargetIsDrive = false;
            if (value == LocationKind.S3) TargetIsS3 = true;
            if (value == LocationKind.Drive) TargetIsDrive = true;
        }
    }

    private static LocationKind ParseKind(string? kind) => kind switch
    {
        "S3" => LocationKind.S3,
        "Drive" => LocationKind.Drive,
        _ => LocationKind.Folder,
    };

    /// <summary>
    /// The Folder | S3 | Drive segments of the source ("Folder", "S3" or "Drive"). The segments show the kind one-way and
    /// call this: two-way radio bindings in the one Plan view, whose DataContext switches between plans, wrote one plan's
    /// kind into another.
    /// </summary>
    [RelayCommand]
    private void ChooseSourceKind(string? kind) => SourceKind = ParseKind(kind);

    /// <summary>Likewise for the target.</summary>
    [RelayCommand]
    private void ChooseTargetKind(string? kind) => TargetKind = ParseKind(kind);

    /// <summary>
    /// "When the target is full, …" means nothing on S3 or Drive (no free-space information): shown for folders only.
    /// </summary>
    public bool ShowFreeSpaceOption => TargetIsFolder;

    /// <summary>Queue state, progress and history of this plan.</summary>
    public PlanRunViewModel Run { get; }

    /// <summary>What the retention rules do with the versions in the target.</summary>
    public RetentionPreviewViewModel RetentionPreview { get; }

    /// <summary>The versions in the saved target, their index, comparison and restore.</summary>
    public VersionsViewModel Versions { get; }

    /// <summary>The retention rules as edited.</summary>
    public ObservableCollection<RetentionRuleViewModel> RetentionRuleRows { get; } = [];

    public bool HasNoRetentionRules => RetentionRuleRows.Count == 0;

    /// <summary>The triggers as edited.</summary>
    public ObservableCollection<TriggerRowViewModel> TriggerRows { get; } = [];

    /// <summary>A copy of the plan as last saved (unsaved edits are not part of a run).</summary>
    public BackupPlan SavedPlan() => _saved.Clone();

    public string Id => _saved.Id;

    /// <summary>
    /// Shows the version a run produced in Explorer; marks the row when that folder no longer exists. The folder is
    /// checked and opened on a worker thread, so a slow share never blocks the UI.
    /// </summary>
    [RelayCommand]
    private async Task OpenRunVersionAsync(RunHistoryRow? row)
    {
        if (row is not { CanOpenVersion: true })
            return;
        var (target, name) = (row.Target, row.VersionName);
        if (!await Task.Run(() => _folders.OpenVersionFolderAsync(target, name)))
            row.MarkVersionMissing();
    }

    public string DisplayName =>
        (IsDirty ? "• " : "") + (string.IsNullOrWhiteSpace(Name) ? Loc.T("common.unnamed") : Name);

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        Touch();
    }

    partial void OnSourceChanged(string value) => SourceEdited(LocationKind.Folder);

    partial void OnTargetChanged(string value) => TargetEdited(LocationKind.Folder);

    partial void OnSourceIsS3Changed(bool value) => SourceKindChanged();

    partial void OnSourceIsDriveChanged(bool value) => SourceKindChanged();

    partial void OnTargetIsS3Changed(bool value) => TargetKindChanged();

    partial void OnTargetIsDriveChanged(bool value) => TargetKindChanged();

    private void SourceKindChanged()
    {
        Preview.Invalidate();
        Touch();
    }

    private void TargetKindChanged()
    {
        RetentionPreview.Invalidate();
        Touch();
    }

    /// <summary>The folder, S3 or Drive half of the source was edited; only the half in use changes the preview.</summary>
    private void SourceEdited(LocationKind half)
    {
        if (half == SourceKind)
            Preview.Invalidate();
        Touch();
    }

    /// <summary>Likewise for the target and the retention preview.</summary>
    private void TargetEdited(LocationKind half)
    {
        if (half == TargetKind)
            RetentionPreview.Invalidate();
        Touch();
    }

    partial void OnEnabledChanged(bool value)
    {
        RefreshNextRuns();
        Touch();
    }
    partial void OnFreeSpaceByRetentionChanged(bool value) => Touch();

    partial void OnIsDirtyChanged(bool value) => RefreshNextRuns();

    partial void OnSchedulerPausedChanged(bool value) => RefreshNextRuns();

    partial void OnIgnorePatternsTextChanged(string value)
    {
        Preview.RequestReevaluate();
        Touch();
    }

    partial void OnUseGlobalIgnoreDefaultsChanged(bool value)
    {
        Preview.RequestReevaluate();
        Touch();
    }

    partial void OnHonorNestedIgnoreFilesChanged(bool value)
    {
        Preview.RequestReevaluate();
        Touch();
    }

    /// <summary>The source as edited.</summary>
    private StorageLocation SourceLocation() => EditedLocation(SourceKind, SourceS3, SourceDrive, Source, _saved.Source);

    /// <summary>
    /// A side as edited: the S3 or Drive location, or the folder. A location of another kind (only possible by editing the
    /// plan file) stays as loaded unless its text was edited.
    /// </summary>
    private static StorageLocation EditedLocation(LocationKind kind, S3LocationViewModel s3, DriveLocationViewModel drive,
        string folder, StorageLocation saved)
    {
        if (kind == LocationKind.S3)
            return s3.Location;
        if (kind == LocationKind.Drive)
            return drive.Location;
        return saved.IsFileSystem || IsS3(saved) || IsDrive(saved) || folder != saved.Path ? StorageLocation.FileSystem(folder) : saved;
    }

    /// <summary>The location a side shows: its S3 or Drive half or its folder, whichever is in use.</summary>
    private static StorageLocation ShownLocation(LocationKind kind, S3LocationViewModel s3, DriveLocationViewModel drive, string folder) =>
        kind switch
        {
            LocationKind.S3 => s3.Location,
            LocationKind.Drive => drive.Location,
            _ => StorageLocation.FileSystem(folder),
        };

    private static bool IsS3(StorageLocation location) => location.Kind == PlanValidator.S3Kind;

    private static bool IsDrive(StorageLocation location) => location.Kind == PlanValidator.DriveKind;

    private static LocationKind KindOf(StorageLocation location) =>
        IsS3(location) ? LocationKind.S3 : IsDrive(location) ? LocationKind.Drive : LocationKind.Folder;

    public BackupPlan ToPlan()
    {
        var plan = _saved.Clone();
        plan.Name = Name;
        plan.Source = SourceLocation();
        plan.Target = EditedLocation(TargetKind, TargetS3, TargetDrive, Target, _saved.Target);
        plan.Enabled = Enabled;
        plan.FreeSpaceByRetention = FreeSpaceByRetention;
        plan.Ignore = CurrentIgnoreSettings();
        plan.Retention = RetentionRuleRows.Select(row => row.ToRule()).ToList();
        plan.Triggers = TriggerRows.Select(row => row.ToTrigger()).ToList();
        return plan;
    }

    /// <summary>A snapshot of the ignore section as currently edited. Trailing blank lines are dropped.</summary>
    public IgnoreSettings CurrentIgnoreSettings()
    {
        var lines = IgnorePatternsText.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return new IgnoreSettings
        {
            UseGlobalDefaults = UseGlobalIgnoreDefaults,
            HonorNestedFiles = HonorNestedIgnoreFiles,
            Patterns = lines,
        };
    }

    [RelayCommand(CanExecute = nameof(CanIgnoreSelected))]
    private void IgnoreSelected()
    {
        if (SelectedEntry() is { } node)
            AppendPattern("/" + IgnorePattern.EscapeLiteral(node.RelativePath) + (node.IsDirectory ? "/" : ""));
    }

    [RelayCommand(CanExecute = nameof(CanIgnoreSelectedExtension))]
    private void IgnoreSelectedExtension()
    {
        if (SelectedExtension() is { } extension)
            AppendPattern("*" + IgnorePattern.EscapeLiteral(extension));
    }

    [RelayCommand(CanExecute = nameof(CanIgnoreSelected))]
    private void UnignoreSelected()
    {
        if (SelectedEntry() is { } node)
            AppendPattern("!/" + IgnorePattern.EscapeLiteral(node.RelativePath) + (node.IsDirectory ? "/" : ""));
    }

    private bool CanIgnoreSelected() => SelectedEntry() is not null;

    private bool CanIgnoreSelectedExtension() => SelectedExtension() is not null;

    private string? SelectedExtension() =>
        SelectedEntry() is { IsDirectory: false } node && Path.GetExtension(node.Name) is { Length: > 1 } extension
            ? extension
            : null;

    /// <summary>The selected preview entry, unless it is the source root (which cannot be ignored) or a "loading" row.</summary>
    private IPreviewEntry? SelectedEntry() =>
        Preview.SelectedNode is { RelativePath.Length: > 0 } entry && entry is not LoadingPlaceholder ? entry : null;

    private void AppendPattern(string pattern)
    {
        var text = IgnorePatternsText.TrimEnd('\r', '\n');
        IgnorePatternsText = text.Length == 0 ? pattern : text + Environment.NewLine + pattern;
    }

    [RelayCommand]
    private void AddRetentionRule()
    {
        AddRetentionRow(new RetentionRule { Period = RetentionPeriod.Daily, Keep = 7 });
        OnRetentionRulesEdited();
    }

    [RelayCommand]
    private void RemoveRetentionRule(RetentionRuleViewModel? row)
    {
        if (row is null || !RetentionRuleRows.Remove(row))
            return;
        row.Changed -= OnRetentionRulesEdited;
        OnRetentionRulesEdited();
    }

    private void AddRetentionRow(RetentionRule rule)
    {
        var row = new RetentionRuleViewModel(rule);
        row.Changed += OnRetentionRulesEdited;
        RetentionRuleRows.Add(row);
    }

    private void OnRetentionRulesEdited()
    {
        OnPropertyChanged(nameof(HasNoRetentionRules));
        RetentionPreview.RequestEvaluate();
        Touch();
    }

    [RelayCommand]
    private void AddTrigger()
    {
        AddTriggerRow(new ScheduleTrigger { Type = TriggerType.Daily, Time = "02:00" });
        OnTriggersEdited();
    }

    [RelayCommand]
    private void RemoveTrigger(TriggerRowViewModel? row)
    {
        if (row is null || !TriggerRows.Remove(row))
            return;
        row.Changed -= OnTriggersEdited;
        OnTriggersEdited();
    }

    private void AddTriggerRow(ScheduleTrigger trigger)
    {
        var row = new TriggerRowViewModel(trigger);
        row.Changed += OnTriggersEdited;
        TriggerRows.Add(row);
    }

    private void OnTriggersEdited()
    {
        RefreshNextRuns();
        RetentionPreview.RequestEvaluate();
        Touch();
    }

    /// <summary>
    /// Recomputes the next five runs of the edited triggers from the current time; the note says whether they apply
    /// (paused scheduler, disabled plan, unsaved edits).
    /// </summary>
    public void RefreshNextRuns()
    {
        var triggers = TriggerRows.Select(row => row.ToTrigger()).ToList();
        string note;
        if (triggers.Count == 0)
        {
            NextRuns = [];
            note = Loc.T("plan.nextRuns.none");
        }
        else if (triggers.Any(t => ScheduleTriggers.Validate(t) is not null))
        {
            NextRuns = [];
            note = Loc.T("plan.nextRuns.fix");
        }
        else
        {
            NextRuns = ScheduleCalculator.LocalRunTimes(triggers, DateTime.UtcNow, TimeZoneInfo.Local)
                .Take(5)
                .Select(Formats.NextRun)
                .ToList();
            note = Enabled ? Loc.T("plan.nextRuns.tray") : Loc.T("plan.nextRuns.disabled");
        }

        var parts = new List<string>();
        if (SchedulerPaused)
            parts.Add(Loc.T("plan.nextRuns.paused"));
        parts.Add(note);
        if (IsDirty)
            parts.Add(Loc.T("plan.nextRuns.unsaved"));
        NextRunsNote = string.Join(" ", parts);
    }

    public void Validate() =>
        Errors = PlanValidator.Validate(ToPlan(), _allPlans(), _s3.BucketOf, _drive.RootOf).Select(message => Loc.F(message)).ToList();

    /// <summary>The language changed: the texts this editor builds in code are built again.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(DisplayName));
        Validate();
        RefreshNextRuns();
        foreach (var row in TriggerRows)
            row.RefreshTexts();
        foreach (var row in RetentionRuleRows)
            row.RefreshTexts();
        Run.RefreshTexts();
        Preview.RefreshTexts();
        RetentionPreview.RefreshTexts();
        Versions.RefreshTexts();
    }

    public bool TrySave(PlanStore store)
    {
        Validate();
        if (Errors.Count > 0)
            return false;

        var plan = ToPlan();
        store.Save(plan);
        _saved = plan;
        // From now on only the saved kind is kept; the other side of each switch starts empty.
        _loading = true;
        try
        {
            if (!SourceIsFolder) Source = "";
            if (!SourceIsS3) SourceS3.Load(null, "");
            if (!SourceIsDrive) SourceDrive.Load(null, "");
            if (!TargetIsFolder) Target = "";
            if (!TargetIsS3) TargetS3.Load(null, "");
            if (!TargetIsDrive) TargetDrive.Load(null, "");
        }
        finally
        {
            _loading = false;
        }
        IsDirty = false;
        IsNew = false;
        return true;
    }

    public void Revert()
    {
        LoadFrom(_saved);
        IsDirty = IsNew;
        Validate();
    }

    /// <summary>Takes a newer version from disk (only called when there are no unsaved edits).</summary>
    public void ReplaceSaved(BackupPlan plan)
    {
        _saved = plan.Clone();
        LoadFrom(_saved);
        IsDirty = false;
        Validate();
    }

    /// <summary>The file was deleted on disk while this editor had unsaved edits; saving recreates it.</summary>
    public void MarkAsNew() => IsNew = true;

    /// <summary>The file exists on disk again after having been unreadable or missing.</summary>
    public void MarkAsExisting() => IsNew = false;

    private void Touch()
    {
        if (_loading)
            return;
        IsDirty = true;
        Validate();
    }

    private void LoadFrom(BackupPlan plan)
    {
        var sourceBefore = ShownLocation(SourceKind, SourceS3, SourceDrive, Source);
        var targetBefore = ShownLocation(TargetKind, TargetS3, TargetDrive, Target);
        _loading = true;
        try
        {
            Name = plan.Name;
            SourceKind = KindOf(plan.Source);
            Source = SourceIsFolder ? plan.Source.Path : "";
            SourceS3.Load(SourceIsS3 ? plan.Source.ConnectionId : null, SourceIsS3 ? plan.Source.Path : "");
            SourceDrive.Load(SourceIsDrive ? plan.Source.ConnectionId : null, SourceIsDrive ? plan.Source.Path : "");
            TargetKind = KindOf(plan.Target);
            Target = TargetIsFolder ? plan.Target.Path : "";
            TargetS3.Load(TargetIsS3 ? plan.Target.ConnectionId : null, TargetIsS3 ? plan.Target.Path : "");
            TargetDrive.Load(TargetIsDrive ? plan.Target.ConnectionId : null, TargetIsDrive ? plan.Target.Path : "");
            Enabled = plan.Enabled;
            FreeSpaceByRetention = plan.FreeSpaceByRetention;
            IgnorePatternsText = string.Join(Environment.NewLine, plan.Ignore.Patterns);
            UseGlobalIgnoreDefaults = plan.Ignore.UseGlobalDefaults;
            HonorNestedIgnoreFiles = plan.Ignore.HonorNestedFiles;

            foreach (var row in RetentionRuleRows)
                row.Changed -= OnRetentionRulesEdited;
            RetentionRuleRows.Clear();
            foreach (var rule in plan.Retention)
                AddRetentionRow(rule);

            foreach (var row in TriggerRows)
                row.Changed -= OnTriggersEdited;
            TriggerRows.Clear();
            foreach (var trigger in plan.Triggers)
                AddTriggerRow(trigger);
        }
        finally
        {
            _loading = false;
        }
        // Loading the S3 or Drive half is no edit, so a connection or path it changes would not invalidate the previews.
        if (ShownLocation(SourceKind, SourceS3, SourceDrive, Source) != sourceBefore)
            Preview.Invalidate();
        if (ShownLocation(TargetKind, TargetS3, TargetDrive, Target) != targetBefore)
            RetentionPreview.Invalidate();
        OnPropertyChanged(nameof(HasNoRetentionRules));
        RefreshNextRuns();
        RetentionPreview.RequestEvaluate();
    }
}
