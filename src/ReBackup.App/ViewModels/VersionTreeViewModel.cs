using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The tree of one version (A), flattened to its visible rows for a virtualized list. Folders load their children from
/// the index when first expanded (off the UI thread). While comparing, entries only in B are shown too and every row
/// carries its status; "changed only" hides Unchanged rows. Expanded folders and the selection are kept by path id
/// (the same in every version) across reloads.
/// </summary>
public sealed partial class VersionTreeViewModel : ObservableObject
{
    private readonly HashSet<long> _expanded = [];
    private List<VersionTreeNode> _roots = [];
    private VersionIndex? _index;
    private long _versionId;
    private long? _otherId;
    private IReadOnlyDictionary<long, DiffStatus>? _statuses;
    private bool _changedOnly;
    private int _generation;

    /// <summary>The entry selected last; a reloaded tree selects it again even after a message was shown meanwhile.</summary>
    private long? _lastSelectedPathId;

    [ObservableProperty] private VersionTreeNode? _selectedNode;

    public RangeObservableCollection<VersionTreeNode> Rows { get; } = new();

    /// <summary>Shows a message instead of a tree (no version, not indexed yet, an error).</summary>
    public void ShowMessage(string message)
    {
        _generation++;
        _index = null;
        _statuses = null;
        _roots = [new VersionTreeNode(message, 0)];
        Rebuild();
    }

    /// <summary>
    /// Stops using the version ids of the tree shown (they may be gone after a re-import): loads still running are
    /// dropped and nothing new is queried until the next <see cref="ShowAsync"/> or <see cref="ShowMessage"/>. The rows
    /// stay as they are meanwhile.
    /// </summary>
    public void Suspend()
    {
        _generation++;
        _index = null;
    }

    /// <summary>
    /// Shows version <paramref name="versionId"/>; with <paramref name="otherId"/> and <paramref name="statuses"/> as a
    /// comparison. Folders that were expanded before stay expanded when they exist.
    /// </summary>
    public async Task ShowAsync(VersionIndex index, long versionId, long? otherId,
        IReadOnlyDictionary<long, DiffStatus>? statuses, bool changedOnly)
    {
        var generation = ++_generation;
        _index = null;   // until the new tree is in, nothing is loaded with the old ids
        var expanded = _expanded.ToHashSet();
        var roots = await Task.Run(() => LoadTree(index, versionId, otherId, statuses, expanded));
        if (generation != _generation)
            return;

        _index = index;
        _versionId = versionId;
        _otherId = otherId;
        _statuses = statuses;
        _changedOnly = changedOnly;
        _roots = roots.Count == 0 ? [new VersionTreeNode("This version holds no files.", 0)] : roots;
        Rebuild();
        if (SelectedNode is null && _lastSelectedPathId is { } selectedId)
            SelectedNode = Rows.FirstOrDefault(r => !r.IsMessage && r.PathId == selectedId);
    }

    partial void OnSelectedNodeChanged(VersionTreeNode? value)
    {
        if (value is { IsMessage: false })
            _lastSelectedPathId = value.PathId;
    }

    /// <summary>Hides or shows Unchanged rows while comparing.</summary>
    public void SetChangedOnly(bool changedOnly)
    {
        _changedOnly = changedOnly;
        Rebuild();
    }

