using System.Globalization;
using ReBackup.Core.Localization;
using ReBackup.Core.Retention;

namespace ReBackup.Core.Schedule;

/// <summary>Checking and reading the fields of schedule triggers.</summary>
public static class ScheduleTriggers
{
    private static readonly string[] TimeFormats = ["HH:mm", "H:mm"];

    /// <summary>The weekday names triggers are written with, Monday first.</summary>
    public static IReadOnlyList<string> ShortDayNames { get; } = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>Null when the trigger can be used; otherwise what is wrong with it (keys under <c>core.trigger</c>).</summary>
    public static Message? Validate(ScheduleTrigger? trigger)
    {
        if (trigger is null)
            return Message.Of("core.trigger.empty");
        if (!Enum.IsDefined(trigger.Type))
            return Message.Of("core.trigger.unknownType");

        if (trigger.Type != TriggerType.Interval && !TryParseTime(trigger.Time, out _))
            return Message.Of("core.trigger.time");

        switch (trigger.Type)
        {
            case TriggerType.Weekly:
                if (trigger.Days is not { Count: > 0 })
                    return Message.Of("core.trigger.noWeekday");
                foreach (var day in trigger.Days)
                {
                    if (!RetentionRules.TryGetWeekday(day, out _))
                        return Message.Of("core.trigger.notWeekday", ("day", day));
                }
                return null;

            case TriggerType.Monthly:
                return trigger.Day is >= -30 and <= 31 ? null : Message.Of("core.trigger.monthDay");

            case TriggerType.Interval:
                if (trigger.EveryHours is not (>= 1 and <= 24))
                    return Message.Of("core.trigger.interval");
                if (trigger.From is not null && !TryParseTime(trigger.From, out _))
                    return Message.Of("core.trigger.from");
                if (trigger.To is not null && !TryParseTime(trigger.To, out _))
                    return Message.Of("core.trigger.to");
                var (from, to) = IntervalWindow(trigger);
                return from > to ? Message.Of("core.trigger.fromAfterTo") : null;

            default:
                return null;
        }
    }

    public static bool TryParseTime(string? text, out TimeOnly time)
    {
        time = default;
        return !string.IsNullOrWhiteSpace(text) &&
               TimeOnly.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    /// <summary>The distinct weekdays of a weekly trigger, in the order written; unreadable names are left out.</summary>
    public static IReadOnlyList<DayOfWeek> WeekdaysOf(ScheduleTrigger trigger)
    {
        var days = new List<DayOfWeek>();
        foreach (var name in trigger.Days ?? [])
        {
            if (RetentionRules.TryGetWeekday(name, out var day) && !days.Contains(day))
                days.Add(day);
        }
        return days;
    }

    /// <summary>The daily window of an interval trigger; 00:00–23:59 where a bound is missing or unreadable.</summary>
    public static (TimeOnly From, TimeOnly To) IntervalWindow(ScheduleTrigger trigger) =>
        (TryParseTime(trigger.From, out var from) ? from : new TimeOnly(0, 0),
         TryParseTime(trigger.To, out var to) ? to : new TimeOnly(23, 59));
}
