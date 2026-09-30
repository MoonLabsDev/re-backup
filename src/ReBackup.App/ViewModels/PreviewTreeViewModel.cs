using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Indexing;

namespace ReBackup.App.ViewModels;

/// <summary>The preview tree flattened to its visible rows, so that a plain virtualized list can show it.</summary>
public sealed partial class PreviewTreeViewModel : ObservableObject
{
    private EvaluatedNode? _root;

    [ObservableProperty] private PreviewRowViewModel? _selectedRow;

    public RangeObservableCollection<PreviewRowViewModel> Rows { get; } = new();

    /// <summary>Shows a new evaluation. Expanded folders and the selection are kept by path.</summary>
    public void SetRoot(EvaluatedNode? root)
    {
        var expanded = Rows.Where(r => r.IsExpanded).Select(r => r.Node.Node.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedPath = SelectedRow?.Node.Node.RelativePath;
        if (_root is null)
            expanded.Add("");   // first evaluation: open the root

        _root = root;
        var rows = new List<PreviewRowViewModel>();
        if (root is not null)
            AppendRows(rows, root, root.TotalSize, 0, expanded);
        Rows.ReplaceAll(rows);

        SelectedRow = selectedPath is null
            ? null
            : Rows.FirstOrDefault(r => r.Node.Node.RelativePath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Expands the folders above the node and selects its row.</summary>
    public void Reveal(EvaluatedNode target)
    {
        if (_root is null || Rows.Count == 0)
            return;

        var current = _root;
        var row = Rows[0];
        var path = target.Node.RelativePath;
        if (path.Length > 0)
        {
            foreach (var part in path.Split('/'))
            {
                var child = current.Children.FirstOrDefault(c => c.Node.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                    return;
                row.IsExpanded = true;
                var childRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Node, child));
                if (childRow is null)
                    return;
                current = child;
                row = childRow;
            }
        }
        SelectedRow = row;
    }

    internal void OnExpandedChanged(PreviewRowViewModel row, bool expanded)
    {
        var index = Rows.IndexOf(row);
        if (index < 0)
            return;

        if (expanded)
        {
            var children = Sorted(row.Node)
                .Select(child => new PreviewRowViewModel(this, child, row.Node.TotalSize, row.Depth + 1, isExpanded: false))
                .ToList();
            Rows.InsertRange(index + 1, children);
        }
        else
        {
            var count = 0;
            while (index + 1 + count < Rows.Count && Rows[index + 1 + count].Depth > row.Depth)
                count++;
            Rows.RemoveRange(index + 1, count);
        }
    }

    private void AppendRows(List<PreviewRowViewModel> rows, EvaluatedNode node, long parentTotalSize, int depth,
        HashSet<string> expanded)
    {
        var isExpanded = node.Children.Count > 0 && expanded.Contains(node.Node.RelativePath);
        rows.Add(new PreviewRowViewModel(this, node, parentTotalSize, depth, isExpanded));
        if (!isExpanded)
            return;
        foreach (var child in Sorted(node))
            AppendRows(rows, child, node.TotalSize, depth + 1, expanded);
    }

    private static IEnumerable<EvaluatedNode> Sorted(EvaluatedNode node) =>
        node.Children
            .OrderByDescending(c => c.TotalSize)
            .ThenBy(c => c.Node.Name, StringComparer.OrdinalIgnoreCase);
}
