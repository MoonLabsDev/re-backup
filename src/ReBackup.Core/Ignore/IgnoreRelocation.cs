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
    private const char Bom = '\uFEFF';

    /// <summary>Moves every line of <c>directory/.backupignore</c> to the end of the plan patterns.</summary>
    public static RelocationProposal ProposeIntegrate(RelocationContext context, string directory)
    {
        var dir = CheckedDirectory(context.SourceRoot, directory);
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
            CheckEquivalence(context, dir, content, after, nestedAfter), Hash(before), HashContent(content))
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

        var dir = CheckedDirectory(context.SourceRoot, directory);
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
            CheckEquivalence(context, dir, content, after, nestedAfter), Hash(before), HashContent(content))
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

    /// <summary>
    /// Writes a proposal: re-checks that the plan and the file are as proposed, then writes the file (extract), saves the plan
    /// and recycles the file (integrate), undoing the earlier steps when a later one fails.
    /// </summary>
    /// <exception cref="RelocationStaleException">The plan or the file changed since the proposal.</exception>
    /// <exception cref="InvalidOperationException">The proposal moves nothing.</exception>
    public static RecycleOutcome? Apply(RelocationProposal proposal, string sourceRoot, IReadOnlyList<string> currentPlanPatterns,
        Action<IReadOnlyList<string>> savePlanPatterns, IRecycleBin recycleBin)
    {
        if (proposal.MovedLines.Count == 0)
            throw new InvalidOperationException("The proposal moves no lines.");
        if (proposal.Kind == RelocationKind.Extract && proposal.FileContentAfter is null)
            throw new InvalidOperationException("An extract proposal has no file content.");

        var path = Path.Combine(sourceRoot, CheckedDirectory(sourceRoot, proposal.Directory), IgnoreOrigins.NestedFileName);
        var current = File.Exists(path) ? File.ReadAllText(path) : null;
        if (Hash(currentPlanPatterns) != proposal.PlanPatternsHash || HashContent(current) != proposal.FileHash)
            throw new RelocationStaleException();

        if (proposal.Kind == RelocationKind.Integrate)
        {
            savePlanPatterns(proposal.PlanPatternsAfter);
            try
            {
                return recycleBin.Recycle(path);
            }
            catch
            {
                RestorePlan(savePlanPatterns, proposal.PlanPatternsBefore);
                throw;
            }
        }

        WriteAtomically(path, proposal.FileContentAfter!);
        try
        {
            savePlanPatterns(proposal.PlanPatternsAfter);
        }
        catch
        {
            RestoreFile(path, proposal.FileContentBefore);
            throw;
        }
        return null;
    }

    /// <summary>
    /// The path an integrated file is renamed to when it cannot go to the recycle bin: <c>.backupignore.integrated</c> in
    /// <paramref name="directoryPath"/>, or <c>.integrated-2</c>, <c>-3</c>, … when that name is taken.
    /// </summary>
    public static string IntegratedFileName(string directoryPath)
    {
        var baseName = IgnoreOrigins.NestedFileName + ".integrated";
        var candidate = Path.Combine(directoryPath, baseName);
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = Path.Combine(directoryPath, baseName + "-" + n);
        return candidate;
    }

    private static void RestorePlan(Action<IReadOnlyList<string>> savePlanPatterns, IReadOnlyList<string> before)
    {
        try { savePlanPatterns(before); }
        catch { /* the original failure is the one to report */ }
    }

    private static void RestoreFile(string path, string? before)
    {
        try
        {
            if (before is null)
                File.Delete(path);
            else
                WriteAtomically(path, before);
        }
        catch { /* the original failure is the one to report */ }
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch { /* must not replace the original failure */ }
        }
    }

    /// <summary>xxHash64 (hex) of plan lines joined with <c>\n</c>.</summary>
    public static string Hash(IEnumerable<string> lines) => HashText(string.Join('\n', lines));

    /// <summary>xxHash64 (hex) of file content with a UTF-8 BOM removed and CRLF read as LF; null for a missing file.</summary>
    public static string? HashContent(string? content)
    {
        if (content is null)
            return null;
        return HashText(StripBom(content).Replace("\r\n", "\n"));
    }

    private static string StripBom(string content) => content.StartsWith(Bom) ? content[1..] : content;

    private static string HashText(string text) =>
        Convert.ToHexString(XxHash64.Hash(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// <paramref name="directory"/> normalized, after checking that it stays below <paramref name="sourceRoot"/>
    /// (no <c>..</c> that leaves it, no rooted path).
    /// </summary>
    /// <exception cref="ArgumentException">The directory leaves the source root.</exception>
    private static string CheckedDirectory(string sourceRoot, string directory)
    {
        var dir = IgnorePattern.NormalizeDirectory(directory);
        if (Path.IsPathRooted(directory))
            throw new ArgumentException("The directory must be relative to the source root.", nameof(directory));
        var root = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, dir));
        if (dir.Length > 0 && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The directory leaves the source root.", nameof(directory));
        return dir;
    }

    private static string? ReadFile(RelocationContext context, string directory)
    {
        var path = Path.Combine(context.SourceRoot, directory, IgnoreOrigins.NestedFileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static List<string> SplitLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return [];
        var lines = StripBom(content).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
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

    /// <summary>
    /// Compares the scope before and after. The file in <paramref name="directory"/> is taken from <paramref name="fileContent"/>
    /// (what is on disk now) on both sides, so an edit made after the scan does not show up as a difference.
    /// </summary>
    private static EquivalenceResult CheckEquivalence(RelocationContext context, string directory, string? fileContent,
        IReadOnlyList<string> planAfter, List<NestedIgnoreFile> nestedAfter)
    {
        if (context.Index is not { } index)
            return new EquivalenceResult.NotChecked(EquivalenceResult.NotChecked.NoScan);

        var origin = IgnoreOrigins.ForNestedFile(directory);
        if (index.UnreadableIgnoreFiles.Any(f => string.Equals(f, origin, StringComparison.OrdinalIgnoreCase)))
            return new EquivalenceResult.NotChecked(EquivalenceResult.NotChecked.UnreadableFile);

        var nestedBefore = NestedFilesWith(context, directory, fileContent is null ? null : SplitLines(fileContent));
        var before = IgnoreMatcher.ForPlan(context.Settings, context.GlobalDefaults, nestedBefore);
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
