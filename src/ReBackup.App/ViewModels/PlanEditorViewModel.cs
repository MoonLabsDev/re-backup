using CommunityToolkit.Mvvm.ComponentModel;
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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayName))] private bool _isDirty;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private IReadOnlyList<string> _errors = [];

    public PlanEditorViewModel(BackupPlan plan, bool isNew, Func<IEnumerable<BackupPlan>> allPlans)
    {
        _saved = plan.Clone();
        _allPlans = allPlans;
        IsNew = isNew;
        LoadFrom(_saved);
        IsDirty = isNew;
        Validate();
    }

    public string Id => _saved.Id;

    public string DisplayName =>
        (IsDirty ? "• " : "") + (string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name);

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        Touch();
    }

    partial void OnSourceChanged(string value) => Touch();
    partial void OnTargetChanged(string value) => Touch();
    partial void OnEnabledChanged(bool value) => Touch();
    partial void OnFreeSpaceByRetentionChanged(bool value) => Touch();

    public BackupPlan ToPlan()
    {
        var plan = _saved.Clone();
        plan.Name = Name;
        plan.Source = Source;
        plan.Target = Target;
        plan.Enabled = Enabled;
        plan.FreeSpaceByRetention = FreeSpaceByRetention;
        return plan;
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
        }
        finally
        {
            _loading = false;
        }
    }
}
