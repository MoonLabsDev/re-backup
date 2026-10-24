using System.IO.Hashing;
using System.Text;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;

namespace ReBackup.Core.Ignore;

/// <summary>
/// Proposes moving ignore lines between the plan and a nested <c>.backupignore</c> and proves, over a scanned source,
/// whether the backup scope stays identical. Pure: nothing is written.
/// </summary>
public static class IgnoreRelocation
{
    private const int MaxExamples = 20;

    /// <summary>Moves every line of <c>directory/.backupignore</c> to the end of the plan patterns.</summary>
    public static RelocationProposal ProposeIntegrate(RelocationContext context, string directory)
    {
        var dir = IgnorePattern.NormalizeDirectory(directory);
        var before = context.Settings.Patterns;
        var content = ReadFile(context, dir);
        var moved = new List<string>();
        var lines = SplitLines(content);
        if (lines.Count > 0)
        {
            moved.Add("# from " + IgnoreOrigins.ForNestedFile(dir));
            moved.AddRange(lines.Select(l => IgnoreTranslation.ToPlanLine(l, dir)));
        }

        List<string> after = [.. before, .. moved];
        var nestedAfter = NestedFilesWith(context, dir, null);
        return new RelocationProposal(RelocationKind.Integrate, dir, [.. before], after, content, null, [],
            CheckEquivalence(context, after, nestedAfter), Hash(before), HashContent(content))
        {
            MovedLines = moved,
        };
    }

    /// <summary>Moves the selected plan lines into <c>directory/.backupignore</c>; lines that cannot move stay in the plan.</summary>
    /// <exception cref="InvalidOperationException">The plan does not honor nested ignore files, so the file would have no effect.</exception>
    public static RelocationProposal ProposeExtract(RelocationContext context, IReadOnlyList<int> planLineIndexes, string directory)
    {
        if (!context.Settings.HonorNestedFiles)
            throw new InvalidOperationException("The plan does not honor nested ignore files.");

        var dir = IgnorePattern.NormalizeDirectory(directory);
        var before = context.Settings.Patterns;
        var content = ReadFile(context, dir);

        var removed = new HashSet<int>();
        var movedLines = new List<string>();
        var nestedLines = new List<string>();
        var rejected = new List<RejectedLine>();
        foreach (var index in planLineIndexes.Distinct().Order())
        {
            if (index < 0 || index >= before.Count)
                throw new ArgumentOutOfRangeException(nameof(planLineIndexes), index, "No such plan line.");
            var (line, reason) = IgnoreTranslation.ToNestedLine(before[index], dir);
            if (line is null)
            {
                rejected.Add(new RejectedLine(before[index], reason!.Value));
                continue;
            }
            removed.Add(index);
            movedLines.Add(before[index]);
            nestedLines.Add(line);
        }

        var after = before.Where((_, i) => !removed.Contains(i)).ToList();
        var contentAfter = AppendFromPlan(content, nestedLines);
        var nestedAfter = NestedFilesWith(context, dir, SplitLines(contentAfter));
        return new RelocationProposal(RelocationKind.Extract, dir, [.. before], after, content, contentAfter, rejected,
            CheckEquivalence(context, after, nestedAfter), Hash(before), HashContent(content))
        {
            MovedLines = movedLines,
        };
    }

    /// <summary>Indexes of the plan lines that move into <paramref name="directory"/> as they are: no comments, blanks or refused lines.</summary>
    public static IReadOnlyList<int> ExtractableLines(IgnoreSettings settings, string directory)
    {
        var dir = IgnorePattern.NormalizeDirectory(directory);
        var result = new List<int>();
        for (var i = 0; i < settings.Patterns.Count; i++)
        {
            var line = settings.Patterns[i];
            if (line.Trim().Length == 0 || line[0] == '#')
                continue;
            if (IgnoreTranslation.ToNestedLine(line, dir).Line is not null)
                result.Add(i);
        }
        return result;
    }

