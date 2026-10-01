using System.Globalization;
using ReBackup.Core.IO;
using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>An entry of the "Compare with" box: another version, or "—" (<paramref name="Row"/> null) for no comparison.</summary>
public sealed record CompareChoice(string Label, VersionRowViewModel? Row);

/// <summary>A search hit; selecting it reveals the entry in the tree.</summary>
public sealed class SearchHitRow(SearchHit hit)
{
    public SearchHit Hit { get; } = hit;
    public string Name => Hit.Name;
    public string Path => Hit.Path;
    public bool IsDirectory => Hit.IsDirectory;
    public string SizeText => ByteSize.Format(Hit.Size);
}

/// <summary>A file in one version, for the history card (newest version first).</summary>
public sealed class FileHistoryRow(HistoryEntry entry, string? versionFolder, string relativePath)
{
    public HistoryEntry Entry { get; } = entry;
    public HistoryStatus Status => Entry.Status;
    public string VersionName => Entry.Version.Name;
    public string DateText => Entry.Version.LocalTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string StatusText => Entry.Status switch
    {
        HistoryStatus.New => "New",
        HistoryStatus.Changed => "Changed",
        HistoryStatus.Unchanged => "Unchanged",
        HistoryStatus.Deleted => "Deleted",
        _ => "—",
    };

    public string SizeText => Entry.Size is { } size ? ByteSize.Format(size) : "";

    public string ModifiedText => Entry.MtimeUtc is { } mtime
        ? mtime.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : "";

    /// <summary>The version folder (null when it is no longer listed) and the file's path in it.</summary>
    public string? VersionFolder { get; } = versionFolder;
    public string RelativePath { get; } = relativePath;

    /// <summary>The folder button shows the copy in Explorer; only for versions that hold the file.</summary>
    public bool CanOpen => Entry.Present && VersionFolder is not null;
}
