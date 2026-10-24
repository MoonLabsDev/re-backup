namespace ReBackup.Core.Versions;

/// <summary>
/// What changed under one key (a folder or file below the compared folder, or an extension) from an older to a newer
/// state: the files added, changed and deleted, the bytes of the added and of the deleted files, and the net change of
/// all of them (<paramref name="NetBytes"/> = newer sizes − older sizes, so a changed file counts with its size change).
/// </summary>
public sealed record GrowthRow(string Key, int Added, int Changed, int Deleted, long AddedBytes, long DeletedBytes, long NetBytes);

/// <summary>The growth below one folder, by direct child and by extension (see <see cref="GrowthTally"/>).</summary>
public sealed record GrowthComparison(IReadOnlyList<GrowthRow> ByFolder, IReadOnlyList<GrowthRow> ByExtension);

/// <summary>A file of a version: its path below the version root (forward slashes), size and time.</summary>
public sealed record IndexedFile(string Path, long Size, DateTime MtimeUtc);

/// <summary>
/// Adds up file differences into <see cref="GrowthRow"/>s by key. Keys ignore case, as paths do on Windows and in the
/// version index: "Docs/" and "docs/" (a case-only rename) are one row, under the spelling seen first.
/// </summary>
public sealed class GrowthTally
{
    private readonly Dictionary<string, Totals> _rows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One file that differs: null <paramref name="olderSize"/> = added, null <paramref name="newerSize"/> = deleted,
    /// both = changed.
    /// </summary>
    public void Add(string key, long? olderSize, long? newerSize)
    {
        // Setting an existing entry keeps its key: the first spelling stays.
        var totals = _rows.GetValueOrDefault(key);
        switch (olderSize, newerSize)
        {
            case (null, { } added):
                totals.Added++;
                totals.AddedBytes += added;
                totals.Net += added;
                break;
            case ({ } deleted, null):
                totals.Deleted++;
                totals.DeletedBytes += deleted;
                totals.Net -= deleted;
                break;
            case ({ } older, { } newer):
                totals.Changed++;
                totals.Net += newer - older;
                break;
            default:
                return;
        }
        _rows[key] = totals;
    }

    /// <summary>The rows, the largest net growth first (then by key).</summary>
    public IReadOnlyList<GrowthRow> Rows() => _rows
        .Select(row => new GrowthRow(row.Key, row.Value.Added, row.Value.Changed, row.Value.Deleted, row.Value.AddedBytes,
            row.Value.DeletedBytes, row.Value.Net))
        .OrderByDescending(row => row.NetBytes)
        .ThenBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
        .ThenBy(row => row.Key, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// The key of <paramref name="path"/> below <paramref name="folder"/> (both relative, forward slashes, "" = root):
    /// the name of the direct child it is in, a folder with a trailing "/" ("docs/"), a file directly inside as its name.
    /// </summary>
    public static string ChildKey(string folder, string path)
    {
        var rest = folder.Length == 0 ? path : path[(folder.Length + 1)..];
        var slash = rest.IndexOf('/');
        return slash < 0 ? rest : rest[..(slash + 1)];
    }

    /// <summary>The extension of a file name in lower case with its dot (".jpg"); "" when it has none.</summary>
    public static string ExtensionKey(string name) => Path.GetExtension(name).ToLowerInvariant();

    private struct Totals
    {
        public int Added, Changed, Deleted;
        public long AddedBytes, DeletedBytes, Net;
    }
}
