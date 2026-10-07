namespace ReBackup.Shared.Indexing;

/// <summary>
/// What a treemap needs of a folder tree entry: its name and size, whether it is ignored (drawn grey), and its children.
/// The app's own entry types implement it (Core's <c>IPreviewEntry</c> extends it).
/// </summary>
public interface ITreemapEntry
{
    string Name { get; }

    /// <summary>Path relative to the source root, with forward slashes; "" for the root.</summary>
    string RelativePath { get; }

    bool IsDirectory { get; }

    /// <summary>Size of the entry and everything below it, ignored or not.</summary>
    long TotalSize { get; }

    /// <summary>Size of what is not ignored.</summary>
    long IncludedSize { get; }

    /// <summary>True when the entry and everything below it is ignored: drawn in the neutral "ignored" colour.</summary>
    bool IsIgnored { get; }

    /// <summary>The label key of the status text shown in the tool tip of the entry's tile (supplied by the app).</summary>
    string StatusLabelKey { get; }

    /// <summary>The children to draw as tiles; empty for a file.</summary>
    IReadOnlyList<ITreemapEntry> GetTreemapChildren();
}
