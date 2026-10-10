using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Backup;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>
/// A folder in the plan's target that another computer marked as being written or deleted (spec 5): shown above the
/// versions, never deleted from here unless a person takes it over.
/// </summary>
public sealed class ForeignFolderRowViewModel(ForeignMarkedFolder folder) : ObservableObject
{
    public ForeignMarkedFolder Folder { get; } = folder;

    public string Name => Folder.Name;

    /// <summary>When the other computer wrote its marker, in this computer's time.</summary>
    public string TimeText => Formats.DateAndTime(ToLocal(Folder.StartedUtc));

    /// <summary>"Being written by PC-2 since 30.09.2026 15:00".</summary>
    public string Text => Loc.F(Folder.Kind == MarkerKind.Pending ? "versions.foreign.pending" : "versions.foreign.deleting",
        ("host", Folder.Host), ("time", TimeText));

    /// <summary>The language changed: every text of the row is read again.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>A marker's time is UTC; one read without a kind is taken as UTC too.</summary>
    private static DateTime ToLocal(DateTime utc) =>
        utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
}
