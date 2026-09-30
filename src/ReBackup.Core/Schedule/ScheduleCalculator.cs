using ReBackup.Core.Retention;

namespace ReBackup.Core.Schedule;

/// <summary>
/// Turns triggers (local wall-clock times) into run instants (UTC). A time that does not exist because the clocks
/// jump forward runs at the first valid minute after it; a time that exists twice runs once, at the earlier instant.
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>How far back <see cref="LastDue"/> looks at most; it only needs to know whether a trigger was missed.</summary>
    public static readonly TimeSpan LookBack = TimeSpan.FromDays(400);

    /// <summary>The run instants of one trigger strictly after <paramref name="afterUtc"/>, ascending and endless.</summary>
    /// <exception cref="ArgumentException">The trigger is not valid.</exception>
    public static IEnumerable<DateTime> Occurrences(ScheduleTrigger trigger, DateTime afterUtc, TimeZoneInfo zone)
    {
        if (ScheduleTriggers.Validate(trigger) is { } problem)
            throw new ArgumentException($"The trigger is not valid: {problem}", nameof(trigger));
        return Iterate(trigger, DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), zone);
    }

    /// <summary>The run instants of all triggers, merged, ascending and without duplicates.</summary>
    /// <exception cref="ArgumentException">A trigger is not valid.</exception>
    public static IEnumerable<DateTime> NextRuns(IReadOnlyList<ScheduleTrigger> triggers, DateTime afterUtc, TimeZoneInfo zone)
    {
        var sources = triggers.Select(t => Occurrences(t, afterUtc, zone)).ToList();
        return Merge(sources);
    }

    /// <summary>The latest run instant after <paramref name="afterUtc"/> and at or before <paramref name="nowUtc"/>; null when there is none.</summary>
    /// <exception cref="ArgumentException">A trigger is not valid.</exception>
    public static DateTime? LastDue(IReadOnlyList<ScheduleTrigger> triggers, DateTime afterUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        var earliest = nowUtc - LookBack;
        var start = afterUtc < earliest ? earliest : afterUtc;
        DateTime? due = null;
        foreach (var run in NextRuns(triggers, start, zone))
        {
            if (run > nowUtc)
                break;
            due = run;
        }
        return due;
    }

    /// <summary><see cref="NextRuns"/> as local wall-clock times of <paramref name="zone"/>.</summary>
    public static IEnumerable<DateTime> LocalRunTimes(IReadOnlyList<ScheduleTrigger> triggers, DateTime afterUtc, TimeZoneInfo zone) =>
        NextRuns(triggers, afterUtc, zone)
            .Select(utc => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, zone), DateTimeKind.Unspecified));

    private static IEnumerable<DateTime> Iterate(ScheduleTrigger trigger, DateTime afterUtc, TimeZoneInfo zone)
    {
        // Start a day early: the local date of the instant can be behind the date of a run that is still ahead.
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone));
        date = date == DateOnly.MinValue ? date : date.AddDays(-1);
        var last = afterUtc;
        while (true)
        {
            foreach (var local in LocalTimesOn(trigger, date))
            {
                var utc = ToUtc(local, zone);
                if (utc > last)
                {
                    last = utc;
                    yield return utc;
                }
            }
            if (date == DateOnly.MaxValue)
                yield break;
            date = date.AddDays(1);
        }
    }

    private static IEnumerable<DateTime> LocalTimesOn(ScheduleTrigger trigger, DateOnly date)
    {
        switch (trigger.Type)
        {
            case TriggerType.Daily:
                yield return At(date, trigger.Time);
                break;

            case TriggerType.Weekly:
                if (ScheduleTriggers.WeekdaysOf(trigger).Contains(date.DayOfWeek))
                    yield return At(date, trigger.Time);
                break;

            case TriggerType.Monthly:
                if (date == RetentionRules.MonthAnchor(date.Year, date.Month, trigger.Day!.Value))
                    yield return At(date, trigger.Time);
                break;

            case TriggerType.Interval:
                var (from, to) = ScheduleTriggers.IntervalWindow(trigger);
                var step = trigger.EveryHours!.Value * 60;
                for (var minute = from.Hour * 60 + from.Minute; minute <= to.Hour * 60 + to.Minute; minute += step)
                    yield return date.ToDateTime(new TimeOnly(minute / 60, minute % 60));
                break;
        }
    }

    private static DateTime At(DateOnly date, string? time)
    {
        ScheduleTriggers.TryParseTime(time, out var parsed);
        return date.ToDateTime(parsed);
    }

    /// <summary>Local wall-clock time to UTC: forward out of a gap, the earlier instant in an overlap.</summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
        {
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static IEnumerable<DateTime> Merge(List<IEnumerable<DateTime>> sources)
    {
        var active = new List<IEnumerator<DateTime>>();
        try
        {
            foreach (var source in sources)
            {
                var enumerator = source.GetEnumerator();
                if (enumerator.MoveNext())
                    active.Add(enumerator);
                else
                    enumerator.Dispose();
            }

            DateTime? last = null;
            while (active.Count > 0)
            {
                var next = active.MinBy(e => e.Current)!;
                var value = next.Current;
                if (last is null || value > last)
                {
                    last = value;
                    yield return value;
                }
                if (!next.MoveNext())
                {
                    next.Dispose();
                    active.Remove(next);
                }
            }
        }
        finally
        {
            foreach (var enumerator in active)
                enumerator.Dispose();
        }
    }
}
