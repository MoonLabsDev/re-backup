using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Cleanup;
using ReBackup.Core.Ignore;
using ReBackup.Core.Plans;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The Cleanup tab of one plan (spec: Cleanup tab): the plan's versions in a grid to check, a scan of what the saved
/// ignore rules would remove from the checked ones (<see cref="CleanupScanner"/>, from the local index), and the
/// cleanup itself, run as a job of the Versions tab (<see cref="VersionsViewModel.CleanUpAsync"/>). The scan goes stale
/// when the saved rules, the checked versions or a version's manifest change; then it has to be repeated before the
/// cleanup can start.
/// </summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly Func<BackupPlan?> _savedPlan;
    private readonly Func<bool> _hasUnsavedChanges;
    private readonly VersionsViewModel _versions;
    private readonly VersionsContext _context;
    private readonly Func<IReadOnlyList<string>> _globalDefaults;
    private readonly RangeObservableCollection<CleanupRowViewModel> _rows = new();

    /// <summary>Set while <see cref="AllChecked"/> checks or unchecks many rows: they are looked at once afterwards.</summary>
    private bool _checkingAll;

    /// <summary>What the scan says about the source right now ("Scanning source…", or why it cannot be read); null otherwise.</summary>
    private LocText? _scanHint;

    /// <summary>The scan runs (it may wait for the source scan first).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CleanUpCommand))]
    private bool _isScanning;

    /// <summary>
    /// The scan no longer matches: the rules were saved, the checked versions or a version's manifest changed since.
    /// "Clean up…" is disabled until the next scan.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanUpCommand))]
    private bool _isStale;

    /// <summary>
    /// The line under the buttons: "Save first", "Scan again", the source scan's progress or why the scan failed; null
    /// when there is nothing to say.
    /// </summary>
    [ObservableProperty] private LocText? _hint;

    /// <summary>The totals below the grid: what the scan found, or what the last cleanup measured.</summary>
    [ObservableProperty] private LocText _totalsText = LocText.Empty;

    /// <param name="savedPlan">The plan as saved; null while it is new.</param>
    /// <param name="hasUnsavedChanges">True while the plan has unsaved edits: no scan then ("Save first").</param>
    /// <param name="versions">The plan's Versions tab: its rows, its index syncs and the cleanup job.</param>
    /// <param name="globalDefaults">The global ignore defaults, as the next backup would use them.</param>
    public CleanupViewModel(Func<BackupPlan?> savedPlan, Func<bool> hasUnsavedChanges, VersionsViewModel versions,
        VersionsContext context, Func<IReadOnlyList<string>> globalDefaults)
    {
        _savedPlan = savedPlan;
        _hasUnsavedChanges = hasUnsavedChanges;
        _versions = versions;
        _context = context;
        _globalDefaults = globalDefaults;
        versions.VersionRows.CollectionChanged += (_, _) => Reload();
        versions.IndexSyncEnded += OnIndexSyncEnded;
        versions.PropertyChanged += OnVersionsChanged;
        Reload();
        RefreshHint();
    }

    /// <summary>The plan's versions, newest first, as the Versions tab lists them.</summary>
    public ObservableCollection<CleanupRowViewModel> Rows => _rows;

    /// <summary>What the scan found by rule (rule, origin, files, bytes, largest hit), most bytes first.</summary>
    public ObservableCollection<CleanupRuleHit> ByRule { get; } = [];

    /// <summary>
    /// The header checkbox: true when every selectable row is checked, false when none is (or there is none), null when
    /// some are. Setting true checks every selectable row; false or null (a three-state box clicked on) unchecks them.
    /// </summary>
    public bool? AllChecked
    {
        get
        {
            var selectable = Rows.Where(r => r.IsSelectable).ToList();
            var count = selectable.Count(r => r.IsChecked);
            return count == 0 ? false : count == selectable.Count ? true : null;
        }
        set
        {
            _checkingAll = true;
            try
            {
                foreach (var row in Rows.Where(r => r.IsSelectable))
                    row.IsChecked = value == true;
            }
            finally
            {
                _checkingAll = false;
            }
            OnSelectionChanged();
        }
    }

    /// <summary>
    /// Builds the rows again from the Versions tab's rows, newest first. Rows are kept by name, so what is checked, the
    /// scan's numbers and the last outcome stay; a row that can no longer be cleaned up is unchecked.
    /// </summary>
    public void Reload()
    {
        var known = Rows.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var rows = new List<CleanupRowViewModel>();
        _checkingAll = true;
        try
        {
            foreach (var version in _versions.VersionRows)
            {
                if (known.Remove(version.Name, out var row))
                {
                    row.Version = version;
                }
                else
                {
                    row = new CleanupRowViewModel(version);
                    row.PropertyChanged += OnRowChanged;
                }
                rows.Add(row);
            }
            foreach (var gone in known.Values)
            {
                gone.PropertyChanged -= OnRowChanged;
                gone.IsChecked = false;
            }
        }
        finally
        {
            _checkingAll = false;
        }
        _rows.ReplaceAll(rows);
        OnSelectionChanged();
    }

    /// <summary>
    /// The saved plan or its unsaved state changed (edited, saved, reverted, reloaded): the scan goes stale when the saved
    /// rules differ from those it used, and Scan follows "Save first".
    /// </summary>
    public void OnPlanChanged()
    {
        if (_scan is { } scan && !IsStale &&
            (_savedPlan() is not { } plan || RuleFingerprint(plan, _globalDefaults(), scan.Nested) != scan.Fingerprint))
            IsStale = true;
        RefreshHint();
        NotifyCommands();
    }

    /// <summary>The language changed: the rows, the totals and the hint are read again.</summary>
    public void RefreshTexts()
    {
        foreach (var row in Rows)
            row.Refresh();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// What the rules a scan used are made of, to tell later whether the saved rules still are the same: the plan's
    /// flags and patterns, the global defaults when the plan uses them, and each <c>.backupignore</c> (its folder and
    /// lines) when the plan honors them.
    /// </summary>
    public static string RuleFingerprint(BackupPlan plan, IReadOnlyList<string> globalDefaults,
        IReadOnlyList<NestedIgnoreFile> nested)
    {
        var ignore = plan.Ignore;
        // Every line is indented, so no pattern can pass for a section header.
        var parts = new List<string> { $"defaults={ignore.UseGlobalDefaults}", $"nested={ignore.HonorNestedFiles}", "plan" };
        parts.AddRange(ignore.Patterns.Select(p => " " + p));
        if (ignore.UseGlobalDefaults)
        {
            parts.Add("global");
            parts.AddRange(globalDefaults.Select(p => " " + p));
        }
        if (ignore.HonorNestedFiles)
        {
            foreach (var file in nested)
            {
                parts.Add("file " + file.DirectoryRelativePath);
                parts.AddRange(file.Lines.Select(line => " " + line));
            }
        }
        return string.Join("\n", parts);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_checkingAll && e.PropertyName is nameof(CleanupRowViewModel.IsChecked) or "")
            OnSelectionChanged();
    }

    /// <summary>The checked rows changed: the header box, Scan, and the scan goes stale when it covered other versions.</summary>
    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(AllChecked));
        if (_scan is { } scan && !IsStale &&
            !scan.Names.SetEquals(Rows.Where(r => r.IsChecked).Select(r => r.Name)))
        {
            IsStale = true;
            RefreshHint();
        }
        NotifyCommands();
    }

    /// <summary>A deletion, resume, restore or cleanup of the plan started or ended: Scan and "Clean up…" look again.</summary>
    private void OnVersionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VersionsViewModel.IsDeleting) or nameof(VersionsViewModel.IsResuming)
            or nameof(VersionsViewModel.IsRestoring) or "")
            NotifyCommands();
    }

    /// <summary>Unsaved edits first ("Save first"), then the source scan's line, then "Scan again".</summary>
    private void RefreshHint() =>
        Hint = _hasUnsavedChanges() ? LocText.Of("cleanup.saveFirst")
            : _scanHint is { } scanHint ? scanHint
            : IsStale ? LocText.Of("cleanup.scanAgain")
            : null;

    private void NotifyCommands()
    {
        ScanCommand.NotifyCanExecuteChanged();
        CleanUpCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}
