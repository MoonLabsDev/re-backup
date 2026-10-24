namespace ReBackup.Core.Ignore;

/// <summary>Why a plan line cannot be moved into a nested ignore file.</summary>
public enum RejectReason
{
    /// <summary>The line is anchored somewhere other than below the folder.</summary>
    NotBelowFolder,

    /// <summary>The line is not anchored, so it also matches outside the folder.</summary>
    MatchesOutsideFolder,
}

public sealed record RejectedLine(string Line, RejectReason Reason);

/// <summary>
/// Translates single ignore lines between the plan patterns and a nested <c>.backupignore</c> in a folder.
/// The folder is relative to the source root, with forward slashes. The tables are syntactic; whether a move keeps
/// the backup identical is for the equivalence check to prove.
/// </summary>
public static class IgnoreTranslation
{
    private const string AnyDepth = "**/";

    /// <summary>One line of <c>directory/.backupignore</c> as the plan pattern with the same meaning.</summary>
    public static string ToPlanLine(string nestedLine, string directory)
    {
        var dir = IgnorePattern.NormalizeDirectory(directory);
        if (dir.Length == 0 || nestedLine.Trim().Length == 0 || nestedLine[0] == '#')
            return nestedLine;

        var negation = nestedLine[0] == '!' ? "!" : "";
        var glob = nestedLine[negation.Length..];
        var core = glob.TrimEnd('/');
        var prefix = "/" + IgnorePattern.EscapeLiteral(dir) + "/";

        if (core.StartsWith(AnyDepth, StringComparison.Ordinal))
            return negation + prefix + glob;
        if (core.Contains('/'))
            return negation + prefix + glob.TrimStart('/');
        return negation + prefix + AnyDepth + glob;
    }

    /// <summary>A plan line as the line for <c>directory/.backupignore</c>, or the reason it cannot be moved there.</summary>
    public static (string? Line, RejectReason? Reason) ToNestedLine(string planLine, string directory)
    {
        var dir = IgnorePattern.NormalizeDirectory(directory);
        if (dir.Length == 0 || planLine.Trim().Length == 0 || planLine[0] == '#')
            return (planLine, null);

        var negation = planLine[0] == '!' ? "!" : "";
        var glob = planLine[negation.Length..];
        var prefix = "/" + IgnorePattern.EscapeLiteral(dir) + "/";

        if (!glob.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var core = glob.TrimEnd('/');
            var matchesOutside = !core.Contains('/') || core.StartsWith(AnyDepth, StringComparison.Ordinal);
            return (null, matchesOutside ? RejectReason.MatchesOutsideFolder : RejectReason.NotBelowFolder);
        }

        var rest = glob[prefix.Length..];
        if (rest.TrimEnd('/').Length == 0)
            return (null, RejectReason.NotBelowFolder);

        if (!rest.StartsWith(AnyDepth, StringComparison.Ordinal))
            return (negation + "/" + rest, null);

        var deeper = rest[AnyDepth.Length..];
        return (negation + (deeper.TrimEnd('/').Contains('/') ? rest : deeper), null);
    }
}