    /// <summary>Expands the folders above <paramref name="path"/> (loading them as needed) and selects its row.</summary>
    public async Task RevealAsync(string path, bool isDirectory)
    {
        if (_index is not { } index)
            return;
        var generation = _generation;
        var parts = path.Split('/');
        var nodes = _roots;
        VersionTreeNode? found = null;
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            var node = nodes.FirstOrDefault(n => !n.IsMessage && n.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase) &&
                                                 (last ? n.IsDirectory == isDirectory : n.IsDirectory));
            if (node is null)
                break;
            found = node;
            if (last)
                break;
            if (node.Children is null)
            {
                var (versionId, otherId, statuses) = (_versionId, _otherId, _statuses);
                var children = await Task.Run(() => Load(index, versionId, otherId, statuses, node.PathId, node.Depth + 1));
                if (generation != _generation)
                    return;
                node.Children = children;
            }
            _expanded.Add(node.PathId);
            nodes = node.Children;
        }

        if (found is not null && _changedOnly && found.Status == DiffStatus.Unchanged)
            _changedOnly = false;   // a revealed entry must be visible
        Rebuild();
        if (found is not null)
            SelectedNode = found;
    }

    internal async void OnExpandedChanged(VersionTreeNode node, bool expanded)
    {
        if (!expanded)
        {
            _expanded.Remove(node.PathId);
            Rebuild();
            return;
        }

        _expanded.Add(node.PathId);
        if (node.Children is not null || _index is not { } index)
        {
            Rebuild();   // without an index (suspended) the folder shows "Loading…" until the next tree loads it
            return;
        }

        var generation = _generation;
        var (versionId, otherId, statuses) = (_versionId, _otherId, _statuses);
        Rebuild();   // shows "Loading…" below the folder
        try
        {
            var children = await Task.Run(() => Load(index, versionId, otherId, statuses, node.PathId, node.Depth + 1));
            if (generation != _generation)
                return;
            node.Children = children;
        }
        catch (Exception ex)
        {
            if (generation != _generation)
                return;
            node.Children = [new VersionTreeNode($"Cannot be read: {ex.Message}", node.Depth + 1)];
        }
        Rebuild();
    }

    /// <summary>Loads the root level and, recursively, every folder that was expanded before.</summary>
    private List<VersionTreeNode> LoadTree(VersionIndex index, long versionId, long? otherId,
        IReadOnlyDictionary<long, DiffStatus>? statuses, HashSet<long> expanded)
    {
        if (index.FindPath("", isDirectory: true) is not { } rootId)
            return [];
        var roots = Load(index, versionId, otherId, statuses, rootId, depth: 0);
        var pending = new Stack<VersionTreeNode>(roots.Where(n => n.IsDirectory && expanded.Contains(n.PathId)));
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            node.Children = Load(index, versionId, otherId, statuses, node.PathId, node.Depth + 1);
            foreach (var child in node.Children.Where(n => n.IsDirectory && expanded.Contains(n.PathId)))
                pending.Push(child);
        }
        return roots;
    }

    private List<VersionTreeNode> Load(VersionIndex index, long versionId, long? otherId,
        IReadOnlyDictionary<long, DiffStatus>? statuses, long folderId, int depth) =>
        index.Children(versionId, folderId, otherId)
            .Select(child => new VersionTreeNode(this, child, depth,
                statuses is null ? null : statuses.GetValueOrDefault(child.PathId, DiffStatus.Unchanged)))
            .ToList();

    /// <summary>
    /// Lists the visible rows. The selection stays on its row, or moves to the row of the same entry when the tree was
    /// reloaded (another version, a comparison, new ids).
    /// </summary>
    private void Rebuild()
    {
        var selected = SelectedNode;
        var rows = new List<VersionTreeNode>();
        Append(rows, _roots);
        Rows.ReplaceAll(rows);
        SelectedNode = selected is null ? null
            : rows.Contains(selected) ? selected
            : selected.IsMessage ? null
            : rows.FirstOrDefault(r => !r.IsMessage && r.PathId == selected.PathId);
    }

    private void Append(List<VersionTreeNode> rows, List<VersionTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (_changedOnly && _statuses is not null && node.Status == DiffStatus.Unchanged)
                continue;
            var expanded = node.IsDirectory && _expanded.Contains(node.PathId);
            node.SyncExpanded(expanded);
            rows.Add(node);
            if (!expanded)
                continue;
            if (node.Children is null)
                rows.Add(new VersionTreeNode("Loading…", node.Depth + 1));
            else if (node.Children.Count == 0)
                rows.Add(new VersionTreeNode("Empty folder", node.Depth + 1));
            else
                Append(rows, node.Children);
        }
    }
}
