using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.IO;
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

    internal VersionTreeNode(VersionTreeViewModel tree, IndexChild entry, int depth, DiffStatus? status)
    {
        _tree = tree;
        Entry = entry;
        Depth = depth;
        Status = status;
    }

    /// <summary>A message row ("Loading…", "Indexing…", "Empty folder").</summary>
    internal VersionTreeNode(string message, int depth)
    {
        Message = message;
        Depth = depth;
    }

    public IndexChild? Entry { get; }
    public string? Message { get; }
    public int Depth { get; }

    /// <summary>The folder's children once loaded; null before the first expansion.</summary>
    internal List<VersionTreeNode>? Children { get; set; }

    public bool IsMessage => Entry is null;
    public long PathId => Entry?.PathId ?? -1;
    public string Name => Entry?.Name ?? Message ?? "";
    public string Path => Entry?.Path ?? "";
    public bool IsDirectory => Entry?.IsDirectory ?? false;
    public bool IsExpandable => IsDirectory;
    public Thickness Indent => new(Depth * IndentPerLevel, 0, 0, 0);

    /// <summary>The entry exists only in version B of the comparison.</summary>
    public bool OnlyInOther => Entry is { InVersion: null };

    /// <summary>The values shown: version A's, or B's for entries only in B.</summary>
    public IndexStats? Stats => Entry?.InVersion ?? Entry?.InOther;

    public string SizeText => Stats is { } stats ? ByteSize.Format(stats.Size) : "";

    public string FilesText => IsDirectory && Stats is { } stats ? stats.Files.ToString("N0", CultureInfo.CurrentCulture) : "";

    public string ModifiedText => Stats?.MtimeUtc is { } mtime
        ? mtime.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : "";

    public string StatusText => Status?.ToString() ?? "";

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
