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
    public PlanEditorViewModel(BackupPlan plan, bool isNew, Func<IEnumerable<BackupPlan>> allPlans,
        Func<IReadOnlyList<string>> globalIgnoreDefaults, IFolderOpener folders, VersionsContext versions, S3Connections s3)
    {
        _saved = plan.Clone();
        _allPlans = allPlans;
        _folders = folders;
        _s3 = s3;
        SourceS3 = new S3LocationViewModel(s3, versions.Dialogs, isTarget: false, () => SourceEdited(s3: true));
        TargetS3 = new S3LocationViewModel(s3, versions.Dialogs, isTarget: true, () => TargetEdited(s3: true));
        Run = new PlanRunViewModel(versions.Storages);
        Preview = new IgnorePreviewViewModel(SourceLocation, CurrentIgnoreSettings, globalIgnoreDefaults, versions.Storages);
        RetentionPreview = new RetentionPreviewViewModel(ToPlan, () => Preview.LastEvaluatedIncludedSize, folders, versions.Storages);
        Versions = new VersionsViewModel(() => IsNew ? null : SavedPlan(), () => Run.IsActive, folders, versions);
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

    // The Folder segments of the switches; a segment that is unchecked leaves the kind as it is.
    public bool SourceIsFolder
    {
        get => !SourceIsS3;
        set { if (value) SourceIsS3 = false; }
    }

    public bool TargetIsFolder
    {
        get => !TargetIsS3;
        set { if (value) TargetIsS3 = false; }
    }

    /// <summary>
    /// The Folder | S3 segments of the source ("Folder" or "S3"). The segments show the kind one-way and call this:
    /// two-way radio bindings in the one Plan view, whose DataContext switches between plans, wrote one plan's kind
    /// into another.
    /// </summary>
    [RelayCommand]
    private void ChooseSourceKind(string? kind) => SourceIsS3 = kind == "S3";

    /// <summary>Likewise for the target.</summary>
    [RelayCommand]
    private void ChooseTargetKind(string? kind) => TargetIsS3 = kind == "S3";

    /// <summary>"When the target is full, …" means nothing on S3 (no free-space information): shown for folders only.</summary>
    public bool ShowFreeSpaceOption => !TargetIsS3;

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

    partial void OnSourceChanged(string value) => SourceEdited(s3: false);

    partial void OnTargetChanged(string value) => TargetEdited(s3: false);

    partial void OnSourceIsS3Changed(bool value)
    {
        Preview.Invalidate();
        Touch();
    }

    partial void OnTargetIsS3Changed(bool value)
    {
        RetentionPreview.Invalidate();
        Touch();
    }

    /// <summary>The folder (<paramref name="s3"/> false) or the S3 half of the source was edited; only the half in use changes the preview.</summary>
    private void SourceEdited(bool s3)
    {
        if (s3 == SourceIsS3)
            Preview.Invalidate();
        Touch();
    }

    /// <summary>Likewise for the target and the retention preview.</summary>
    private void TargetEdited(bool s3)
    {
        if (s3 == TargetIsS3)
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
    private StorageLocation SourceLocation() => EditedLocation(SourceIsS3, SourceS3, Source, _saved.Source);

    /// <summary>
    /// A side as edited: the S3 location, or the folder. A location of another kind (only possible by editing the plan
    /// file) stays as loaded unless its text was edited.
    /// </summary>
    private static StorageLocation EditedLocation(bool isS3, S3LocationViewModel s3, string folder, StorageLocation saved)
    {
        if (isS3)
            return s3.Location;
        return saved.IsFileSystem || IsS3(saved) || folder != saved.Path ? StorageLocation.FileSystem(folder) : saved;
    }

    private static bool IsS3(StorageLocation location) => location.Kind == PlanValidator.S3Kind;

    public BackupPlan ToPlan()
    {
        var plan = _saved.Clone();
        plan.Name = Name;
        plan.Source = SourceLocation();
        plan.Target = EditedLocation(TargetIsS3, TargetS3, Target, _saved.Target);
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
        Errors = PlanValidator.Validate(ToPlan(), _allPlans(), _s3.BucketOf).Select(message => Loc.F(message)).ToList();

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
            if (SourceIsS3) Source = ""; else SourceS3.Load(null, "");
            if (TargetIsS3) Target = ""; else TargetS3.Load(null, "");
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
        _loading = true;
        try
        {
            Name = plan.Name;
            SourceIsS3 = IsS3(plan.Source);
            Source = SourceIsS3 ? "" : plan.Source.Path;
            SourceS3.Load(SourceIsS3 ? plan.Source.ConnectionId : null, SourceIsS3 ? plan.Source.Path : "");
            TargetIsS3 = IsS3(plan.Target);
            Target = TargetIsS3 ? "" : plan.Target.Path;
            TargetS3.Load(TargetIsS3 ? plan.Target.ConnectionId : null, TargetIsS3 ? plan.Target.Path : "");
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
        OnPropertyChanged(nameof(HasNoRetentionRules));
        RefreshNextRuns();
        RetentionPreview.RequestEvaluate();
    }
}
