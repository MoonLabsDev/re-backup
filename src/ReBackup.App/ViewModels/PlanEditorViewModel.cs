using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;

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

    public PlanEditorViewModel(BackupPlan plan, bool isNew, Func<IEnumerable<BackupPlan>> allPlans,
        Func<IReadOnlyList<string>> globalIgnoreDefaults)
    {
        _saved = plan.Clone();
        _allPlans = allPlans;
        Preview = new IgnorePreviewViewModel(() => Source, CurrentIgnoreSettings, globalIgnoreDefaults);
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

    partial void OnTargetChanged(string value) => Touch();
    partial void OnEnabledChanged(bool value) => Touch();
    partial void OnFreeSpaceByRetentionChanged(bool value) => Touch();

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

    /// <summary>The selected preview entry, unless it is the source root (which cannot be ignored).</summary>
    private IndexNode? SelectedEntry() =>
        Preview.SelectedNode?.Node is { RelativePath.Length: > 0 } node ? node : null;

    private void AppendPattern(string pattern)
    {
        var text = IgnorePatternsText.TrimEnd('\r', '\n');
        IgnorePatternsText = text.Length == 0 ? pattern : text + Environment.NewLine + pattern;
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
        }
        finally
        {
            _loading = false;
        }
    }
}
