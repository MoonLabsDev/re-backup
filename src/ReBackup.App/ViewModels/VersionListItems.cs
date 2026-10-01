using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.App.Localization;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>An entry of the "Compare with" box: another version, or "—" (<paramref name="Row"/> null) for no comparison.</summary>
public sealed record CompareChoice(string Label, VersionRowViewModel? Row);

/// <summary>A search hit; selecting it reveals the entry in the tree.</summary>
public sealed class SearchHitRow(SearchHit hit) : ObservableObject
{
    public SearchHit Hit { get; } = hit;
    public string Name => Hit.Name;
    public string Path => Hit.Path;
    public bool IsDirectory => Hit.IsDirectory;
    public string SizeText => Formats.Bytes(Hit.Size);

    /// <summary>The language changed.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A file in one version, for the history card (newest version first).</summary>
public sealed class FileHistoryRow(HistoryEntry entry, string? versionFolder, string relativePath) : ObservableObject
{
    public HistoryEntry Entry { get; } = entry;
    public HistoryStatus Status => Entry.Status;
    public string VersionName => Entry.Version.Name;
    public string DateText => Formats.DateAndTime(Entry.Version.LocalTime);

    public string StatusText => Loc.T("enum.historyStatus." + Entry.Status);

    public string SizeText => Entry.Size is { } size ? Formats.Bytes(size) : "";

    public string ModifiedText => Entry.MtimeUtc is { } mtime ? Formats.DateAndTime(mtime.ToLocalTime()) : "";

    /// <summary>The version folder (null when it is no longer listed) and the file's path in it.</summary>
    public string? VersionFolder { get; } = versionFolder;
    public string RelativePath { get; } = relativePath;

    /// <summary>The folder button shows the copy in Explorer; only for versions that hold the file.</summary>
    public bool CanOpen => Entry.Present && VersionFolder is not null;

    /// <summary>The language changed.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
