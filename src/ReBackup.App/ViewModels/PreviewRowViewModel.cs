using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;

namespace ReBackup.App.ViewModels;

/// <summary>One visible row of the preview tree. Values are read from the entry on every refresh.</summary>
public sealed class PreviewRowViewModel : ObservableObject
{
    private const double IndentPerLevel = 16;
    private readonly PreviewTreeViewModel _tree;
    private bool _isExpanded;

    public PreviewRowViewModel(PreviewTreeViewModel tree, IPreviewEntry entry, IPreviewEntry parent, int depth,
        bool isExpanded)
    {
        _tree = tree;
        Entry = entry;
        Parent = parent;
        Depth = depth;
        _isExpanded = isExpanded;
    }

    public IPreviewEntry Entry { get; }

    /// <summary>The parent folder's entry; the root row has itself.</summary>
    public IPreviewEntry Parent { get; }

    public int Depth { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                _tree.OnExpandedChanged(this, value);
        }
    }

    /// <summary>Sets the flag while the tree rebuilds its rows, without asking the tree to rebuild again.</summary>
    internal void SyncExpanded(bool value) => SetProperty(ref _isExpanded, value, nameof(IsExpanded));

    public bool IsPlaceholder => Entry is LoadingPlaceholder;

    /// <summary>A folder of a running scan that is not finished yet; its values still grow.</summary>
    public bool IsLoading => Entry.IsDirectory && Entry.State != ScanState.Done;

    public bool IsExpandable => Entry.IsDirectory && (IsLoading || Entry.GetChildren().Count > 0);
    public string Name => Entry.Name;
    public Thickness Indent => new(Depth * IndentPerLevel, 0, 0, 0);

    private string Prefix => IsLoading ? "≥ " : "";

    public string SizeText => IsPlaceholder ? "" : Prefix + ByteSize.Format(Entry.TotalSize);

    /// <summary>What the backup of this entry takes: its size without the ignored parts.</summary>
    public string BackupSizeText =>
        IsPlaceholder ? ""
        : Entry.Status == IncludeStatus.Ignored ? "—"
        : Prefix + ByteSize.Format(Entry.IncludedSize);

    public string FilesText =>
        Entry.IsDirectory ? Prefix + Entry.TotalFiles.ToString("N0", CultureInfo.CurrentCulture) : "";

    public double PercentOfParent => Share(Entry.TotalSize, Parent.TotalSize);

    /// <summary>Share of the parent's backup size (both without ignored entries).</summary>
    public double BackupPercentOfParent => Share(Entry.IncludedSize, Parent.IncludedSize);

    public string PercentText =>
        IsPlaceholder ? "" : PercentOfParent.ToString("0.0", CultureInfo.CurrentCulture) + " %";

    public string BackupPercentText =>
        IsPlaceholder ? ""
        : Entry.Status == IncludeStatus.Ignored ? "—"
        : BackupPercentOfParent.ToString("0.0", CultureInfo.CurrentCulture) + " %";

    public string StatusText =>
        IsPlaceholder ? ""
        : IsLoading ? (Entry.State == ScanState.Waiting ? "waiting" : "loading")
        : Entry.Error is not null && Entry.Status != IncludeStatus.Ignored ? "Not scanned"
        : Entry.Status.ToString();

    public bool IsIgnored => !IsPlaceholder && Entry.Status == IncludeStatus.Ignored;
    public bool IsPartial => !IsLoading && Entry.Status == IncludeStatus.Partial;
    public bool IsNotScanned => Entry.Error is not null && Entry.Status != IncludeStatus.Ignored;

    public string? StatusDetail
    {
        get
        {
            if (IsPlaceholder)
                return null;
            var detail = Entry.Status switch
            {
                IncludeStatus.Ignored when Entry.IgnoredByParent =>
                    $"Ignored because a parent folder is ignored by \"{Entry.Pattern?.Text}\" ({Entry.Pattern?.Origin})",
                IncludeStatus.Ignored =>
                    $"Ignored by \"{Entry.Pattern?.Text}\" ({Entry.Pattern?.Origin})",
                IncludeStatus.Partial =>
                    $"Partly ignored: {ByteSize.Format(Entry.IgnoredSize)} in {Entry.IgnoredFiles:N0} files are skipped",
                _ when Entry.Pattern is not null =>
                    $"Re-included by \"{Entry.Pattern.Text}\" ({Entry.Pattern.Origin})",
                _ => "Included",
            };
            if (IsLoading)
                detail = "Still being scanned; the numbers still grow.\n" + detail;
            return Entry.Error is null ? detail : $"{detail}\nNot scanned: {Entry.Error}";
        }
    }

    /// <summary>Re-reads every value (a running scan changes them).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    // Values of a running scan are read one after the other, so a child can briefly show more than its parent.
    private static double Share(long part, long whole) => whole > 0 ? Math.Min(100, 100.0 * part / whole) : 0;
}
