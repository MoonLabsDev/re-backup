using ReBackup.App.Localization;
using ReBackup.Core.Backup;
using ReBackup.Core.Retention;

namespace ReBackup.App.ViewModels;

/// <summary>One version folder in the "now" list of the Retention tab.</summary>
public sealed class RetentionNowRow
{
    public RetentionNowRow(VersionDecision decision, IReadOnlyList<RetentionRule> rules)
    {
        var version = decision.Version;
        Name = version.Name;
        DateText = Formats.DateAndTime(version.LocalTime);
        SizeText = version.TotalBytes is { } bytes ? Formats.Bytes(bytes) : "";
        FilesText = version.FileCount is { } files ? Formats.Count(files) : "";
        IsManaged = decision.Decision is not null;
        IsDelete = decision.Delete;
        DecisionText = !IsManaged ? Loc.T("retention.decision.notManaged")
            : IsDelete ? Loc.T("retention.decision.delete")
            : Loc.T("retention.decision.keep");
        ReasonText = decision.Decision is { } made
            ? string.Join(", ", made.Reasons.Select(reason => RetentionTexts.Reason(reason, rules)))
            : version.Ownership switch
            {
                VersionOwnership.NoManifest => Loc.T("retention.owner.noManifest"),
                VersionOwnership.Foreign => Loc.T("retention.owner.foreign"),
                VersionOwnership.Renamed => Loc.T("retention.owner.renamed"),
                _ => Loc.T("retention.owner.unreadable"),
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
