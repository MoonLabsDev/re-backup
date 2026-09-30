using ReBackup.Core.Plans;

namespace ReBackup.Core.Ignore;

/// <summary>The lines of one <c>.backupignore</c> file and the folder (relative to the source root) it lives in.</summary>
public sealed record NestedIgnoreFile(string DirectoryRelativePath, IReadOnlyList<string> Lines);

/// <summary><see cref="Pattern"/> is the pattern that decided, or null when no pattern matched.</summary>
public readonly record struct IgnoreResult(bool IsIgnored, IgnorePattern? Pattern)
{
    public static readonly IgnoreResult NotMatched = new(false, null);
}

public static class IgnoreOrigins
{
    public const string GlobalDefaults = "Global defaults";
    public const string Plan = "Plan";
    public const string NestedFileName = ".backupignore";

    public static string ForNestedFile(string directoryRelativePath) =>
        directoryRelativePath.Length == 0 ? NestedFileName : directoryRelativePath + "/" + NestedFileName;
}

public sealed class IgnoreMatcher
{
    private readonly IgnorePattern[] _patterns;   // lowest precedence first

    public IgnoreMatcher(IEnumerable<IgnorePattern> patternsLowestPrecedenceFirst) =>
        _patterns = [.. patternsLowestPrecedenceFirst];

    public IReadOnlyList<IgnorePattern> Patterns => _patterns;

    public static IgnoreMatcher Create(IEnumerable<string> globalDefaults, IEnumerable<string> planPatterns,
        IEnumerable<NestedIgnoreFile> nestedFiles)
    {
        var patterns = new List<IgnorePattern>();
        AddLines(patterns, globalDefaults, IgnoreOrigins.GlobalDefaults, "");
        AddLines(patterns, planPatterns, IgnoreOrigins.Plan, "");

        // Ancestors before descendants, so that deeper files take precedence.
        var ordered = nestedFiles
            .Select(f => (Directory: IgnorePattern.NormalizeDirectory(f.DirectoryRelativePath), f.Lines))
            .OrderBy(f => Depth(f.Directory))
            .ThenBy(f => f.Directory, StringComparer.OrdinalIgnoreCase);
        foreach (var (directory, lines) in ordered)
            AddLines(patterns, lines, IgnoreOrigins.ForNestedFile(directory), directory);

        return new IgnoreMatcher(patterns);
    }

    public static IgnoreMatcher ForPlan(IgnoreSettings settings, IEnumerable<string> globalDefaults,
        IEnumerable<NestedIgnoreFile> nestedFiles) =>
        Create(
            settings.UseGlobalDefaults ? globalDefaults : [],
            settings.Patterns,
            settings.HonorNestedFiles ? nestedFiles : []);

    /// <summary>Decision for this entry alone: the last matching pattern wins. Parent folders are not considered.</summary>
    public IgnoreResult MatchEntry(string relativePath, bool isDirectory)
    {
        for (var i = _patterns.Length - 1; i >= 0; i--)
        {
            var pattern = _patterns[i];
            if (pattern.IsMatch(relativePath, isDirectory))
                return new IgnoreResult(!pattern.IsNegated, pattern);
        }
        return IgnoreResult.NotMatched;
    }

    /// <summary>Full decision: an entry below an ignored folder is ignored and cannot be re-included.</summary>
    public IgnoreResult Match(string relativePath, bool isDirectory)
    {
        var slash = relativePath.IndexOf('/');
        while (slash >= 0)
        {
            var parent = MatchEntry(relativePath[..slash], isDirectory: true);
            if (parent.IsIgnored)
                return parent;
            slash = relativePath.IndexOf('/', slash + 1);
        }
        return MatchEntry(relativePath, isDirectory);
    }

    private static void AddLines(List<IgnorePattern> patterns, IEnumerable<string> lines, string origin, string baseDirectory)
    {
        foreach (var line in lines)
        {
            if (IgnorePattern.Parse(line, origin, baseDirectory) is { } pattern)
                patterns.Add(pattern);
        }
    }

    private static int Depth(string directory) =>
        directory.Length == 0 ? 0 : directory.Count(c => c == '/') + 1;
}
