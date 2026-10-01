using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Core.Retention;
using ReBackup.Core.Schedule;

namespace ReBackup.App.ViewModels;

/// <summary>Editable copy of one plan. Edits stay in memory (per plan) until saved or reverted.</summary>
public sealed partial class PlanEditorViewModel : ObservableObject
{
    private readonly Func<IEnumerable<BackupPlan>> _allPlans;
    private BackupPlan _saved;
    private bool _loading;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _target = "";
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

    public PlanEditorViewModel(BackupPlan plan, bool isNew, Func<IEnumerable<BackupPlan>> allPlans,
        Func<IReadOnlyList<string>> globalIgnoreDefaults)
    {
        _saved = plan.Clone();
        _allPlans = allPlans;
        Preview = new IgnorePreviewViewModel(() => Source, CurrentIgnoreSettings, globalIgnoreDefaults);
        RetentionPreview = new RetentionPreviewViewModel(ToPlan, () => Preview.LastEvaluatedIncludedSize);
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

    /// <summary>Queue state, progress and history of this plan.</summary>
    public PlanRunViewModel Run { get; } = new();

    /// <summary>What the retention rules do with the versions in the target.</summary>
    public RetentionPreviewViewModel RetentionPreview { get; }

    /// <summary>The retention rules as edited.</summary>
    public ObservableCollection<RetentionRuleViewModel> RetentionRuleRows { get; } = [];

    public bool HasNoRetentionRules => RetentionRuleRows.Count == 0;

    /// <summary>The triggers as edited.</summary>
    public ObservableCollection<TriggerRowViewModel> TriggerRows { get; } = [];

    /// <summary>A copy of the plan as last saved (unsaved edits are not part of a run).</summary>
    public BackupPlan SavedPlan() => _saved.Clone();

    public string Id => _saved.Id;

    public string DisplayName =>
        (IsDirty ? "• " : "") + (string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name);

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        Touch();
    }

    partial void OnSourceChanged(string value)
    {
        Preview.Invalidate();
        Touch();
    }

    partial void OnTargetChanged(string value)
    {
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

    public BackupPlan ToPlan()
    {
        var plan = _saved.Clone();
        plan.Name = Name;
        plan.Source = Source;
        plan.Target = Target;
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
            note = "No triggers: this plan runs only when started by hand.";
        }
        else if (triggers.Any(t => ScheduleTriggers.Validate(t) is not null))
        {
            NextRuns = [];
            note = "Correct the triggers above to see the next runs.";
        }
        else
        {
            NextRuns = ScheduleCalculator.LocalRunTimes(triggers, DateTime.UtcNow, TimeZoneInfo.Local)
                .Take(5)
                .Select(time => time.ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture))
                .ToList();
            note = Enabled
                ? "Runs happen only while ReBackup is running (it keeps running in the tray when the window is closed)."
                : "The plan is disabled: it runs only when started by hand until it is enabled again.";
        }

        NextRunsNote = (SchedulerPaused ? "The scheduler is paused. " : "") + note +
                       (IsDirty ? " Unsaved changes take effect after Save." : "");
    }

    public void Validate() => Errors = PlanValidator.Validate(ToPlan(), _allPlans());

    public bool TrySave(PlanStore store)
    {
        Validate();
        if (Errors.Count > 0)
            return false;

        var plan = ToPlan();
        store.Save(plan);
        _saved = plan;
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
            Source = plan.Source;
            Target = plan.Target;
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
