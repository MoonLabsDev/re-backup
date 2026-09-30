using System.Text;
using System.Text.RegularExpressions;

namespace ReBackup.Core.Ignore;

/// <summary>One parsed gitignore-style line. Paths are relative to the source root and use forward slashes.</summary>
public sealed class IgnorePattern
{
    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly char[] GlobChars = ['*', '?', '[', '\\'];

    private readonly string _basePrefix;
    private readonly string? _literalName;
    private readonly string? _nameSuffix;
    private readonly Regex? _regex;

    private IgnorePattern(string text, string origin, string baseDirectory, bool negated, bool directoryOnly,
        bool anchored, string glob)
    {
        Text = text;
        Origin = origin;
        BaseDirectory = baseDirectory;
        IsNegated = negated;
        IsDirectoryOnly = directoryOnly;
        IsAnchored = anchored;
        _basePrefix = baseDirectory.Length == 0 ? "" : baseDirectory + "/";

        // Fast paths for the two most common shapes: "name" and "*.ext".
        if (!anchored && glob.IndexOfAny(GlobChars) < 0)
        {
            _literalName = glob;
        }
        else if (!anchored && glob.Length > 1 && glob[0] == '*' && glob.IndexOfAny(GlobChars, 1) < 0)
        {
            _nameSuffix = glob[1..];
        }
        else
        {
            var prefix = anchored ? "^" : "^(?:.*/)?";
            try
            {
                _regex = new Regex(prefix + GlobToRegex(glob) + "$", MatchOptions);
            }
            catch (ArgumentException)
            {
                // e.g. a reversed range like [z-a]: treat the whole pattern as literal text.
                _regex = new Regex(prefix + Regex.Escape(glob) + "$", MatchOptions);
            }
        }
    }

    /// <summary>The line as written, without trailing whitespace.</summary>
    public string Text { get; }

    /// <summary>Where the pattern comes from: "Global defaults", "Plan" or the path of a nested ignore file.</summary>
    public string Origin { get; }

    /// <summary>Folder the pattern is relative to ("" for the source root).</summary>
    public string BaseDirectory { get; }

    public bool IsNegated { get; }
    public bool IsDirectoryOnly { get; }
    public bool IsAnchored { get; }

    public static IgnorePattern? Parse(string line, string origin, string baseDirectory = "")
    {
        var text = TrimTrailingSpaces(line.TrimEnd('\r', '\n'));
        if (text.Length == 0 || text[0] == '#')
            return null;

        var glob = text;
        var negated = glob[0] == '!';
        if (negated)
            glob = glob[1..];

        var directoryOnly = glob.EndsWith('/');
        glob = glob.TrimEnd('/');
        if (glob.Length == 0)
            return null;

        var anchored = glob.Contains('/');
        glob = glob.TrimStart('/');
        if (glob.Length == 0)
            return null;

        return new IgnorePattern(text, origin, NormalizeDirectory(baseDirectory), negated, directoryOnly, anchored, glob);
    }

    public bool IsMatch(string relativePath, bool isDirectory)
    {
        if (IsDirectoryOnly && !isDirectory)
            return false;

        ReadOnlySpan<char> local = relativePath;
        if (_basePrefix.Length > 0)
        {
            if (!relativePath.StartsWith(_basePrefix, StringComparison.OrdinalIgnoreCase))
                return false;
            local = local[_basePrefix.Length..];
        }

        if (_literalName is not null)
            return NameOf(local).Equals(_literalName, StringComparison.OrdinalIgnoreCase);
        if (_nameSuffix is not null)
            return NameOf(local).EndsWith(_nameSuffix, StringComparison.OrdinalIgnoreCase);
        return _regex!.IsMatch(local);
    }

    /// <summary>Escapes glob characters so that the text only matches itself when used in a pattern.</summary>
    public static string EscapeLiteral(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '?' or '[' or ']')
                sb.Append('\\');
            sb.Append(c);
        }
        if (sb.Length > 0 && sb[^1] == ' ')
            sb.Insert(sb.Length - 1, '\\');
        return sb.ToString();
    }

    internal static string NormalizeDirectory(string directory) =>
        directory.Replace('\\', '/').Trim('/');

    private static ReadOnlySpan<char> NameOf(ReadOnlySpan<char> path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string TrimTrailingSpaces(string s)
    {
        var end = s.Length;
        while (end > 0 && s[end - 1] == ' ' && (end < 2 || s[end - 2] != '\\'))
            end--;
        return s[..end];
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < glob.Length)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    var atSegmentStart = i == 0 || glob[i - 1] == '/';
                    var after = i + 2;
                    if (atSegmentStart && after < glob.Length && glob[after] == '/')
                    {
                        sb.Append("(?:.*/)?");   // "**/" : zero or more folders
                        i = after + 1;
                        continue;
                    }
                    if (atSegmentStart && after == glob.Length)
                    {
                        sb.Append(".*");         // trailing "/**" : everything inside
                        i = after;
                        continue;
                    }
                    while (i < glob.Length && glob[i] == '*')
                        i++;
                    sb.Append("[^/]*");          // other runs of stars behave like one star
                    continue;
                }
                sb.Append("[^/]*");
                i++;
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else if (c == '[')
            {
                var next = AppendCharacterClass(glob, i, sb);
                if (next < 0)
                {
                    sb.Append("\\[");
                    i++;
                }
                else
                {
                    i = next;
                }
            }
            else if (c == '\\' && i + 1 < glob.Length)
            {
                sb.Append(Regex.Escape(glob[i + 1].ToString()));
                i += 2;
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Appends the regex for the class starting at <paramref name="start"/>; returns the index after it, or -1 if unterminated.</summary>
    private static int AppendCharacterClass(string glob, int start, StringBuilder sb)
    {
        var i = start + 1;
        var negate = i < glob.Length && (glob[i] == '!' || glob[i] == '^');
        if (negate)
            i++;
        var contentStart = i;
        if (i < glob.Length && glob[i] == ']')
            i++;   // a leading ] is a literal
        while (i < glob.Length && glob[i] != ']')
            i++;
        if (i >= glob.Length)
            return -1;

        if (negate)
            sb.Append("(?!/)");   // a class never matches a slash
        sb.Append('[');
        if (negate)
            sb.Append('^');
        foreach (var ch in glob.AsSpan(contentStart, i - contentStart))
        {
            if (ch is '\\' or '[' or ']' or '^')
                sb.Append('\\');
            sb.Append(ch);
        }
        sb.Append(']');
        return i + 1;
    }
}
