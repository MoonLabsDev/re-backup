namespace ReBackup.Core.Versions;

/// <summary>
/// What changed under one key (a folder or file below the compared folder, or an extension) from an older to a newer
/// state: the files added, changed and deleted, the bytes of the added files, the newer sizes of the changed files, the
/// bytes of the deleted files, and the net change of all of them (<paramref name="NetBytes"/> = newer sizes − older
/// sizes, so a changed file counts with its size change only).
/// </summary>
public sealed record GrowthRow(string Key, int Added, int Changed, int Deleted, long AddedBytes, long ChangedBytes,
    long DeletedBytes, long NetBytes)
{
    /// <summary>
    /// What an incremental run copies for these files: the added ones and the changed ones whole. A large file rewritten
    /// in place (a mailbox, a VM disk, a database) costs its full size in every version although its net growth is ~0.
    /// </summary>
    public long CopiedBytes => AddedBytes + ChangedBytes;
}

/// <summary>How growth rows are ordered: by net growth, or by the bytes an incremental run copies.</summary>
public enum GrowthSort
{
    /// <summary>The largest <see cref="GrowthRow.NetBytes"/> first.</summary>
    Net,

    /// <summary>The largest <see cref="GrowthRow.CopiedBytes"/> first (then net growth).</summary>
    Copied,
}

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
                totals.ChangedBytes += newer;
                totals.Net += newer - older;
                break;
            default:
                return;
        }
        _rows[key] = totals;
    }

    /// <summary>The rows, the largest net growth (or copied bytes, by <paramref name="sort"/>) first, then by key.</summary>
    public IReadOnlyList<GrowthRow> Rows(GrowthSort sort = GrowthSort.Net) => Order(_rows
        .Select(row => new GrowthRow(row.Key, row.Value.Added, row.Value.Changed, row.Value.Deleted, row.Value.AddedBytes,
            row.Value.ChangedBytes, row.Value.DeletedBytes, row.Value.Net)), sort);

    /// <summary><paramref name="rows"/> in the order of <paramref name="sort"/>; ties by key.</summary>
    public static IReadOnlyList<GrowthRow> Order(IEnumerable<GrowthRow> rows, GrowthSort sort)
    {
        var ordered = sort == GrowthSort.Copied
            ? rows.OrderByDescending(row => row.CopiedBytes).ThenByDescending(row => row.NetBytes)
            : rows.OrderByDescending(row => row.NetBytes);
        return ordered
            .ThenBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Key, StringComparer.Ordinal)
            .ToList();
    }

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
        public long AddedBytes, ChangedBytes, DeletedBytes, Net;
    }
}
