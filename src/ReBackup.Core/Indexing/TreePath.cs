using System.Collections.Concurrent;

namespace ReBackup.Core.Indexing;

/// <summary>An entry of a scanned tree that knows only its own name and its parent folder (null for the root).</summary>
public interface ITreePathNode<out T> where T : class, ITreePathNode<T>
{
    T? Parent { get; }
    string Name { get; }
}

/// <summary>
/// Paths of scanned entries. A scan of millions of entries does not keep a path per entry: each entry holds its name
/// and its parent, and the path is put together when it is asked for.
/// </summary>
public static class TreePath
{
    /// <summary>The path relative to the root, with forward slashes; "" for the root (whose name is a display name).</summary>
    public static string Of<T>(T node) where T : class, ITreePathNode<T>
    {
        if (node.Parent is null)
            return "";
        if (node.Parent.Parent is null)
            return node.Name;

        var length = node.Name.Length;
        for (var folder = node.Parent; folder.Parent is not null; folder = folder.Parent)
            length += folder.Name.Length + 1;
        return string.Create(length, node, static (span, last) =>
        {
            var end = span.Length;
            for (var entry = last; entry.Parent is not null; entry = entry.Parent)
            {
                end -= entry.Name.Length;
                entry.Name.CopyTo(span[end..]);
                if (end > 0)
                    span[--end] = '/';
            }
        });
    }
}

/// <summary>
/// The names of one scan, each kept once: the many entries that share a name (index.js, package.json, …) share one
/// string. Safe to use from several threads.
/// </summary>
internal sealed class NamePool
{
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);

    public string Get(string name) => _names.GetOrAdd(name, name);
}
