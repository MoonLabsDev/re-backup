using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;

namespace ReBackup.App.ViewModels;

/// <summary>
/// Scans a plan's source live (several folders at a time; the tree grows while it runs) and afterwards re-evaluates
/// the ignore patterns against the cached index without a rescan.
/// </summary>
public sealed partial class IgnorePreviewViewModel : ObservableObject
{
    private static readonly TimeSpan ReevaluateDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<string> _source;
    private readonly Func<IgnoreSettings> _ignoreSettings;
    private readonly Func<IReadOnlyList<string>> _globalDefaults;
    private SourceIndex? _index;
    private LiveScan? _scan;
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _evaluateCts;
    private int _evaluationVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatternNote))]
    private bool _isIndexing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private LocText _progress = LocText.Of("ignore.progress.notIndexed");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    private LocText? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private LocText _summaryText = LocText.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InBackupText))]
    private LocText _inBackupPill = LocText.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IgnoredText))]
    private LocText _ignoredPill = LocText.Empty;

    public string ProgressText => Progress.ToString();

    public string? Error => ErrorText?.ToString();

    public string Summary => SummaryText.ToString();

    /// <summary>Size and files that go into the backup, for the summary pill ("" before the first scan).</summary>
    public string InBackupText => InBackupPill.ToString();

    /// <summary>Size and files that are ignored, for the summary pill ("" before the first scan).</summary>
    public string IgnoredText => IgnoredPill.ToString();

    /// <summary>The language changed: the texts and the tree's rows are read again.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
        foreach (var row in Tree.Rows)
            row.Refresh();
    }

    /// <summary>The evaluated tree of the last complete scan (treemap, sizes); null while a scan runs.</summary>
    [ObservableProperty] private EvaluatedNode? _root;

    [ObservableProperty] private IPreviewEntry? _selectedNode;

    /// <summary>Included size of the last complete evaluation; kept during a rescan (Root is null then). Null when none.</summary>
    public long? LastEvaluatedIncludedSize { get; private set; }

    public IgnorePreviewViewModel(Func<string> source, Func<IgnoreSettings> ignoreSettings,
        Func<IReadOnlyList<string>> globalDefaults)
    {
        _source = source;
        _ignoreSettings = ignoreSettings;
        _globalDefaults = globalDefaults;
        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewTreeViewModel.SelectedRow))
                SelectedNode = Tree.SelectedRow?.Entry;
        };
        Tree.LoadingFolderExpanded += entry =>
        {
            if (_scan is { } scan && entry is LiveNode folder)
                scan.Prioritize(folder);
        };
    }

    public PreviewTreeViewModel Tree { get; } = new();

    /// <summary>Shown next to the patterns while a scan runs.</summary>
    public string? PatternNote => IsIndexing ? Loc.T("ignore.patternNote") : null;

    partial void OnSelectedNodeChanged(IPreviewEntry? value)
    {
        // Selection coming from outside the tree (the treemap): show it in the tree.
        if (value is not null && !ReferenceEquals(Tree.SelectedRow?.Entry, value))
            Tree.Reveal(value);
    }

    /// <summary>Drops the cached index, e.g. after the source folder changed.</summary>
    public void Invalidate()
    {
        _indexCts?.Cancel();
        _indexCts = null;   // the aborted scan must not touch the state below any more
        _scan = null;
        _evaluateCts?.Cancel();
        _evaluationVersion++;
        _index = null;
        LastEvaluatedIncludedSize = null;
        IsIndexing = false;
        Root = null;
        Tree.SetRoot(null);
        SummaryText = LocText.Empty;
        InBackupPill = LocText.Empty;
        IgnoredPill = LocText.Empty;
        ErrorText = null;
        Progress = LocText.Of("ignore.progress.notIndexed");
    }

    /// <summary>Re-applies the patterns to the cached index after a short pause in typing. No rescan; not during a scan.</summary>
    public async void RequestReevaluate()
    {
        // During a scan the patterns are applied when it is finished (it is then evaluated with the current ones).
        if (_index is null || IsIndexing)
            return;

        _evaluateCts?.Cancel();
        var cts = _evaluateCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(ReevaluateDelay, cts.Token);
            if (_index is { } index && !IsIndexing)
                await EvaluateAsync(index, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorText = LocText.Known(ex.Message);
        }
    }

    [RelayCommand]
    private async Task IndexAsync()
    {
        var source = _source();
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            ErrorText = LocText.Of("ignore.sourceMissing");
            return;
        }

        _indexCts?.Cancel();
        _evaluateCts?.Cancel();
        _evaluationVersion++;   // an evaluation still running from an earlier cancel must not publish over this scan
        var cts = _indexCts = new CancellationTokenSource();
        ErrorText = null;

        LiveScan scan;
        try
        {
            scan = LiveScan.Start(source, _ignoreSettings(), _globalDefaults().ToList(), cancellationToken: cts.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ErrorText = LocText.Known(ex.Message);
            return;
        }

        _scan = scan;
        IsIndexing = true;
        Root = null;   // the treemap waits for the finished scan
        Tree.SetRoot(scan.Root);

        try
        {
            while (!scan.Completion.IsCompleted)
            {
                await Task.WhenAny(scan.Completion, Task.Delay(LiveRefreshInterval));
                if (!ReferenceEquals(_indexCts, cts))
                    return;   // superseded by a new scan or dropped by Invalidate
                ShowLive(scan);
            }

            var index = await scan.Completion;
            _scan = null;
            // A newer evaluation (pattern edit) can supersede this one; repeat until the new index is shown.
            while (!await EvaluateAsync(index, cts.Token))
            {
            }
            IsIndexing = false;
            Progress = LocText.Of("ignore.progress.indexed", ("files", Formats.Files(index.FileCount)),
                ("folders", Formats.Folders(index.DirectoryCount)));
            RequestReevaluate();   // in case the patterns were edited while the result was being evaluated
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(_indexCts, cts))
                return;
            _scan = null;
            IsIndexing = false;
            if (_index is not null)
            {
                if (await ShowPreviousIndexAsync(cts))
                    Progress = LocText.Of("ignore.progress.canceledPrevious");
            }
            else
            {
                Tree.Refresh();
                Progress = LocText.Of("ignore.progress.canceledIncomplete");
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_indexCts, cts))
                return;
            _scan = null;
            IsIndexing = false;
            // Like a cancel: show the previous index again (so the tree, the index and the progress line agree),
            // or keep the partial tree when there is none. The error stays visible either way.
            var restored = _index is not null && await ShowPreviousIndexAsync(cts);
            if (!ReferenceEquals(_indexCts, cts))
                return;
            if (!restored)
                Tree.Refresh();
            Progress = restored ? LocText.Of("ignore.progress.failedPrevious") : LocText.Of("ignore.progress.failedIncomplete");
            ErrorText = LocText.Known(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_indexCts, cts))
                IsIndexing = false;
        }
    }

    /// <summary>
    /// Re-evaluates the previous index and shows it. Stops as soon as the scan was superseded (Invalidate / a new
    /// scan), because the old index must not be published then. True when it was shown.
    /// </summary>
    private async Task<bool> ShowPreviousIndexAsync(CancellationTokenSource cts)
    {
        var shown = false;
        try
        {
            while (_index is { } previous && ReferenceEquals(_indexCts, cts) &&
                   !(shown = await EvaluateAsync(previous, CancellationToken.None)))
            {
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ReferenceEquals(_indexCts, cts))
                ErrorText = LocText.Known(ex.Message);   // must not escape the command
            return false;
        }
        return shown && ReferenceEquals(_indexCts, cts);
    }

    [RelayCommand]
    private void CancelIndex() => _indexCts?.Cancel();

    private void ShowLive(LiveScan scan)
    {
        Tree.Refresh();
        Progress = LocText.Of("ignore.progress.live",
            ("files", Formats.Files(scan.Files)), ("folders", Formats.Folders(scan.Directories)),
            ("waiting", Formats.Folders(scan.WaitingFolders)));
        var root = scan.Root;
        SummaryText = SummaryOf("ignore.summary.live", root);
        SetPills(root, "≥ ");
    }

    /// <summary>The summary line of the numbers <paramref name="root"/> has now.</summary>
    private static LocText SummaryOf(string key, IPreviewEntry root)
    {
        var (includedFiles, includedSize, ignoredFiles, ignoredSize) =
            (root.IncludedFiles, root.IncludedSize, root.IgnoredFiles, root.IgnoredSize);
        return new LocText(() => Loc.F(key, ("includedFiles", Formats.Files(includedFiles)),
            ("includedSize", Formats.Bytes(includedSize)), ("ignoredFiles", Formats.Files(ignoredFiles)),
            ("ignoredSize", Formats.Bytes(ignoredSize))));
    }

    private void SetPills(IPreviewEntry root, string prefix)
    {
        var (includedSize, includedFiles, ignoredSize, ignoredFiles) =
            (root.IncludedSize, root.IncludedFiles, root.IgnoredSize, root.IgnoredFiles);
        InBackupPill = new LocText(() => Loc.F("ignore.pill.inBackup",
            ("prefix", prefix), ("size", Formats.Bytes(includedSize)), ("files", Formats.Files(includedFiles))));
        IgnoredPill = new LocText(() => Loc.F("ignore.pill.ignored",
            ("prefix", prefix), ("size", Formats.Bytes(ignoredSize)), ("files", Formats.Files(ignoredFiles))));
    }

    /// <summary>Evaluates and publishes the index. False means a newer evaluation superseded this one.</summary>
    private async Task<bool> EvaluateAsync(SourceIndex index, CancellationToken cancellationToken)
    {
        var version = ++_evaluationVersion;

        // Snapshots, because the evaluation runs on a worker thread.
        var settings = _ignoreSettings();
        var globalDefaults = _globalDefaults().ToList();

        var root = await Task.Run(
            () => IndexEvaluator.Evaluate(index, IgnoreMatcher.ForPlan(settings, globalDefaults, index.IgnoreFiles), cancellationToken),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (version != _evaluationVersion)
            return false;

        _index = index;
        LastEvaluatedIncludedSize = root.IncludedSize;
        ErrorText = null;
        Root = root;
        Tree.SetRoot(root);
        SummaryText = SummaryOf("ignore.summary.done", root);
        SetPills(root, "");
        return true;
    }
}
