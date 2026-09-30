using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;
using ReBackup.Core.Plans;

namespace ReBackup.App.ViewModels;

/// <summary>Indexes a plan's source once and re-evaluates the ignore patterns against that cached index.</summary>
public sealed partial class IgnorePreviewViewModel : ObservableObject
{
    private const string NotIndexedText = "Not indexed yet.";
    private static readonly TimeSpan ReevaluateDelay = TimeSpan.FromMilliseconds(300);

    private readonly Func<string> _source;
    private readonly Func<IgnoreSettings> _ignoreSettings;
    private readonly Func<IReadOnlyList<string>> _globalDefaults;
    private SourceIndex? _index;
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _evaluateCts;

    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private string _progressText = NotIndexedText;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private EvaluatedNode? _root;
    [ObservableProperty] private EvaluatedNode? _selectedNode;

    public IgnorePreviewViewModel(Func<string> source, Func<IgnoreSettings> ignoreSettings,
        Func<IReadOnlyList<string>> globalDefaults)
    {
        _source = source;
        _ignoreSettings = ignoreSettings;
        _globalDefaults = globalDefaults;
        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewTreeViewModel.SelectedRow))
                SelectedNode = Tree.SelectedRow?.Node;
        };
    }

    public PreviewTreeViewModel Tree { get; } = new();

    partial void OnSelectedNodeChanged(EvaluatedNode? value)
    {
        // Selection coming from outside the tree (the treemap): show it in the tree.
        if (value is not null && !ReferenceEquals(Tree.SelectedRow?.Node, value))
            Tree.Reveal(value);
    }

    /// <summary>Drops the cached index, e.g. after the source folder changed.</summary>
    public void Invalidate()
    {
        _indexCts?.Cancel();
        _evaluateCts?.Cancel();
        _index = null;
        Root = null;
        Tree.SetRoot(null);
        Summary = "";
        Error = null;
        ProgressText = NotIndexedText;
    }

    /// <summary>Re-applies the patterns to the cached index after a short pause in typing. No rescan.</summary>
    public async void RequestReevaluate()
    {
        if (_index is null)
            return;

        _evaluateCts?.Cancel();
        var cts = _evaluateCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(ReevaluateDelay, cts.Token);
            await EvaluateAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
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
        var cts = _indexCts = new CancellationTokenSource();
        IsIndexing = true;
        Error = null;
        var progress = new Progress<IndexProgress>(p =>
        {
            if (!cts.IsCancellationRequested && IsIndexing)
                ProgressText = $"{p.Files:N0} files, {p.Directories:N0} folders — {p.CurrentDirectory}";
        });

        try
        {
            var index = await SourceIndexer.BuildAsync(source, progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            _index = index;
            await EvaluateAsync(cts.Token);
            IsIndexing = false;
            ProgressText = $"Indexed {index.FileCount:N0} files in {index.DirectoryCount:N0} folders.";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_indexCts, cts))
                ProgressText = _index is null ? "Indexing canceled." : "Indexing canceled; showing the previous index.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        if (_index is not { } index)
            return;

        // Snapshots, because the evaluation runs on a worker thread.
        var settings = _ignoreSettings();
        var globalDefaults = _globalDefaults().ToList();

        var root = await Task.Run(
            () => IndexEvaluator.Evaluate(index, IgnoreMatcher.ForPlan(settings, globalDefaults, index.IgnoreFiles), cancellationToken),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Root = root;
        Tree.SetRoot(root);
        Summary = $"Included: {root.IncludedFiles:N0} files, {ByteSize.Format(root.IncludedSize)}   ·   " +
                  $"Ignored: {root.IgnoredFiles:N0} files, {ByteSize.Format(root.IgnoredSize)}";
    }
}
