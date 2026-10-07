using System.Text.Json.Serialization;

namespace ReBackup.Shared.Schedule;

public enum TriggerType { Daily, Weekly, Monthly, Interval }

/// <summary>One entry of a plan's "triggers" list. Which fields are used depends on <see cref="Type"/>.</summary>
public sealed class ScheduleTrigger
{
    public TriggerType Type { get; set; }

    /// <summary>Daily, Weekly, Monthly: local time of day, "HH:mm".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Time { get; set; }

    /// <summary>Weekly: the weekdays, written "Mon" … "Sun".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Days { get; set; }

    /// <summary>Monthly: 1..31 (at most the last day), 0 = last day, -n = n days before the last day.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Day { get; set; }

    /// <summary>Interval: hours between two runs, 1..24.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EveryHours { get; set; }

    /// <summary>Interval: first run of the day, "HH:mm"; 00:00 when missing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; set; }

    /// <summary>Interval: no run later in the day than this, "HH:mm"; 23:59 when missing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? To { get; set; }
}
