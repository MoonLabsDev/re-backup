using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Indexing;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The preview tree flattened to its visible rows, so that a plain virtualized list can show it. Works on a finished
/// evaluation as well as on a scan that is still running (<see cref="Refresh"/> re-reads it).
/// </summary>
public sealed partial class PreviewTreeViewModel : ObservableObject
{
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IPreviewEntry, PreviewRowViewModel> _placeholders = new(ReferenceEqualityComparer.Instance);
    private IPreviewEntry? _root;

    [ObservableProperty] private PreviewRowViewModel? _selectedRow;

    public RangeObservableCollection<PreviewRowViewModel> Rows { get; } = new();

    /// <summary>Raised when a folder is expanded that is not finished yet.</summary>
    public event Action<IPreviewEntry>? LoadingFolderExpanded;

    /// <summary>Shows a new tree. Expanded folders and the selection are kept by path.</summary>
    public void SetRoot(IPreviewEntry? root)
    {
        if (_root is null)
            _expanded.Add("");   // first tree: open the root
        _root = root;
        _placeholders.Clear();
        Rebuild(reuseRows: false);
    }

    /// <summary>Re-reads a running scan: the values of the visible rows, new entries and a changed order.</summary>
    public void Refresh() => Rebuild(reuseRows: true);

    /// <summary>Expands the folders above the entry and selects its row.</summary>
    public void Reveal(IPreviewEntry target)
    {
        if (_root is null)
            return;

        var previous = SelectedRow;
        var current = _root;
        var found = true;
        _expanded.Add(_root.RelativePath);
        if (target.RelativePath.Length > 0)
        {
            foreach (var part in target.RelativePath.Split('/'))
            {
                var child = current.GetChildren().FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    found = false;
                    break;
                }
                _expanded.Add(current.RelativePath);
                current = child;
            }
        }

        Rebuild(reuseRows: true);
        var row = found ? Rows.FirstOrDefault(r => ReferenceEquals(r.Entry, current)) : null;
        SelectedRow = row ?? (previous is not null && Rows.Contains(previous) ? previous : null);
    }

    internal void OnExpandedChanged(PreviewRowViewModel row, bool expanded)
    {
        if (expanded)
            _expanded.Add(row.Entry.RelativePath);
        else
            _expanded.Remove(row.Entry.RelativePath);
        if (expanded && row.IsLoading)
            LoadingFolderExpanded?.Invoke(row.Entry);
        Rebuild(reuseRows: true);
    }

    private void Rebuild(bool reuseRows)
    {
        var selectedPath = SelectedRow is { IsPlaceholder: false } selected ? selected.Entry.RelativePath : null;
        var known = new Dictionary<IPreviewEntry, PreviewRowViewModel>(ReferenceEqualityComparer.Instance);
        if (reuseRows)
        {
            foreach (var row in Rows)
                known[row.Entry] = row;
        }

        var rows = new List<PreviewRowViewModel>();
        if (_root is not null)
            Append(rows, _root, _root, 0, known);

        if (rows.Count == Rows.Count && rows.Zip(Rows).All(pair => ReferenceEquals(pair.First, pair.Second)))
        {
            foreach (var row in rows)
                row.Refresh();
            return;
        }

        Rows.ReplaceAll(rows);
        foreach (var row in rows)
            row.Refresh();
        // The list view may drop its selection on a reset; put it back by path (or on the nearest visible folder above).
        SelectedRow = selectedPath is null ? null : FindRow(selectedPath);
    }

    private PreviewRowViewModel? FindRow(string path)
    {
        while (true)
        {
            var row = Rows.FirstOrDefault(r => !r.IsPlaceholder &&
                                               r.Entry.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (row is not null || path.Length == 0)
                return row;
            var slash = path.LastIndexOf('/');
            path = slash < 0 ? "" : path[..slash];
        }
    }

    private void Append(List<PreviewRowViewModel> rows, IPreviewEntry entry, IPreviewEntry parent, int depth,
        Dictionary<IPreviewEntry, PreviewRowViewModel> known)
    {
        var expanded = entry.IsDirectory && _expanded.Contains(entry.RelativePath);
        if (known.TryGetValue(entry, out var row))
            row.SyncExpanded(expanded);
        else
            row = new PreviewRowViewModel(this, entry, parent, depth, expanded);
        rows.Add(row);
        if (!expanded)
            return;

        var children = entry.GetChildren();
        if (children.Count == 0)
        {
            if (entry.State != ScanState.Done)
            {
                if (!_placeholders.TryGetValue(entry, out var placeholder))
                {
                    placeholder = new PreviewRowViewModel(this, new LoadingPlaceholder(entry.RelativePath), entry, depth + 1, false);
                    _placeholders[entry] = placeholder;
                }
                rows.Add(placeholder);
            }
            return;
        }

        foreach (var child in children
                     .OrderByDescending(c => c.TotalSize)
                     .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            Append(rows, child, entry, depth + 1, known);
        }
    }
}
