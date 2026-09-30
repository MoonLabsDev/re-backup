using System.Globalization;

namespace ReBackup.Core.Retention;

/// <summary>Checking, reading and applying the anchors of retention rules.</summary>
public static class RetentionRules
{
    public const int MaxKeep = 9999;

    private static readonly string[] ShortDayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>Null when the rule can be used; otherwise what is wrong with it.</summary>
    public static string? Validate(RetentionRule? rule)
    {
        if (rule is null)
            return "the rule is empty.";
        if (!Enum.IsDefined(rule.Period))
            return "the period is unknown.";
        if (rule.Keep < 1 || rule.Keep > MaxKeep)
            return $"keep must be a number from 1 to {MaxKeep}.";

        return rule.Period switch
        {
            RetentionPeriod.Weekly when !TryGetWeekday(rule.Anchor, out _) =>
                "the anchor must be a weekday, for example Sunday.",
            RetentionPeriod.Monthly when !TryGetMonthDay(rule.Anchor, out _) =>
                "the anchor must be a day from 1 to 31, 0 for the last day of the month, or -1 to -30 for days before the last day.",
            RetentionPeriod.Yearly when !TryGetYearDate(rule.Anchor, out _, out _) =>
                "the anchor must be a date written as MM-DD, for example 01-01.",
            _ => null,
        };
    }

    /// <summary>Accepts full English weekday names and their three-letter forms, in any case.</summary>
    public static bool TryGetWeekday(string? anchor, out DayOfWeek day)
    {
        day = default;
        var text = anchor?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        for (var i = 0; i < ShortDayNames.Length; i++)
        {
            if (text.Equals(ShortDayNames[i], StringComparison.OrdinalIgnoreCase) ||
                text.Equals(((DayOfWeek)i).ToString(), StringComparison.OrdinalIgnoreCase))
            {
                day = (DayOfWeek)i;
                return true;
            }
        }
        return false;
    }

    public static bool TryGetMonthDay(string? anchor, out int day) =>
        int.TryParse(anchor?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out day) &&
        day is >= -30 and <= 31;

    public static bool TryGetYearDate(string? anchor, out int month, out int day)
    {
        month = 0;
        day = 0;
        var text = anchor?.Trim();
        if (text is not { Length: 5 } || text[2] != '-')
            return false;
        if (!int.TryParse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month) ||
            !int.TryParse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out day))
            return false;

        // 2000 is a leap year, so 02-29 is allowed; it is clamped to 02-28 in other years.
        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(2000, month);
    }

    /// <summary>Short text for a rule, e.g. <c>Weekly(Sun)</c> or <c>Monthly(0)</c>.</summary>
    public static string Describe(RetentionRule rule) => rule.Period switch
    {
        RetentionPeriod.Daily => "Daily",
        RetentionPeriod.Weekly when TryGetWeekday(rule.Anchor, out var weekday) => $"Weekly({ShortDayNames[(int)weekday]})",
        RetentionPeriod.Monthly when TryGetMonthDay(rule.Anchor, out var monthDay) =>
            string.Create(CultureInfo.InvariantCulture, $"Monthly({monthDay})"),
        RetentionPeriod.Yearly when TryGetYearDate(rule.Anchor, out var month, out var day) =>
            string.Create(CultureInfo.InvariantCulture, $"Yearly({month:00}-{day:00})"),
        _ => $"{rule.Period}({rule.Anchor})",
    };

    /// <summary>The anchor day of a month: 1..31 is that day (at most the last day), 0 the last day, -n n days before it.</summary>
    public static DateOnly MonthAnchor(int year, int month, int anchor)
    {
        var days = DateTime.DaysInMonth(year, month);
        var day = anchor >= 1 ? Math.Min(anchor, days) : Math.Max(1, days + anchor);
        return new DateOnly(year, month, day);
    }

    /// <summary>The anchor date of a year; a day the month does not have in that year becomes its last day.</summary>
    public static DateOnly YearAnchor(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    /// <summary>The first day of the slot that contains <paramref name="date"/>: the anchor day at or before it.</summary>
    /// <exception cref="ArgumentException">The rule is not valid.</exception>
    public static DateOnly SlotStart(RetentionRule rule, DateOnly date)
    {
        switch (rule.Period)
        {
            case RetentionPeriod.Daily:
                return date;

            case RetentionPeriod.Weekly when TryGetWeekday(rule.Anchor, out var weekday):
                var daysBack = ((int)date.DayOfWeek - (int)weekday + 7) % 7;
                return date.DayNumber >= daysBack ? date.AddDays(-daysBack) : DateOnly.MinValue;

            case RetentionPeriod.Monthly when TryGetMonthDay(rule.Anchor, out var monthDay):
                var thisMonth = MonthAnchor(date.Year, date.Month, monthDay);
                if (date >= thisMonth)
                    return thisMonth;
                if (date is { Year: 1, Month: 1 })
                    return DateOnly.MinValue;
                var previous = date.AddMonths(-1);
                return MonthAnchor(previous.Year, previous.Month, monthDay);

            case RetentionPeriod.Yearly when TryGetYearDate(rule.Anchor, out var month, out var day):
                var thisYear = YearAnchor(date.Year, month, day);
                if (date >= thisYear)
                    return thisYear;
                return date.Year == 1 ? DateOnly.MinValue : YearAnchor(date.Year - 1, month, day);

            default:
                throw new ArgumentException($"The retention rule is not valid: {Validate(rule)}", nameof(rule));
        }
    }
}
