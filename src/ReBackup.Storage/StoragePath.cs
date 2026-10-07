namespace ReBackup.Storage;

/// <summary>Helpers for storage paths: relative to the root, <c>/</c>-separated, no leading <c>/</c>, <c>""</c> is the root.</summary>
public static class StoragePath
{
    /// <summary>Joins the parts with <c>/</c>, skipping empty ones.</summary>
    public static string Combine(params string[] parts) =>
        string.Join('/', parts.Where(part => part.Length > 0));

    /// <summary>The folder part of a path; <c>""</c> for a top-level entry.</summary>
    public static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    /// <summary>The last segment of a path.</summary>
    public static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>Returns the path when valid (<c>""</c> is the root); throws <see cref="ArgumentException"/> on <c>\</c>, a leading or trailing <c>/</c>, empty, <c>.</c> or <c>..</c> segments.</summary>
    public static string Validate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0) return path;
        if (path.Contains('\\')) throw new ArgumentException($"A storage path must not contain '\\': '{path}'.", nameof(path));
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0) throw new ArgumentException($"A storage path must not start or end with '/' or contain '//': '{path}'.", nameof(path));
            if (segment is "." or "..") throw new ArgumentException($"A storage path must not contain '.' or '..' segments: '{path}'.", nameof(path));
        }
        return path;
    }
}
