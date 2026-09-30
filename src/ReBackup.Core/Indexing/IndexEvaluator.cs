using ReBackup.Core.Ignore;

namespace ReBackup.Core.Indexing;

public enum IncludeStatus
{
    /// <summary>The entry and everything below it is backed up.</summary>
    Included,
    /// <summary>The entry and everything below it is skipped.</summary>
    Ignored,
    /// <summary>A folder that is backed up, but something below it is skipped.</summary>
    Partial,
}

public sealed class EvaluatedNode : IPreviewEntry
{
    public required IndexNode Node { get; init; }
    public required IncludeStatus Status { get; init; }

    /// <summary>The pattern that decided: for ignored entries the excluding pattern (of the entry or of its ignored parent), for re-included entries the negated pattern.</summary>
    public IgnorePattern? Pattern { get; init; }

    /// <summary>True when the entry is ignored only because a parent folder is ignored.</summary>
    public bool IgnoredByParent { get; init; }

    public long IncludedSize { get; init; }
    public long IgnoredSize { get; init; }
    public int IncludedFiles { get; init; }
    public int IgnoredFiles { get; init; }
    public IReadOnlyList<EvaluatedNode> Children { get; init; } = [];

    public long TotalSize => IncludedSize + IgnoredSize;
    public int TotalFiles => IncludedFiles + IgnoredFiles;

    public string Name => Node.Name;
    public string RelativePath => Node.RelativePath;
    public bool IsDirectory => Node.IsDirectory;
    public string? Error => Node.Error;

    /// <summary>An evaluation is always of a finished index.</summary>
    public ScanState State => ScanState.Done;

    public IReadOnlyList<IPreviewEntry> GetChildren() => Children;
}

public static class IndexEvaluator
{
    /// <summary>Applies the matcher to a cached index. Nothing is read from disk.</summary>
    public static EvaluatedNode Evaluate(SourceIndex index, IgnoreMatcher matcher,
        CancellationToken cancellationToken = default) =>
        Evaluate(index.RootNode, matcher, isRoot: true, parentIgnored: false, parentPattern: null, cancellationToken);

    private static EvaluatedNode Evaluate(IndexNode node, IgnoreMatcher matcher, bool isRoot, bool parentIgnored,
        IgnorePattern? parentPattern, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ignored = parentIgnored;
        var pattern = parentPattern;
        if (!parentIgnored && !isRoot)
        {
            var result = matcher.MatchEntry(node.RelativePath, node.IsDirectory);
            ignored = result.IsIgnored;
            pattern = result.Pattern;
        }

        if (!node.IsDirectory)
        {
            return new EvaluatedNode
            {
                Node = node,
                Status = ignored ? IncludeStatus.Ignored : IncludeStatus.Included,
                Pattern = pattern,
                IgnoredByParent = parentIgnored,
                IncludedSize = ignored ? 0 : node.Size,
                IgnoredSize = ignored ? node.Size : 0,
                IncludedFiles = ignored ? 0 : 1,
                IgnoredFiles = ignored ? 1 : 0,
            };
        }

        var children = new List<EvaluatedNode>(node.Children.Count);
        long includedSize = 0, ignoredSize = 0;
        int includedFiles = 0, ignoredFiles = 0;
        var anythingSkipped = false;
        foreach (var child in node.Children)
        {
            var evaluated = Evaluate(child, matcher, isRoot: false, ignored, ignored ? pattern : null, cancellationToken);
            children.Add(evaluated);
            includedSize += evaluated.IncludedSize;
            ignoredSize += evaluated.IgnoredSize;
            includedFiles += evaluated.IncludedFiles;
            ignoredFiles += evaluated.IgnoredFiles;
            anythingSkipped |= evaluated.Status != IncludeStatus.Included;
        }

        return new EvaluatedNode
        {
            Node = node,
            Status = ignored ? IncludeStatus.Ignored : anythingSkipped ? IncludeStatus.Partial : IncludeStatus.Included,
            Pattern = pattern,
            IgnoredByParent = parentIgnored,
            IncludedSize = includedSize,
            IgnoredSize = ignoredSize,
            IncludedFiles = includedFiles,
            IgnoredFiles = ignoredFiles,
            Children = children,
        };
    }
}
