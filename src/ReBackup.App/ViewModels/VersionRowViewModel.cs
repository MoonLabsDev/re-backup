using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.App.Localization;
using ReBackup.Core.Backup;
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

    public string DateText => Formats.DateAndTime(Info.LocalTime);

    public string SizeText => (Info.TotalBytes ?? Indexed?.TotalBytes) is { } bytes ? Formats.Bytes(bytes) : "—";

    public string FilesText =>
        (Info.FileCount ?? Indexed?.FileCount) is { } count ? Loc.F("common.fileCount", ("count", count)) : "—";

    /// <summary>Retention manages it; the others are greyed and never deleted.</summary>
    public bool IsManaged => Info.IsOwned;

    /// <summary>Why a folder is not managed; empty for managed versions.</summary>
    public string ReasonText => Info.Ownership switch
    {
        VersionOwnership.NoManifest => Loc.T("versions.notManaged.noManifest"),
        VersionOwnership.Foreign => Loc.T("versions.notManaged.foreign"),
        VersionOwnership.Unreadable => Loc.T("versions.notManaged.unreadable"),
        VersionOwnership.Renamed => Loc.T("versions.notManaged.renamed"),
        _ => "",
    };

    public string IndexStateText => IndexState switch
    {
        IndexState.Indexing => Loc.T("versions.index.indexing"),
        IndexState.Indexed => Loc.T("versions.index.indexed"),
        IndexState.Failed => Loc.T("versions.index.failed"),
        _ => Loc.T("versions.index.notIndexed"),
    };

    /// <summary>The language changed: every text of the row is read again.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
