using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Json;
using ReBackup.Core.Localization;
using ReBackup.Core.Retention;

namespace ReBackup.Core.Tests.Retention;

public class RetentionRulesTests
{
    private static RetentionRule Rule(RetentionPeriod period, string? anchor = null, int keep = 1) =>
        new() { Period = period, Anchor = anchor, Keep = keep };

    private static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The problem as Core renders it in English.</summary>
    private static string? Problem(RetentionRule? rule) => RetentionRules.Validate(rule).ToEnglish();

    [Theory]
    [InlineData(RetentionPeriod.Daily, null)]
    [InlineData(RetentionPeriod.Weekly, "Sunday")]
    [InlineData(RetentionPeriod.Weekly, "wed")]
    [InlineData(RetentionPeriod.Monthly, "1")]
    [InlineData(RetentionPeriod.Monthly, "31")]
    [InlineData(RetentionPeriod.Monthly, "0")]
    [InlineData(RetentionPeriod.Monthly, "-30")]
    [InlineData(RetentionPeriod.Yearly, "01-01")]
    [InlineData(RetentionPeriod.Yearly, "02-29")]
    public void Valid_rules_pass(RetentionPeriod period, string? anchor)
    {
        Problem(Rule(period, anchor)).Should().BeNull();
    }

    [Theory]
    [InlineData(RetentionPeriod.Weekly, null)]
    [InlineData(RetentionPeriod.Weekly, "Sonntag")]
    [InlineData(RetentionPeriod.Weekly, "3")]
    [InlineData(RetentionPeriod.Monthly, null)]
    [InlineData(RetentionPeriod.Monthly, "32")]
    [InlineData(RetentionPeriod.Monthly, "-31")]
    [InlineData(RetentionPeriod.Monthly, "first")]
    [InlineData(RetentionPeriod.Yearly, null)]
    [InlineData(RetentionPeriod.Yearly, "1-1")]
    [InlineData(RetentionPeriod.Yearly, "13-01")]
    [InlineData(RetentionPeriod.Yearly, "02-30")]
    [InlineData(RetentionPeriod.Yearly, "00-10")]
    public void Invalid_anchors_are_reported(RetentionPeriod period, string? anchor)
    {
        Problem(Rule(period, anchor)).Should().Contain("anchor");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10000)]
    public void Keep_must_be_between_1_and_9999(int keep)
    {
        Problem(Rule(RetentionPeriod.Daily, null, keep)).Should().Be("keep must be a number from 1 to 9999.");
    }

    [Fact]
    public void A_missing_rule_is_reported()
    {
        Problem(null).Should().Be("the rule is empty.");
    }

    [Theory]
    [InlineData(RetentionPeriod.Daily, null, "Daily")]
    [InlineData(RetentionPeriod.Weekly, "Sunday", "Weekly(Sun)")]
    [InlineData(RetentionPeriod.Weekly, "wed", "Weekly(Wed)")]
    [InlineData(RetentionPeriod.Monthly, "0", "Monthly(0)")]
    [InlineData(RetentionPeriod.Monthly, "-1", "Monthly(-1)")]
    [InlineData(RetentionPeriod.Monthly, "14", "Monthly(14)")]
    [InlineData(RetentionPeriod.Yearly, "01-01", "Yearly(01-01)")]
    [InlineData(RetentionPeriod.Weekly, "Someday", "Weekly(Someday)")]
    public void Describe_gives_a_short_label(RetentionPeriod period, string? anchor, string expected)
    {
        RetentionRules.Describe(Rule(period, anchor)).Should().Be(expected);
    }

    [Theory]
    [InlineData(2026, 2, 1, "2026-02-01")]
    [InlineData(2026, 2, 31, "2026-02-28")]
    [InlineData(2024, 2, 31, "2024-02-29")]
    [InlineData(2026, 9, 0, "2026-09-30")]
    [InlineData(2026, 9, -1, "2026-09-29")]
    [InlineData(2026, 2, -30, "2026-02-01")]
    public void Month_anchor_is_clamped_to_the_month(int year, int month, int anchor, string expected)
    {
        RetentionRules.MonthAnchor(year, month, anchor).Should().Be(D(expected));
    }

    // 2026-09-27 is a Sunday, 2026-09-30 a Wednesday.
    [Theory]
    [InlineData(RetentionPeriod.Daily, null, "2026-09-30", "2026-09-30")]
    [InlineData(RetentionPeriod.Weekly, "Sunday", "2026-09-30", "2026-09-27")]
    [InlineData(RetentionPeriod.Weekly, "Sunday", "2026-09-27", "2026-09-27")]
    [InlineData(RetentionPeriod.Weekly, "Sunday", "2026-09-26", "2026-09-20")]
    [InlineData(RetentionPeriod.Weekly, "Wednesday", "2026-09-30", "2026-09-30")]
    [InlineData(RetentionPeriod.Weekly, "Wednesday", "2026-09-29", "2026-09-23")]
    [InlineData(RetentionPeriod.Monthly, "1", "2026-09-30", "2026-09-01")]
    [InlineData(RetentionPeriod.Monthly, "1", "2026-09-01", "2026-09-01")]
    [InlineData(RetentionPeriod.Monthly, "15", "2026-09-14", "2026-08-15")]
    [InlineData(RetentionPeriod.Monthly, "31", "2026-02-28", "2026-02-28")]
    [InlineData(RetentionPeriod.Monthly, "31", "2026-02-27", "2026-01-31")]
    [InlineData(RetentionPeriod.Monthly, "31", "2026-03-30", "2026-02-28")]
    [InlineData(RetentionPeriod.Monthly, "31", "2024-02-29", "2024-02-29")]
    [InlineData(RetentionPeriod.Monthly, "0", "2026-09-30", "2026-09-30")]
    [InlineData(RetentionPeriod.Monthly, "0", "2026-09-29", "2026-08-31")]
    [InlineData(RetentionPeriod.Monthly, "-1", "2026-09-29", "2026-09-29")]
    [InlineData(RetentionPeriod.Monthly, "-1", "2026-09-28", "2026-08-30")]
    [InlineData(RetentionPeriod.Monthly, "1", "2026-01-01", "2026-01-01")]
    [InlineData(RetentionPeriod.Monthly, "15", "2026-01-10", "2025-12-15")]
    [InlineData(RetentionPeriod.Yearly, "01-01", "2026-09-30", "2026-01-01")]
    [InlineData(RetentionPeriod.Yearly, "03-15", "2026-03-14", "2025-03-15")]
    [InlineData(RetentionPeriod.Yearly, "03-15", "2026-03-15", "2026-03-15")]
    [InlineData(RetentionPeriod.Yearly, "02-29", "2026-03-01", "2026-02-28")]
    [InlineData(RetentionPeriod.Yearly, "02-29", "2024-02-29", "2024-02-29")]
    [InlineData(RetentionPeriod.Yearly, "02-29", "2025-02-27", "2024-02-29")]
    public void Slot_start_is_the_anchor_day_at_or_before_the_date(RetentionPeriod period, string? anchor, string date, string expected)
    {
        RetentionRules.SlotStart(Rule(period, anchor), D(date)).Should().Be(D(expected));
    }

    [Fact]
    public void Slot_start_of_an_invalid_rule_throws()
    {
        var act = () => RetentionRules.SlotStart(Rule(RetentionPeriod.Weekly, "Someday"), D("2026-09-30"));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rules_round_trip_with_numbers_for_day_anchors()
    {
        var json = """
            [ { "period": "Daily", "keep": 7 },
              { "period": "Weekly", "anchor": "Sunday", "keep": 4 },
              { "period": "Monthly", "anchor": 0, "keep": 12 },
              { "period": "Monthly", "anchor": -1, "keep": 2 },
              { "period": "Yearly", "anchor": "01-01", "keep": 3 } ]
            """;

        var rules = JsonSerializer.Deserialize<List<RetentionRule>>(json, JsonDefaults.Options)!;

        rules.Select(r => r.Period).Should().Equal(RetentionPeriod.Daily, RetentionPeriod.Weekly,
            RetentionPeriod.Monthly, RetentionPeriod.Monthly, RetentionPeriod.Yearly);
        rules.Select(r => r.Anchor).Should().Equal(new string?[] { null, "Sunday", "0", "-1", "01-01" });
        rules.Select(r => r.Keep).Should().Equal(7, 4, 12, 2, 3);

        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(rules, JsonDefaults.Options));
        saved.RootElement[0].TryGetProperty("anchor", out _).Should().BeFalse("a daily rule has no anchor");
        saved.RootElement[1].GetProperty("anchor").GetString().Should().Be("Sunday");
        saved.RootElement[2].GetProperty("anchor").GetInt32().Should().Be(0);
        saved.RootElement[3].GetProperty("anchor").GetInt32().Should().Be(-1);
        saved.RootElement[4].GetProperty("anchor").GetString().Should().Be("01-01");
        saved.RootElement[2].GetProperty("period").GetString().Should().Be("Monthly");
        saved.RootElement[2].GetProperty("keep").GetInt32().Should().Be(12);
    }

    [Fact]
    public void An_anchor_that_is_neither_text_nor_a_whole_number_is_rejected()
    {
        var act = () => JsonSerializer.Deserialize<RetentionRule>("""{ "period": "Monthly", "anchor": true, "keep": 1 }""",
            JsonDefaults.Options);
        act.Should().Throw<JsonException>();
    }


    [Fact]
    public void Problems_are_keys_with_arguments()
    {
        RetentionRules.Validate(Rule(RetentionPeriod.Daily, null, 0)).Should().Be(Message.Of("core.retention.keep", ("max", 9999)));
        RetentionRules.Validate(Rule(RetentionPeriod.Weekly, "Sonntag")).Should().Be(Message.Of("core.retention.weekday"));
    }
}
