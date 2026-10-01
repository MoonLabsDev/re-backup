using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>Whether a version's file list is in the plan's index.</summary>
public enum IndexState { NotIndexed, Indexing, Indexed, Failed }

/// <summary>A version folder in the list of the Versions tab. Kept by name across refreshes.</summary>
public sealed partial class VersionRowViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndexStateText))]
    private IndexState _indexState;

    /// <summary>
    /// The version as the index knows it; null until it is indexed. A re-import gives the version a new id, so this is
    /// replaced after every sync and cleared while the version is being imported again.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(FilesText))]
    private IndexedVersion? _indexed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(FilesText), nameof(DateText), nameof(IsManaged), nameof(ReasonText))]
    private VersionInfo _info = null!;

    public VersionRowViewModel(VersionInfo info) => Info = info;

    public string Name => Info.Name;

    public string DateText => Info.LocalTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string SizeText => (Info.TotalBytes ?? Indexed?.TotalBytes) is { } bytes ? ByteSize.Format(bytes) : "—";

    public string FilesText =>
        (Info.FileCount ?? Indexed?.FileCount) is { } count ? count.ToString("N0", CultureInfo.CurrentCulture) + " files" : "—";

    /// <summary>Retention manages it; the others are greyed and never deleted.</summary>
    public bool IsManaged => Info.IsOwned;

    /// <summary>Why a folder is not managed; empty for managed versions.</summary>
    public string ReasonText => Info.Ownership switch
    {
        VersionOwnership.NoManifest => "Not managed: no manifest",
        VersionOwnership.Foreign => "Not managed: manifest of another plan",
        VersionOwnership.Unreadable => "Not managed: manifest cannot be read",
        VersionOwnership.Renamed => "Not managed: renamed or copied by hand",
        _ => "",
    };

    public string IndexStateText => IndexState switch
    {
        IndexState.Indexing => "indexing…",
        IndexState.Indexed => "indexed",
        IndexState.Failed => "could not be indexed",
        _ => "not indexed",
    };
}
