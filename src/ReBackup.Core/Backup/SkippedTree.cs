using ReBackup.Core.Localization;

namespace ReBackup.Core.Backup;

/// <summary>Why an entry was skipped, for colouring: gone from the source, changed while copied, or anything else.</summary>
public enum SkipKind { Deleted, Changed, Other }

/// <summary>
/// A folder or an entry of the tree of a run's skipped entries. <see cref="Name"/> can be several folder levels
/// ("a/b/c") where a folder holds just one folder. Folders count the skipped entries below them by kind.
/// </summary>
public sealed class SkippedNode
{
    internal SkippedNode(string name, string path)
    {
        Name = name;
        Path = path;
    }

    /// <summary>The name shown: one level, or several joined by "/" for a chain of single folders.</summary>
    public string Name { get; internal set; }

    /// <summary>The full relative path, with forward slashes.</summary>
    public string Path { get; internal set; }

    public bool IsDirectory { get; internal set; }

    /// <summary>The reason the entry itself was skipped (as stored, in English); null for a folder that only holds some.</summary>
    public string? Reason { get; internal set; }

    public SkipKind? Kind => Reason is null ? null : SkippedTree.KindOf(Reason);

    /// <summary>Folders first, then by name without regard to case.</summary>
    public List<SkippedNode> Children { get; } = [];

    /// <summary>The skipped entries of the kind in this node and below it.</summary>
    public int Deleted { get; internal set; }

    public int Changed { get; internal set; }

    public int Other { get; internal set; }
}

/// <summary>Turns the flat list of a run's skipped entries into a tree of folders.</summary>
public static class SkippedTree
{
    private static readonly string NoLongerExists = CoreTexts.English("core.file.noLongerExists");
    private static readonly string ChangedWhileCopied = CoreTexts.English("core.skip.changed");

    /// <summary>Reasons that are only ever given for folders.</summary>
    private static readonly HashSet<string> FolderReasons =
        [CoreTexts.English("core.scan.link"), CoreTexts.English("core.scan.tooDeep")];

    public static SkipKind KindOf(string reason) =>
        reason == NoLongerExists ? SkipKind.Deleted
        : reason == ChangedWhileCopied ? SkipKind.Changed
        : SkipKind.Other;

    /// <summary>The top level of the tree. Of entries with the same path (by case or slash style), the first wins.</summary>
    public static IReadOnlyList<SkippedNode> Build(IEnumerable<SkippedEntry> entries)
    {
        var root = new SkippedNode("", "") { IsDirectory = true };
        var byPath = new Dictionary<string, SkippedNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        foreach (var entry in entries)
        {
            var path = entry.Path.Replace('\\', '/').Trim('/');
            if (path.Length == 0)
                continue;
            var node = Ensure(path, byPath);
            if (node.Reason is not null)
                continue;
            node.Reason = entry.Reason;
            if (FolderReasons.Contains(entry.Reason))
                node.IsDirectory = true;
        }

        Finish(root);
        return root.Children;
    }

    private static SkippedNode Ensure(string path, Dictionary<string, SkippedNode> byPath)
    {
        if (byPath.TryGetValue(path, out var node))
            return node;
        var slash = path.LastIndexOf('/');
        var parent = Ensure(slash < 0 ? "" : path[..slash], byPath);
        parent.IsDirectory = true;
        node = new SkippedNode(path[(slash + 1)..], path);
        parent.Children.Add(node);
        byPath[path] = node;
        return node;
    }

    /// <summary>Counts, sorts and compacts the children of <paramref name="folder"/>, depth first.</summary>
    private static void Finish(SkippedNode folder)
    {
        for (var i = 0; i < folder.Children.Count; i++)
        {
            var child = folder.Children[i];
            Finish(child);
            // A folder that only holds one folder (and was not skipped itself) is shown as one row: "a/b".
            while (child is { IsDirectory: true, Reason: null, Children: [{ IsDirectory: true } only] })
            {
                only.Name = child.Name + "/" + only.Name;
                folder.Children[i] = child = only;
            }
            folder.Deleted += child.Deleted;
            folder.Changed += child.Changed;
            folder.Other += child.Other;
        }

        switch (folder.Kind)
        {
            case SkipKind.Deleted: folder.Deleted++; break;
            case SkipKind.Changed: folder.Changed++; break;
            case SkipKind.Other: folder.Other++; break;
        }

        folder.Children.Sort((a, b) => a.IsDirectory != b.IsDirectory
            ? (a.IsDirectory ? -1 : 1)
            : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name) is var byName and not 0
                ? byName
                : StringComparer.Ordinal.Compare(a.Name, b.Name));
    }
}
