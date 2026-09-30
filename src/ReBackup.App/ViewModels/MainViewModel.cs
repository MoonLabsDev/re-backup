using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Core.Config;
using ReBackup.Core.Plans;

namespace ReBackup.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly PlanStore _store;
    private readonly ConfigPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly Action _openSettings;

    [ObservableProperty] private PlanEditorViewModel? _selectedPlan;
    [ObservableProperty] private string? _statusMessage;

    public MainViewModel(PlanStore store, ConfigPaths paths, IDialogService dialogs, Action openSettings)
    {
        _store = store;
        _paths = paths;
        _dialogs = dialogs;
        _openSettings = openSettings;

        var result = _store.LoadAll();
        foreach (var plan in result.Plans)
            Plans.Add(new PlanEditorViewModel(plan, isNew: false, AllPlans));
        SelectedPlan = Plans.FirstOrDefault();
        StatusMessage = LoadErrorText(result) ?? $"Configuration: {_paths.Root}";
    }

    public ObservableCollection<PlanEditorViewModel> Plans { get; } = [];

    public bool HasUnsavedChanges => Plans.Any(p => p.IsDirty);

    public IEnumerable<string> UnsavedPlanNames =>
        Plans.Where(p => p.IsDirty).Select(p => string.IsNullOrWhiteSpace(p.Name) ? "(unnamed)" : p.Name);

    public void ReloadFromDisk()
    {
        var result = _store.LoadAll();
        var loaded = result.Plans.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var editor in Plans.ToList())
        {
            if (loaded.Remove(editor.Id, out var plan))
            {
                if (!editor.IsDirty)
                    editor.ReplaceSaved(plan);
            }
            else if (!editor.IsNew)
            {
                if (editor.IsDirty)
                    editor.MarkAsNew();
                else
                    Plans.Remove(editor);
            }
        }

        foreach (var plan in loaded.Values)
            Plans.Add(new PlanEditorViewModel(plan, isNew: false, AllPlans));

        if (SelectedPlan is null || !Plans.Contains(SelectedPlan))
            SelectedPlan = Plans.FirstOrDefault();

        StatusMessage = LoadErrorText(result) ?? "Plans reloaded after a change on disk.";
    }

    [RelayCommand]
    private void NewPlan()
    {
        var editor = new PlanEditorViewModel(new BackupPlan { Name = UniqueName("New plan") }, isNew: true, AllPlans);
        Plans.Add(editor);
        SelectedPlan = editor;
    }

    [RelayCommand]
    private void DeletePlan()
    {
        var editor = SelectedPlan;
        if (editor is null)
            return;

        if (!editor.IsNew)
        {
            if (!_dialogs.Confirm("Delete plan",
                    $"Delete plan \"{editor.Name}\"?\n\nBackups already stored in the target are not touched."))
                return;
            var deleteLog = _dialogs.AskYesNoCancel("Delete plan", "Also delete this plan's run history?");
            if (deleteLog is null)
                return;

            try
            {
                _store.Delete(editor.Id);
                var logFile = _paths.LogFileFor(editor.Id);
                if (deleteLog == true && File.Exists(logFile))
                    File.Delete(logFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError("Delete plan", ex.Message);
                return;
            }
        }

        var index = Plans.IndexOf(editor);
        Plans.Remove(editor);
        SelectedPlan = Plans.Count == 0 ? null : Plans[Math.Min(index, Plans.Count - 1)];
    }

    [RelayCommand]
    private void Save()
    {
        var editor = SelectedPlan;
        if (editor is null)
            return;

        try
        {
            StatusMessage = editor.TrySave(_store)
                ? $"Saved \"{editor.Name}\"."
                : "Not saved: fix the errors shown in the plan.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Save failed", ex.Message);
        }
    }

    [RelayCommand]
    private void Revert()
    {
        var editor = SelectedPlan;
        if (editor is null)
            return;

        if (editor.IsNew)
        {
            Plans.Remove(editor);
            SelectedPlan = Plans.FirstOrDefault();
            return;
        }
        editor.Revert();
    }

    [RelayCommand]
    private void BrowseSource()
    {
        if (SelectedPlan is { } editor && _dialogs.PickFolder("Choose source folder", editor.Source) is { } folder)
            editor.Source = folder;
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        if (SelectedPlan is { } editor && _dialogs.PickFolder("Choose target folder", editor.Target) is { } folder)
            editor.Target = folder;
    }

    [RelayCommand]
    private void OpenSettings() => _openSettings();

    private IEnumerable<BackupPlan> AllPlans() => Plans.Select(p => p.ToPlan());

    private string UniqueName(string baseName)
    {
        var names = Plans.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
            return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!names.Contains(candidate))
                return candidate;
        }
    }

    private static string? LoadErrorText(PlanLoadResult result) =>
        result.Errors.Count == 0
            ? null
            : $"{result.Errors.Count} plan file(s) could not be read: " +
              string.Join("; ", result.Errors.Select(e => $"{Path.GetFileName(e.FilePath)}: {e.Message}"));
}
