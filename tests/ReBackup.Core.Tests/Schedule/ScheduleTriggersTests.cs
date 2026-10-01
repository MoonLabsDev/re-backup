using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Json;
using ReBackup.Core.Localization;
using ReBackup.Core.Schedule;

namespace ReBackup.Core.Tests.Schedule;

public class ScheduleTriggersTests
{
    private static ScheduleTrigger Daily(string? time) => new() { Type = TriggerType.Daily, Time = time };

    /// <summary>The problem as Core renders it in English.</summary>
    private static string? Problem(ScheduleTrigger? trigger) => ScheduleTriggers.Validate(trigger).ToEnglish();

    [Fact]
    public void Valid_triggers_pass()
    {
        ScheduleTrigger[] triggers =
        [
            Daily("02:00"),
            Daily("7:05"),
            new() { Type = TriggerType.Weekly, Days = ["Mon", "wednesday"], Time = "18:00" },
            new() { Type = TriggerType.Monthly, Day = 31, Time = "03:00" },
            new() { Type = TriggerType.Monthly, Day = 0, Time = "03:00" },
            new() { Type = TriggerType.Monthly, Day = -30, Time = "03:00" },
            new() { Type = TriggerType.Interval, EveryHours = 4, From = "08:00", To = "20:00" },
            new() { Type = TriggerType.Interval, EveryHours = 24 },
        ];

        triggers.Select(Problem).Should().OnlyContain(problem => problem == null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("24:00")]
    [InlineData("2 Uhr")]
    [InlineData("02:60")]
    public void A_time_that_is_not_HH_mm_is_reported(string? time)
    {
        Problem(Daily(time)).Should().Be("the time must be written as HH:mm, for example 02:00.");
    }

    [Fact]
    public void Weekly_needs_valid_weekdays()
    {
        Problem(new ScheduleTrigger { Type = TriggerType.Weekly, Time = "18:00" })
            .Should().Be("choose at least one weekday.");
        Problem(new ScheduleTrigger { Type = TriggerType.Weekly, Days = [], Time = "18:00" })
            .Should().Be("choose at least one weekday.");
        Problem(new ScheduleTrigger { Type = TriggerType.Weekly, Days = ["Mon", "Mo"], Time = "18:00" })
            .Should().Be("\"Mo\" is not a weekday.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(32)]
    [InlineData(-31)]
    public void Monthly_needs_a_day_in_range(int? day)
    {
        Problem(new ScheduleTrigger { Type = TriggerType.Monthly, Day = day, Time = "03:00" })
            .Should().Be("the day must be from 1 to 31, 0 for the last day of the month, or -1 to -30 for days before the last day.");
    }

    [Theory]
    [InlineData(null, "08:00", "20:00", "the interval must be a whole number of hours from 1 to 24.")]
    [InlineData(0, "08:00", "20:00", "the interval must be a whole number of hours from 1 to 24.")]
    [InlineData(25, "08:00", "20:00", "the interval must be a whole number of hours from 1 to 24.")]
    [InlineData(4, "8 Uhr", "20:00", "the start time must be written as HH:mm, for example 08:00.")]
    [InlineData(4, "08:00", "later", "the end time must be written as HH:mm, for example 20:00.")]
    [InlineData(4, "20:00", "08:00", "the start time must not be after the end time.")]
    public void Interval_problems_are_reported(int? everyHours, string? from, string? to, string expected)
    {
        Problem(new ScheduleTrigger { Type = TriggerType.Interval, EveryHours = everyHours, From = from, To = to })
            .Should().Be(expected);
    }

    [Fact]
    public void A_missing_or_unknown_trigger_is_reported()
    {
        Problem(null).Should().Be("the trigger is empty.");
        Problem(new ScheduleTrigger { Type = (TriggerType)42, Time = "02:00" }).Should().Be("the type is unknown.");
    }

    [Fact]
    public void Weekdays_and_interval_window_are_read()
    {
        ScheduleTriggers.WeekdaysOf(new ScheduleTrigger { Type = TriggerType.Weekly, Days = ["Wed", "mon", "Wednesday"] })
            .Should().Equal(DayOfWeek.Wednesday, DayOfWeek.Monday);
        ScheduleTriggers.IntervalWindow(new ScheduleTrigger { Type = TriggerType.Interval, EveryHours = 4 })
            .Should().Be((new TimeOnly(0, 0), new TimeOnly(23, 59)));
        ScheduleTriggers.IntervalWindow(new ScheduleTrigger { Type = TriggerType.Interval, EveryHours = 4, From = "08:00", To = "20:00" })
            .Should().Be((new TimeOnly(8, 0), new TimeOnly(20, 0)));
        ScheduleTriggers.ShortDayNames.Should().Equal("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun");
    }

    [Fact]
    public void Triggers_round_trip_in_the_spec_format()
    {
        var json = """
            [ { "type": "Daily", "time": "02:00" },
              { "type": "Weekly", "days": ["Mon", "Wed"], "time": "18:00" },
              { "type": "Interval", "everyHours": 4, "from": "08:00", "to": "20:00" },
              { "type": "Monthly", "day": 1, "time": "03:00" } ]
            """;

        var triggers = JsonSerializer.Deserialize<List<ScheduleTrigger>>(json, JsonDefaults.Options)!;

        triggers.Select(t => t.Type).Should().Equal(TriggerType.Daily, TriggerType.Weekly, TriggerType.Interval, TriggerType.Monthly);
        triggers[1].Days.Should().Equal("Mon", "Wed");
        triggers[2].EveryHours.Should().Be(4);
        triggers[3].Day.Should().Be(1);

        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(triggers, JsonDefaults.Options));
        saved.RootElement[0].EnumerateObject().Select(p => p.Name).Should().Equal("type", "time");
        saved.RootElement[1].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("type", "time", "days");
        saved.RootElement[2].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("type", "everyHours", "from", "to");
        saved.RootElement[3].GetProperty("day").GetInt32().Should().Be(1);
        saved.RootElement[0].GetProperty("type").GetString().Should().Be("Daily");
    }


    [Fact]
    public void Problems_are_keys_with_arguments()
    {
        ScheduleTriggers.Validate(new ScheduleTrigger { Type = TriggerType.Weekly, Days = ["Mon", "Mo"], Time = "18:00" })
            .Should().Be(Message.Of("core.trigger.notWeekday", ("day", "Mo")));
        ScheduleTriggers.Validate(Daily("25:00")).Should().Be(Message.Of("core.trigger.time"));
        ScheduleTriggers.Validate(Daily("02:00")).Should().BeNull();
    }

    [Fact]
    public void An_invalid_trigger_is_refused_with_the_English_problem()
    {
        FluentActions.Invoking(() => ScheduleCalculator.Occurrences(Daily("later"), DateTime.UtcNow, TimeZoneInfo.Utc).First())
            .Should().Throw<ArgumentException>()
            .WithMessage("The trigger is not valid: the time must be written as HH:mm, for example 02:00.*");
    }
}
