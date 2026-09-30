using System.Globalization;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;
using ReBackup.Core.Retention;

namespace ReBackup.App.ViewModels;

/// <summary>One version folder in the "now" list of the Retention tab.</summary>
public sealed class RetentionNowRow
{
    public RetentionNowRow(VersionDecision decision)
    {
        var version = decision.Version;
        Name = version.Name;
        DateText = version.LocalTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        SizeText = version.TotalBytes is { } bytes ? ByteSize.Format(bytes) : "";
        FilesText = version.FileCount is { } files ? files.ToString("N0", CultureInfo.CurrentCulture) : "";
        IsManaged = decision.Decision is not null;
        IsDelete = decision.Delete;
        DecisionText = !IsManaged ? "Not managed" : IsDelete ? "Delete" : "Keep";
        ReasonText = decision.Decision is { } made
            ? string.Join(", ", made.Reasons.Select(r => r.Label))
            : version.Ownership switch
            {
                VersionOwnership.NoManifest => "no manifest in the folder",
                VersionOwnership.Foreign => "the manifest belongs to another plan",
                VersionOwnership.Renamed => "renamed or copied by hand",
                _ => "the manifest cannot be read",
            };
    }

    public string Name { get; }
    public string DateText { get; }
    public string SizeText { get; }
    public string FilesText { get; }
    public string DecisionText { get; }
    public string ReasonText { get; }
    public bool IsDelete { get; }
    public bool IsManaged { get; }
}
