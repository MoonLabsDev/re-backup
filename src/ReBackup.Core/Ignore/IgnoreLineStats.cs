using ReBackup.Core.Indexing;

namespace ReBackup.Core.Ignore;

/// <summary>What one pattern line decides in the evaluated tree. <paramref name="LineNumber"/> is 1-based.</summary>
public readonly record struct LineHits(int LineNumber, string Text, int Entries, long Bytes);

public static class IgnoreLineStats
{
    /// <summary>
    /// For every pattern line (no blank lines, no comments) of the ignore file with the given origin: how many entries it
    /// decides and their total size. Entries ignored only because a parent folder is ignored count for the parent's line.
    /// Re-included entries count for the negated line. Zero hits are included.
    /// </summary>
    /// <remarks>
    /// Equal lines are the same pattern and the last one wins, so a repeated line's hits go to its last occurrence.
    /// </remarks>
    public static IReadOnlyList<LineHits> ForOrigin(EvaluatedNode root, string origin, IReadOnlyList<string> lines)
    {
        var byText = new Dictionary<string, (int Entries, long Bytes)>(StringComparer.Ordinal);
        Collect(root, origin, byText);

        var parsed = new List<(int LineNumber, string Text)>();
        var lastLineOfText = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i] is null || IgnorePattern.Parse(lines[i], origin) is not { } pattern)
                continue;
            parsed.Add((i + 1, pattern.Text));
            lastLineOfText[pattern.Text] = i + 1;
        }

        var result = new List<LineHits>(parsed.Count);
        foreach (var (lineNumber, text) in parsed)
        {
            var hits = lastLineOfText[text] == lineNumber && byText.TryGetValue(text, out var found) ? found : default;
            result.Add(new LineHits(lineNumber, text, hits.Entries, hits.Bytes));
        }
        return result;
    }

    private static void Collect(EvaluatedNode node, string origin, Dictionary<string, (int Entries, long Bytes)> byText)
    {
        if (node.IgnoredByParent)
            return;

        if (node.Pattern is { } pattern && string.Equals(pattern.Origin, origin, StringComparison.Ordinal))
        {
            byText.TryGetValue(pattern.Text, out var sum);
            byText[pattern.Text] = (sum.Entries + 1, sum.Bytes + node.TotalSize);
        }

        // Everything below an ignored folder is ignored by it; a re-included or plain folder may still hold decided entries.
        if (node.Status == IncludeStatus.Ignored)
            return;
        foreach (var child in node.Children)
            Collect(child, origin, byText);
    }
}
