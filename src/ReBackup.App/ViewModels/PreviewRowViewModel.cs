using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.App.Localization;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;

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

    public bool IsDirectory => Entry.IsDirectory;

    /// <summary>A folder of a running scan that is not finished yet; its values still grow.</summary>
    public bool IsLoading => Entry.IsDirectory && Entry.State != ScanState.Done;

    public bool IsExpandable => Entry.IsDirectory && (IsLoading || Entry.ChildCount > 0);
    public string Name => Entry.Name;
    public Thickness Indent => new(Depth * IndentPerLevel, 0, 0, 0);

    private string Prefix => IsLoading ? "≥ " : "";

    public string SizeText => IsPlaceholder ? "" : Prefix + Formats.Bytes(Entry.TotalSize);

    /// <summary>What the backup of this entry takes: its size without the ignored parts.</summary>
    public string BackupSizeText =>
        IsPlaceholder ? ""
        : Entry.Status == IncludeStatus.Ignored ? "—"
        : Prefix + Formats.Bytes(Entry.IncludedSize);

    public string FilesText =>
        Entry.IsDirectory ? Prefix + Formats.Count(Entry.TotalFiles) : "";

    public double PercentOfParent => Share(Entry.TotalSize, Parent.TotalSize);

    /// <summary>Share of the parent's backup size (both without ignored entries).</summary>
    public double BackupPercentOfParent => Share(Entry.IncludedSize, Parent.IncludedSize);

    public string PercentText =>
        IsPlaceholder ? "" : PercentOfParent.ToString("0.0", Loc.Culture) + " %";

    public string BackupPercentText =>
        IsPlaceholder ? ""
        : Entry.Status == IncludeStatus.Ignored ? "—"
        : BackupPercentOfParent.ToString("0.0", Loc.Culture) + " %";

    // The values the tree shows; they follow the tree's "In backup | Total" toggle.
    private bool InBackup => _tree.ShowInBackup;
    private bool ShowsDash => InBackup && IsIgnored;

    public string ShownFilesText =>
        !Entry.IsDirectory ? ""
        : ShowsDash ? "—"
        : Prefix + Formats.Count(InBackup ? Entry.IncludedFiles : Entry.TotalFiles);

    public string ShownSizeText => InBackup ? BackupSizeText : SizeText;

    public double ShownPercent => ShowsDash ? 0 : InBackup ? BackupPercentOfParent : PercentOfParent;

    public string ShownPercentText => InBackup ? BackupPercentText : PercentText;

    public string StatusText =>
        IsPlaceholder ? ""
        : IsLoading ? (Entry.State == ScanState.Waiting ? Loc.T("ignore.row.waiting") : Loc.T("ignore.row.loading"))
        : Entry.Error is not null && Entry.Status != IncludeStatus.Ignored ? Loc.T("ignore.row.notScanned")
        : Loc.T("enum.includeStatus." + Entry.Status);

    public bool IsIgnored => !IsPlaceholder && Entry.Status == IncludeStatus.Ignored;
    public bool IsPartial => !IsLoading && Entry.Status == IncludeStatus.Partial;
    public bool IsNotScanned => Entry.Error is not null && Entry.Status != IncludeStatus.Ignored;

    public string? StatusDetail
    {
        get
        {
            if (IsPlaceholder)
                return null;
            var pattern = Entry.Pattern?.Text;
            var origin = OriginText(Entry.Pattern?.Origin);
            var detail = Entry.Status switch
            {
                IncludeStatus.Ignored when Entry.IgnoredByParent =>
                    Loc.F("ignore.row.ignoredByParent", ("pattern", pattern), ("origin", origin)),
                IncludeStatus.Ignored => Loc.F("ignore.row.ignoredBy", ("pattern", pattern), ("origin", origin)),
                IncludeStatus.Partial =>
                    Loc.F("ignore.row.partly", ("size", Formats.Bytes(Entry.IgnoredSize)), ("files", Entry.IgnoredFiles)),
                _ when Entry.Pattern is not null => Loc.F("ignore.row.reincluded", ("pattern", pattern), ("origin", origin)),
                _ => Loc.T("ignore.row.included"),
            };
            if (IsLoading)
                detail = Loc.T("ignore.row.stillScanning") + "\n" + detail;
            return Entry.Error is null
                ? detail
                : detail + "\n" + Loc.F("ignore.row.notScannedReason", ("error", Loc.Known(Entry.Error)));
        }
    }

    /// <summary>Where a pattern comes from, as shown: the global defaults, the plan, or the path of a nested ignore file.</summary>
    internal static string? OriginText(string? origin) => origin switch
    {
        IgnoreOrigins.GlobalDefaults => Loc.T("ignore.origin.globalDefaults"),
        IgnoreOrigins.Plan => Loc.T("ignore.origin.plan"),
        _ => origin,
    };

    /// <summary>Re-reads every value (a running scan changes them).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    // Values of a running scan are read one after the other, so a child can briefly show more than its parent.
    private static double Share(long part, long whole) => whole > 0 ? Math.Min(100, 100.0 * part / whole) : 0;
}
