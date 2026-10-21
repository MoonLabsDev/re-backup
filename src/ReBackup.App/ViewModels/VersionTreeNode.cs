using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>
/// A row of the version tree: a file or folder of version A (or, while comparing, of version B only), or a message
/// row ("Loading…"). Children are loaded when the folder is first expanded.
/// </summary>
public sealed partial class VersionTreeNode : ObservableObject
{
    private const double IndentPerLevel = 16;
    private readonly VersionTreeViewModel? _tree;
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private DiffStatus? _status;

    internal VersionTreeNode(VersionTreeViewModel tree, IndexChild entry, int depth, DiffStatus? status,
        bool shownIsNewer = false)
    {
        _tree = tree;
        Entry = entry;
        Depth = depth;
        Status = status;
        ShownIsNewer = shownIsNewer;
    }

    /// <summary>A message row ("Loading…", "Indexing…", "Empty folder").</summary>
    internal VersionTreeNode(LocText message, int depth)
    {
        Message = message;
        Depth = depth;
    }

    public IndexChild? Entry { get; }
    public LocText? Message { get; }
    public int Depth { get; }

    /// <summary>The folder's children once loaded; null before the first expansion.</summary>
    internal List<VersionTreeNode>? Children { get; set; }

    public bool IsMessage => Entry is null;
    public long PathId => Entry?.PathId ?? -1;
    public string Name => Entry?.Name ?? Message?.ToString() ?? "";
    public string Path => Entry?.Path ?? "";
    public bool IsDirectory => Entry?.IsDirectory ?? false;
    public bool IsExpandable => IsDirectory;
    public Thickness Indent => new(Depth * IndentPerLevel, 0, 0, 0);

    /// <summary>The entry exists only in version B of the comparison.</summary>
    public bool OnlyInOther => Entry is { InVersion: null };

    /// <summary>The values shown: version A's, or B's for entries only in B.</summary>
    public IndexStats? Stats => Entry?.InVersion ?? Entry?.InOther;

    /// <summary>Version A is the newer one of the comparison (the difference is newer minus older).</summary>
    public bool ShownIsNewer { get; }

    /// <summary>A's size; while comparing an entry only in B has none (B's is the compared size).</summary>
    public string SizeText =>
        (Status is null ? Stats : Entry?.InVersion) is { } stats ? Formats.Bytes(stats.Size) : "";

    /// <summary>B's size while comparing; empty for entries only in A.</summary>
    public string CompareSizeText => Status is not null && Entry?.InOther is { } other ? Formats.Bytes(other.Size) : "";

    /// <summary>
    /// While comparing: the newer version's size minus the older one's (a missing entry counts as 0), so growth is
    /// positive whichever of the two is shown; null otherwise.
    /// </summary>
    public long? SizeChange => Status is null || Entry is not { } entry ? null
        : ShownIsNewer ? (entry.InVersion?.Size ?? 0) - (entry.InOther?.Size ?? 0)
        : (entry.InOther?.Size ?? 0) - (entry.InVersion?.Size ?? 0);

    /// <summary>"+1.2 MB" / "−1.2 MB"; empty when the size stayed the same or nothing is compared.</summary>
    public string SizeChangeText => SizeChange switch
    {
        null or 0 => "",
        > 0 and var growth => "+" + Formats.Bytes(growth),
        var shrink => "−" + Formats.Bytes(-shrink.Value),
    };

    public bool IsGrowth => SizeChange > 0;

    public string FilesText => IsDirectory && Stats is { } stats ? Formats.Count(stats.Files) : "";

    public string ModifiedText => Stats?.MtimeUtc is { } mtime ? Formats.DateAndTime(mtime.ToLocalTime()) : "";

    public string StatusText => Status is { } status ? Loc.T("enum.diffStatus." + status) : "";

    /// <summary>The language changed: every text of the row is read again.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                _tree?.OnExpandedChanged(this, value);
        }
    }

    /// <summary>Sets the flag while the tree rebuilds its rows, without asking it to load or rebuild again.</summary>
    internal void SyncExpanded(bool value) => SetProperty(ref _isExpanded, value, nameof(IsExpanded));
}
