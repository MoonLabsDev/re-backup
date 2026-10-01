using ReBackup.App.Localization;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;

namespace ReBackup.App.ViewModels;

/// <summary>The "loading …" row shown in an expanded folder of which nothing has been listed yet.</summary>
public sealed class LoadingPlaceholder(string parentPath) : IPreviewEntry
{
    public string Name => Loc.T("ignore.row.placeholder");
    public string RelativePath { get; } = parentPath + "/…";
    public bool IsDirectory => false;
    public string? Error => null;
    public ScanState State => ScanState.Waiting;
    public IncludeStatus Status => IncludeStatus.Included;
    public IgnorePattern? Pattern => null;
    public bool IgnoredByParent => false;
    public long IncludedSize => 0;
    public long IgnoredSize => 0;
    public int IncludedFiles => 0;
    public int IgnoredFiles => 0;
    public long TotalSize => 0;
    public int TotalFiles => 0;
    public int ChildCount => 0;
    public IReadOnlyList<IPreviewEntry> GetChildren() => [];
}
