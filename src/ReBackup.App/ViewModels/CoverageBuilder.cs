using System.Globalization;
using ReBackup.Core.Backup;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>
/// The coverage timeline of the Retention and Versions tabs: lanes from a plan's owned versions, its rules and its run
/// log, the lanes' labels and the cells' tool tips, in the applied language.
/// </summary>
public static class CoverageBuilder
{
    /// <summary>
    /// The lanes and their labels. Only <see cref="VersionOwnership.Owned"/> versions count; a version whose run (looked up
    /// in <paramref name="log"/> by version name) ended with warnings is marked so, with its skipped count as detail.
    /// </summary>
    /// <exception cref="ArgumentException">A rule is not valid.</exception>
    public static (IReadOnlyList<CoverageLane> Lanes, IReadOnlyList<string> Labels) Build(IReadOnlyList<VersionInfo> owned,
        IReadOnlyList<RetentionRule> rules, IReadOnlyList<RunLogEntry> log, DateOnly today)
    {
        var runs = new Dictionary<string, RunLogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in log)
        {
            if (!string.IsNullOrEmpty(entry.Version))
                runs[entry.Version] = entry;   // the log is oldest first: the last run of a name wins
        }

        var versions = owned.Where(v => v.IsOwned)
            .Select(v => runs.TryGetValue(v.Name, out var run) && run.Status == RunStatus.CompletedWithWarnings
                ? new CoverageVersion(v.Name, v.LocalTime, CoverageQuality.Warning, true,
                    run.SkippedCount.ToString(CultureInfo.InvariantCulture))
                : new CoverageVersion(v.Name, v.LocalTime, CoverageQuality.Ok, true, null))
            .ToList();
        var lanes = RetentionCoverage.Compute(versions, rules, today);
        return (lanes, lanes.Select(lane => Label(lane.Rule)).ToList());
    }

    /// <summary>A lane's label, e.g. "Daily · 7" or "Weekly(Sun) · 4".</summary>
    public static string Label(RetentionRule rule) =>
        Loc.F("retention.coverage.lane", ("rule", RetentionTexts.Describe(rule)), ("keep", rule.Keep));

    /// <summary>
    /// A cell's tool tip: the period, then the version (date and, when <paramref name="sizeOf"/> knows it, size) and its
    /// warnings, or why there is none.
    /// </summary>
    public static string SlotText(CoverageSlot slot, Func<string, long?> sizeOf)
    {
        var last = slot.End.AddDays(-1);
        var period = last <= slot.Start
            ? Formats.Date(Day(slot.Start))
            : Loc.F("retention.coverage.period", ("from", Formats.Date(Day(slot.Start))), ("to", Formats.Date(Day(last))));

        if (slot.Version is { } version)
        {
            var text = period + "\n" + Formats.DateAndTime(version.LocalTime) +
                       (sizeOf(version.Id) is { } bytes ? " · " + Formats.Bytes(bytes) : "");
            if (version.Quality == CoverageQuality.Warning)
                text += "\n" + (int.TryParse(version.Detail, NumberStyles.None, CultureInfo.InvariantCulture, out var skipped)
                    ? Loc.F("retention.coverage.warnings", ("count", skipped))
                    : Loc.T("retention.coverage.warningsOnly"));
            return text;
        }

        return period + "\n" + slot.State switch
        {
            SlotState.Pending => Loc.T("retention.coverage.pending"),
            SlotState.BeforeFirst => Loc.T("retention.coverage.beforeFirst"),
            _ => Loc.T("retention.coverage.missing"),
        };
    }

    private static DateTime Day(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);
}
