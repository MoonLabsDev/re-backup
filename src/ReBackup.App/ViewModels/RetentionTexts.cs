using System.Globalization;
using ReBackup.App.Localization;
using ReBackup.Core.Retention;

namespace ReBackup.App.ViewModels;

/// <summary>Retention rules and keep reasons as the applied language shows them (Core's labels are English).</summary>
public static class RetentionTexts
{
    /// <summary>Short text for a rule: "Weekly(Sun)" / "Wöchentlich(So)", "Monthly(0)", "Yearly(01-01)".</summary>
    public static string Describe(RetentionRule rule) => rule.Period switch
    {
        RetentionPeriod.Daily => Loc.T("enum.period.Daily"),
        RetentionPeriod.Weekly when RetentionRules.TryGetWeekday(rule.Anchor, out var weekday) =>
            Loc.F("retention.describe.weekly", ("day", Loc.T("enum.weekdayShort." + weekday.ToString()[..3]))),
        RetentionPeriod.Monthly when RetentionRules.TryGetMonthDay(rule.Anchor, out var monthDay) =>
            Loc.F("retention.describe.monthly", ("day", monthDay)),
        RetentionPeriod.Yearly when RetentionRules.TryGetYearDate(rule.Anchor, out var month, out var day) =>
            Loc.F("retention.describe.yearly", ("date", string.Create(CultureInfo.InvariantCulture, $"{month:00}-{day:00}"))),
        _ => $"{Loc.T("enum.period." + rule.Period)}({rule.Anchor})",
    };

    /// <summary>Why a version is kept: "Daily #3" for a rule's slot, or the built-in reasons (newest, no rules).</summary>
    public static string Reason(KeepReason reason, IReadOnlyList<RetentionRule> rules)
    {
        if (reason.RuleIndex >= 0 && reason.RuleIndex < rules.Count)
            return $"{Describe(rules[reason.RuleIndex])} #{reason.Slot}";
        if (reason.Label == RetentionEngine.NewestLabel)
            return Loc.T("retention.reason.newest");
        if (reason.Label == RetentionEngine.NoRulesLabel)
            return Loc.T("retention.reason.noRules");
        return reason.Label;
    }
}
