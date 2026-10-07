using ReBackup.Core.Ignore;
using ReBackup.Shared.Indexing;

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

/// <summary>
/// An entry of the preview tree: a finished evaluation or an entry of a scan that is still running. Also what the
/// treemap draws (<see cref="ITreemapEntry"/>).
/// </summary>
public interface IPreviewEntry : ITreemapEntry
{
    // Name, RelativePath, IsDirectory, IncludedSize and TotalSize are those of ITreemapEntry.
    string? Error { get; }
    ScanState State { get; }
    IncludeStatus Status { get; }
    IgnorePattern? Pattern { get; }
    bool IgnoredByParent { get; }
    long IgnoredSize { get; }
    int IncludedFiles { get; }
    int IgnoredFiles { get; }
    int TotalFiles { get; }

    /// <summary>The number of children as of now; cheaper than <c>GetChildren().Count</c> for a running scan.</summary>
    int ChildCount { get; }

    /// <summary>The children as they are now; for a running scan a copy that does not change afterwards.</summary>
    IReadOnlyList<IPreviewEntry> GetChildren();

    bool ITreemapEntry.IsIgnored => Status == IncludeStatus.Ignored;

    string ITreemapEntry.StatusLabelKey => "enum.includeStatus." + Status;

    IReadOnlyList<ITreemapEntry> ITreemapEntry.GetTreemapChildren() => GetChildren();
}
