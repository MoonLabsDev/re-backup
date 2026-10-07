using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.App.Localization;
using ReBackup.Core.Backup;

namespace ReBackup.App.ViewModels;

/// <summary>The skipped entries of one run as a flattened tree (History tab, below the selected run).</summary>
public sealed class SkippedTreeViewModel
{
    /// <summary>Up to this many nodes everything starts expanded; above it only the top level.</summary>
    private const int ExpandAllUpTo = 300;

    private readonly IReadOnlyList<SkippedNode> _roots;
    private readonly HashSet<SkippedNode> _expanded = [];

    public SkippedTreeViewModel(IEnumerable<SkippedEntry> entries)
    {
        _roots = SkippedTree.Build(entries);
        var folders = new List<SkippedNode>();
        Collect(_roots, folders, out var nodes);
        foreach (var folder in nodes <= ExpandAllUpTo ? folders : folders.Where(f => _roots.Contains(f)))
            _expanded.Add(folder);
        Rebuild();
    }

    public RangeObservableCollection<SkippedRowViewModel> Rows { get; } = new();

    internal bool IsExpanded(SkippedNode node) => _expanded.Contains(node);

    internal void SetExpanded(SkippedNode node, bool expanded)
    {
        if (expanded ? _expanded.Add(node) : _expanded.Remove(node))
            Rebuild();
    }

    /// <summary>The language changed: the reasons and counts are read again.</summary>
    public void RefreshTexts()
    {
        foreach (var row in Rows)
            row.Refresh();
    }

    private void Rebuild()
    {
        var rows = new List<SkippedRowViewModel>();
        Append(rows, _roots, 0);
        Rows.ReplaceAll(rows);
    }

    private void Append(List<SkippedRowViewModel> rows, IReadOnlyList<SkippedNode> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            rows.Add(new SkippedRowViewModel(this, node, depth));
            if (_expanded.Contains(node))
                Append(rows, node.Children, depth + 1);
        }
    }

    private static void Collect(IReadOnlyList<SkippedNode> nodes, List<SkippedNode> folders, out int count)
    {
        count = 0;
        foreach (var node in nodes)
        {
            count++;
            if (node.Children.Count == 0)
                continue;
            folders.Add(node);
            Collect(node.Children, folders, out var below);
            count += below;
        }
    }
}

/// <summary>A row of <see cref="SkippedTreeViewModel"/>: a folder with its counts, or a skipped entry with its reason.</summary>
public sealed class SkippedRowViewModel : ObservableObject
{
    private const double IndentPerLevel = 16;
    private readonly SkippedTreeViewModel _owner;
    private readonly SkippedNode _node;

    internal SkippedRowViewModel(SkippedTreeViewModel owner, SkippedNode node, int depth)
    {
        _owner = owner;
        _node = node;
        Indent = new Thickness(depth * IndentPerLevel, 0, 0, 0);
    }

    public string Name => _node.Name;

    /// <summary>The full relative path (tool tip).</summary>
    public string Path => _node.Path;

    public bool IsDirectory => _node.IsDirectory;

    public bool IsExpandable => _node.Children.Count > 0;

    public Thickness Indent { get; }

    public bool IsExpanded
    {
        get => _owner.IsExpanded(_node);
        set => _owner.SetExpanded(_node, value);
    }

    /// <summary>Why the entry itself was skipped (for its dot); null for a folder that only holds skipped entries.</summary>
    public SkipKind? Kind => _node.Kind;

    /// <summary>The entry's reason; for a folder its counts by kind, after its own reason if it was skipped itself.</summary>
    public string ReasonText
    {
        get
        {
            var own = _node.Reason is { } reason ? Loc.Known(reason) : null;
            if (_node.Children.Count == 0)
                return own ?? "";
            var parts = new List<string>();
            if (_node.Deleted > 0)
                parts.Add(Loc.F("history.skippedTree.deleted", ("count", _node.Deleted)));
            if (_node.Changed > 0)
                parts.Add(Loc.F("history.skippedTree.changed", ("count", _node.Changed)));
            if (_node.Other > 0)
                parts.Add(Loc.F("history.skippedTree.other", ("count", _node.Other)));
            var counts = string.Join(" · ", parts);
            return own is null ? counts : $"{own} · {counts}";
        }
    }

    /// <summary>Folder rows show their counts muted; entries show their reason in the colour of their kind.</summary>
    public bool IsSummary => _node.Children.Count > 0 && _node.Reason is null;

    public void Refresh() => OnPropertyChanged(nameof(ReasonText));
}
