using ReBackup.Core.Ignore;

namespace ReBackup.Core.Indexing;

public enum ScanState
{
    /// <summary>Found by its parent folder, not listed yet.</summary>
    Waiting,

    /// <summary>Listed; something below it is not finished yet.</summary>
    Scanning,

    /// <summary>The entry and everything below it is finished.</summary>
    Done,
}

/// <summary>An entry of the preview tree: a finished evaluation or an entry of a scan that is still running.</summary>
public interface IPreviewEntry
{
    string Name { get; }

    /// <summary>Path relative to the source root, with forward slashes; "" for the root.</summary>
    string RelativePath { get; }

    bool IsDirectory { get; }
    string? Error { get; }
    ScanState State { get; }
    IncludeStatus Status { get; }
    IgnorePattern? Pattern { get; }
    bool IgnoredByParent { get; }
    long IncludedSize { get; }
    long IgnoredSize { get; }
    int IncludedFiles { get; }
    int IgnoredFiles { get; }
    long TotalSize { get; }
    int TotalFiles { get; }

    /// <summary>The children as they are now; for a running scan a copy that does not change afterwards.</summary>
    IReadOnlyList<IPreviewEntry> GetChildren();
}