    /// <summary>xxHash64 (hex) of plan lines joined with <c>\n</c>.</summary>
    public static string Hash(IEnumerable<string> lines) => HashText(string.Join('\n', lines));

    /// <summary>xxHash64 (hex) of file content with a UTF-8 BOM removed and CRLF read as LF; null for a missing file.</summary>
    public static string? HashContent(string? content)
    {
        if (content is null)
            return null;
        if (content.StartsWith('﻿'))
            content = content[1..];
        return HashText(content.Replace("\r\n", "\n"));
    }

    private static string HashText(string text) =>
        Convert.ToHexString(XxHash64.Hash(Encoding.UTF8.GetBytes(text)));

    private static string? ReadFile(RelocationContext context, string directory)
    {
        var path = Path.Combine(context.SourceRoot, directory, IgnoreOrigins.NestedFileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static List<string> SplitLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return [];
        if (content.StartsWith('﻿'))
            content = content[1..];
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string? AppendFromPlan(string? content, List<string> nestedLines)
    {
        if (nestedLines.Count == 0)
            return content;
        var newline = content is not null && content.Contains("\r\n") ? "\r\n" : "\n";
        var text = new StringBuilder(content ?? "");
        if (text.Length > 0 && text[^1] != '\n')
            text.Append(newline);
        text.Append("# from plan").Append(newline);
        foreach (var line in nestedLines)
            text.Append(line).Append(newline);
        return text.ToString();
    }

    /// <summary>The scanned nested files with the one in <paramref name="directory"/> replaced, or removed when <paramref name="lines"/> is null.</summary>
    private static List<NestedIgnoreFile> NestedFilesWith(RelocationContext context, string directory, IReadOnlyList<string>? lines)
    {
        var files = (context.Index?.IgnoreFiles ?? [])
            .Where(f => !string.Equals(IgnorePattern.NormalizeDirectory(f.DirectoryRelativePath), directory, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (lines is not null)
            files.Add(new NestedIgnoreFile(directory, lines));
        return files;
    }

    private static EquivalenceResult CheckEquivalence(RelocationContext context, IReadOnlyList<string> planAfter,
        List<NestedIgnoreFile> nestedAfter)
    {
        if (context.Index is not { } index)
            return new EquivalenceResult.NotChecked(EquivalenceResult.NotChecked.NoScan);

        var before = IgnoreMatcher.ForPlan(context.Settings, context.GlobalDefaults, index.IgnoreFiles);
        var settingsAfter = new IgnoreSettings
        {
            UseGlobalDefaults = context.Settings.UseGlobalDefaults,
            HonorNestedFiles = context.Settings.HonorNestedFiles,
            Patterns = [.. planAfter],
        };
        var after = IgnoreMatcher.ForPlan(settingsAfter, context.GlobalDefaults, nestedAfter);

        var count = 0;
        var examples = new List<EquivalenceExample>();
        foreach (var child in index.RootNode.Children)
            Compare(child, "", before, after, ref count, examples);
        return count == 0 ? new EquivalenceResult.Identical() : new EquivalenceResult.Differs(count, examples);
    }

    private static void Compare(IndexNode node, string parentPath, IgnoreMatcher before, IgnoreMatcher after,
        ref int count, List<EquivalenceExample> examples)
    {
        var path = parentPath.Length == 0 ? node.Name : parentPath + "/" + node.Name;
        var was = before.Match(path, node.IsDirectory);
        var now = after.Match(path, node.IsDirectory);
        if (was.IsIgnored != now.IsIgnored)
        {
            count++;
            if (examples.Count < MaxExamples)
                examples.Add(new EquivalenceExample(path, was.IsIgnored, now.IsIgnored, now.Pattern?.Text));
        }

        // Below a folder ignored both before and after, every entry is ignored in both.
        if (node.IsDirectory && !(was.IsIgnored && now.IsIgnored))
            foreach (var child in node.Children)
                Compare(child, path, before, after, ref count, examples);
    }
}
