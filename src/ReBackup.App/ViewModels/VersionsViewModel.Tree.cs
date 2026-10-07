using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The Versions tab: tree of version A, comparison with B, search and the history of a file. Queries read the index
/// beside a running sync (off the UI thread) and only with the ids of the rows' <see cref="VersionRowViewModel.Indexed"/>,
/// which are replaced after every sync: whenever those of A or B change, the tree stops using the old ones at once
/// and is loaded again.
/// </summary>
public sealed partial class VersionsViewModel
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);
    private static readonly CompareChoice NoCompare = new("—", null);
    private int _suppressReload;
    private int _treeGeneration;
    private int _historyGeneration;
    private bool _treeReloadPosted;

    /// <summary>What the tree currently shows (null while it loads or shows a message).</summary>
    private (VersionIndex Index, IndexedVersion A, IndexedVersion? B)? _shown;
    private CancellationTokenSource? _searchCts;
    private VersionRowViewModel? _watchedA;
    private VersionRowViewModel? _watchedB;

    [ObservableProperty] private IReadOnlyList<CompareChoice> _compareChoices = [NoCompare];

    /// <summary>Version B; <see cref="NoCompare"/> (or null) while nothing is compared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComparing))]
    [NotifyCanExecuteChangedFor(nameof(SwapCommand))]
    private CompareChoice? _selectedCompare = NoCompare;

    /// <summary>Hides Unchanged entries while comparing.</summary>
    [ObservableProperty] private bool _changedOnly;

    /// <summary>Substring or wildcard pattern (<c>*</c>, <c>?</c>); a non-blank text replaces the tree with the hits.</summary>
    [ObservableProperty] private string _searchText = "";

    [ObservableProperty] private bool _isSearchActive;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchSummary))]
    private LocText _searchSummaryText = LocText.Empty;

    public string SearchSummary => SearchSummaryText.ToString();
    [ObservableProperty] private SearchHitRow? _selectedSearchHit;

    [ObservableProperty] private bool _showHistory;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryTitle))]
    private LocText _historyTitleText = LocText.Empty;

    public string HistoryTitle => HistoryTitleText.ToString();

    partial void OnRefreshTexts()
    {
        RebuildCompareChoices();   // the labels of not-managed versions
        Tree.RefreshTexts();
        foreach (var row in SearchResults)
            row.Refresh();
        foreach (var row in HistoryRows)
            row.Refresh();
    }

    public VersionTreeViewModel Tree { get; } = new();

    public bool IsComparing => SelectedCompare?.Row is not null;

    public RangeObservableCollection<SearchHitRow> SearchResults { get; } = new();

    /// <summary>The selected file in every indexed version, newest first.</summary>
    public RangeObservableCollection<FileHistoryRow> HistoryRows { get; } = new();

    partial void OnCreated()
    {
        Tree.PropertyChanged += OnTreePropertyChanged;
        VersionRows.CollectionChanged += OnVersionRowsChanged;
    }

    partial void OnIndexReady()
    {
        var compareChanged = RebuildCompareChoices();
        // A sync that left A's and B's rows (and the index) as they were does not recompute the tree or the comparison.
        if (!compareChanged && _shown is { } shown && ReferenceEquals(shown.Index, _index) &&
            shown.A == SelectedVersion?.Indexed && shown.B == SelectedCompare?.Row?.Indexed)
            return;
        RequestTreeReload();
    }

    partial void OnSelectedVersionChanged(VersionRowViewModel? value)
    {
        RebuildCompareChoices();
        SearchText = "";
        if (_suppressReload == 0)
            RequestTreeReload();
    }

    partial void OnSelectedCompareChanged(CompareChoice? value)
    {
        if (_suppressReload == 0)
            RequestTreeReload();
    }

    partial void OnChangedOnlyChanged(bool value) => Tree.SetChangedOnly(value);

    partial void OnSearchTextChanged(string value) => _ = SearchAsync(value);

    partial void OnSelectedSearchHitChanged(SearchHitRow? value)
    {
        if (value is null)
            return;
        var hit = value.Hit;
        SearchText = "";   // back to the tree
        _ = RevealAsync(hit);
    }

    /// <summary>Runs whenever the tree's selection changes (the actions of the tab hook in here).</summary>
    partial void OnTreeSelectionChanged();

    /// <summary>
    /// Shows B's tree compared with A: the two versions change places. The statuses keep their direction (older to
    /// newer); only whose tree is shown changes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsComparing))]
    private void Swap()
    {
        if (SelectedVersion is not { } a || SelectedCompare?.Row is not { } b)
            return;
        _suppressReload++;
        try
        {
            SelectedVersion = b;
            SelectedCompare = CompareChoices.FirstOrDefault(c => ReferenceEquals(c.Row, a)) ?? NoCompare;
        }
        finally
        {
            _suppressReload--;
        }
        RequestTreeReload();
    }

    /// <summary>Shows the history row's copy of the file in Explorer.</summary>
    [RelayCommand]
    private async Task OpenHistoryCopyAsync(FileHistoryRow? row)
    {
        if (row is not { CanOpen: true, VersionFolder: { } folder })
            return;
        var path = row.RelativePath;
        if (!await Task.Run(() => _files.ShowInExplorer(folder, path)))
            _context.ReportStatus(LocText.Of("versions.copyMissing", ("version", row.VersionName)));
    }

    /// <summary>Rebuilds the "Compare with" box; true when version B is no longer the same (it left the list).</summary>
    private bool RebuildCompareChoices()
    {
        var current = SelectedCompare?.Row;
        var choices = new List<CompareChoice> { NoCompare };
        choices.AddRange(VersionRows
            .Where(row => !ReferenceEquals(row, SelectedVersion))
            .Select(row => new CompareChoice(row.DateText + (row.IsManaged ? "" : Loc.T("versions.compare.notManaged")), row)));
        _suppressReload++;
        try
        {
            CompareChoices = choices;
            SelectedCompare = choices.FirstOrDefault(c => c.Row is not null && ReferenceEquals(c.Row, current)) ?? NoCompare;
        }
        finally
        {
            _suppressReload--;
        }
        return !ReferenceEquals(SelectedCompare?.Row, current);
    }

    /// <summary>The version list was read again: new versions join the compare box; a vanished B ends the comparison.</summary>
    private void OnVersionRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (RebuildCompareChoices() && _suppressReload == 0)
            RequestTreeReload();
    }

    /// <summary>
    /// Stops the tree from querying with the ids it has (they may be gone) and loads it again once the current UI
    /// work is done, so that several changes in one step (a sync's new ids for A and B, then OnIndexReady) load it once.
    /// </summary>
    private void RequestTreeReload()
    {
        _treeGeneration++;   // a reload that is still querying drops its result
        _shown = null;
        Tree.Suspend();
        if (SynchronizationContext.Current is not { } ui)
        {
            _ = ReloadTreeAsync();
            return;
        }
        if (_treeReloadPosted)
            return;
        _treeReloadPosted = true;
        ui.Post(_ =>
        {
            _treeReloadPosted = false;
            _ = ReloadTreeAsync();
        }, null);
    }

    /// <summary>
    /// Shows version A's tree; while comparing, with the statuses from the older to the newer of the two versions
    /// (§11: Added = only in the newer one), whichever of them is A.
    /// </summary>
    private async Task ReloadTreeAsync()
    {
        var generation = ++_treeGeneration;
        _shown = null;
        WatchRows(SelectedVersion, SelectedCompare?.Row);
        if (!IsComparing)
            ChangedOnly = false;

        if (SelectedVersion is not { } a)
        {
            Tree.ShowMessage(VersionRows.Count == 0 ? LocText.Empty : LocText.Of("versions.tree.selectVersion"));
            return;
        }
        if (_index is not { } index || a.Indexed is not { } versionA)
        {
            Tree.ShowMessage(a.IndexState == IndexState.Failed ? LocText.Of("versions.tree.notIndexed") : LocText.Of("versions.tree.indexing"));
            return;
        }

        var other = SelectedCompare?.Row?.Indexed;
        if (IsComparing && other is null)
        {
            // B is listed but not (or no longer) indexed: no comparison until its sync gives it an id.
            Tree.ShowMessage(LocText.Of("versions.tree.otherIndexing"));
            return;
        }

        IReadOnlyDictionary<long, DiffStatus>? statuses = null;
        try
        {
            if (other is not null)
            {
                var aFirst = versionA.LocalTime != other.LocalTime
                    ? versionA.LocalTime < other.LocalTime
                    : string.Compare(versionA.Name, other.Name, StringComparison.OrdinalIgnoreCase) <= 0;
                var (older, newer) = aFirst ? (versionA, other) : (other, versionA);
                statuses = await Task.Run(() => index.Compare(older.Id, newer.Id));
                if (generation != _treeGeneration)
                    return;
            }
            await Tree.ShowAsync(index, versionA.Id, other?.Id, statuses, ChangedOnly);
            if (generation == _treeGeneration)
                _shown = (index, versionA, other);
        }
        catch (Exception ex)
        {
            if (generation == _treeGeneration)
                Tree.ShowMessage(LocText.Of("versions.tree.unreadable", ("error", ex.Message)));
        }
    }

    /// <summary>Follows the Indexed ids of A and B: a sync that imports either again replaces them.</summary>
    private void WatchRows(VersionRowViewModel? a, VersionRowViewModel? b)
    {
        if (!ReferenceEquals(_watchedA, a))
        {
            if (_watchedA is not null)
                _watchedA.PropertyChanged -= OnWatchedRowChanged;
            _watchedA = a;
            if (a is not null)
                a.PropertyChanged += OnWatchedRowChanged;
        }
        if (!ReferenceEquals(_watchedB, b))
        {
            if (_watchedB is not null)
                _watchedB.PropertyChanged -= OnWatchedRowChanged;
            _watchedB = b;
            if (b is not null)
                b.PropertyChanged += OnWatchedRowChanged;
        }
    }

    private void OnWatchedRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(VersionRowViewModel.Indexed) or nameof(VersionRowViewModel.IndexState)))
            return;
        if (e.PropertyName == nameof(VersionRowViewModel.Indexed) && ReferenceEquals(sender, SelectedVersion) &&
            !string.IsNullOrWhiteSpace(SearchText))
            _ = SearchAsync(SearchText);   // the hits were found with the old id
        RequestTreeReload();
    }

    private async Task RevealAsync(SearchHit hit)
    {
        await Tree.RevealAsync(hit.Path, hit.IsDirectory);
        // The tree shows an Unchanged hit even under "changed only"; the switch follows it.
        if (ChangedOnly && Tree.SelectedNode is { Status: DiffStatus.Unchanged })
            ChangedOnly = false;
    }

    private async Task SearchAsync(string text)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(text))
        {
            IsSearchActive = false;
            SearchResults.ReplaceAll([]);
            SearchSummaryText = LocText.Empty;
            return;
        }

        try
        {
            await Task.Delay(SearchDelay, cts.Token);
            IsSearchActive = true;
            if (_index is not { } index || SelectedVersion?.Indexed is not { } version)
            {
                SearchResults.ReplaceAll([]);
                SearchSummaryText = LocText.Of("versions.search.notIndexed");
                return;
            }

            var hits = await Task.Run(() => index.Search(version.Id, text), cts.Token);
            // A sync may have replaced the id meanwhile; the search then runs again with the new one.
            if (!ReferenceEquals(_searchCts, cts) || SelectedVersion?.Indexed?.Id != version.Id)
                return;
            SearchResults.ReplaceAll(hits.Select(hit => new SearchHitRow(hit)));
            SearchSummaryText = hits.Count == 0 ? LocText.Of("versions.search.none")
                : hits.Count >= VersionIndex.SearchLimit ? LocText.Of("versions.search.capped", ("limit", VersionIndex.SearchLimit))
                : LocText.Of("versions.search.count", ("count", hits.Count));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_searchCts, cts))
                SearchSummaryText = LocText.Of("versions.search.failed", ("error", ex.Message));
        }
    }

    private void OnTreePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VersionTreeViewModel.SelectedNode))
            return;
        _ = LoadHistoryAsync(Tree.SelectedNode);
        OnTreeSelectionChanged();
    }

    /// <summary>
    /// The history of the selected file. It is read by path id, which every version shares, and loaded again whenever
    /// the tree is (the tree selects the new row of the same file).
    /// </summary>
    private async Task LoadHistoryAsync(VersionTreeNode? node)
    {
        var generation = ++_historyGeneration;
        if (node is not { IsMessage: false, IsDirectory: false } || _index is not { } index)
        {
            ShowHistory = false;
            HistoryRows.ReplaceAll([]);
            return;
        }

        HistoryTitleText = LocText.Of("versions.history.title", ("name", node.Name));
        ShowHistory = true;
        try
        {
            var pathId = node.PathId;
            var entries = await Task.Run(() => index.History(pathId));
            if (generation != _historyGeneration)
                return;
            var folders = VersionRows.ToDictionary(r => r.Name, r => r.Info.Path, StringComparer.OrdinalIgnoreCase);
            HistoryRows.ReplaceAll(entries.Reverse()
                .Select(entry => new FileHistoryRow(entry, folders.GetValueOrDefault(entry.Version.Name), node.Path)));
        }
        catch (Exception ex)
        {
            if (generation != _historyGeneration)
                return;
            HistoryRows.ReplaceAll([]);
            HistoryTitleText = LocText.Of("versions.history.unreadable", ("name", node.Name), ("error", ex.Message));
        }
    }
}
