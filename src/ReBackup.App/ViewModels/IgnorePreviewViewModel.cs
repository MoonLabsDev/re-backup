using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;
using ReBackup.Core.Plans;

namespace ReBackup.App.ViewModels;

/// <summary>
/// Scans a plan's source live (several folders at a time; the tree grows while it runs) and afterwards re-evaluates
/// the ignore patterns against the cached index without a rescan.
/// </summary>
public sealed partial class IgnorePreviewViewModel : ObservableObject
{
    private const string NotIndexedText = "Not indexed yet.";
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

    [ObservableProperty] private string _progressText = NotIndexedText;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _summary = "";

    /// <summary>The evaluated tree of the last complete scan (treemap, sizes); null while a scan runs.</summary>
    [ObservableProperty] private EvaluatedNode? _root;

    [ObservableProperty] private IPreviewEntry? _selectedNode;

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
    public string? PatternNote => IsIndexing ? "Changes to the patterns are applied when the scan is finished." : null;

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
        IsIndexing = false;
        Root = null;
        Tree.SetRoot(null);
        Summary = "";
        Error = null;
        ProgressText = NotIndexedText;
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
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task IndexAsync()
    {
        var source = _source();
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            Error = "The source folder does not exist.";
            return;
        }

        _indexCts?.Cancel();
        _evaluateCts?.Cancel();
        _evaluationVersion++;   // an evaluation still running from an earlier cancel must not publish over this scan
        var cts = _indexCts = new CancellationTokenSource();
        Error = null;

        LiveScan scan;
        try
        {
            scan = LiveScan.Start(source, _ignoreSettings(), _globalDefaults().ToList(), cancellationToken: cts.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Error = ex.Message;
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
            ProgressText = $"Indexed {index.FileCount:N0} files in {index.DirectoryCount:N0} folders.";
            RequestReevaluate();   // in case the patterns were edited while the result was being evaluated
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(_indexCts, cts))
                return;
            _scan = null;
            IsIndexing = false;
            if (_index is { } previous)
            {
                // Stop as soon as this scan was superseded (Invalidate / a new scan): the old index must not be published.
                var shown = false;
                while (ReferenceEquals(_indexCts, cts) && !(shown = await EvaluateAsync(previous, CancellationToken.None)))
                {
                }
                if (shown && ReferenceEquals(_indexCts, cts))
                    ProgressText = "Indexing canceled; showing the previous index.";
            }
            else
            {
                Tree.Refresh();
                ProgressText = "Scan canceled — incomplete.";
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_indexCts, cts))
                return;
            _scan = null;
            Tree.Refresh();
            Error = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_indexCts, cts))
                IsIndexing = false;
        }
    }

    [RelayCommand]
    private void CancelIndex() => _indexCts?.Cancel();

    private void ShowLive(LiveScan scan)
    {
        Tree.Refresh();
        ProgressText = $"{scan.Files:N0} files, {scan.Directories:N0} folders so far — {scan.WaitingFolders:N0} folders waiting";
        var root = scan.Root;
        Summary = $"So far — included: {root.IncludedFiles:N0} files, {ByteSize.Format(root.IncludedSize)}   ·   " +
                  $"ignored: {root.IgnoredFiles:N0} files, {ByteSize.Format(root.IgnoredSize)}";
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
        Error = null;
        Root = root;
        Tree.SetRoot(root);
        Summary = $"Included: {root.IncludedFiles:N0} files, {ByteSize.Format(root.IncludedSize)}   ·   " +
                  $"Ignored: {root.IgnoredFiles:N0} files, {ByteSize.Format(root.IgnoredSize)}";
        return true;
    }
}
