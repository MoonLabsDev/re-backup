using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Schedule;

namespace ReBackup.App.ViewModels;

/// <summary>A weekday check box of a weekly trigger.</summary>
public sealed partial class WeekdayChoice : ObservableObject
{
    [ObservableProperty] private bool _isChecked;

    public WeekdayChoice(string name, DayOfWeek day)
    {
        Name = name;
        Day = day;
    }

    public string Name { get; }
    public DayOfWeek Day { get; }
}

/// <summary>One trigger of a plan while it is edited. Fields of other trigger types keep their values while hidden.</summary>
public sealed partial class TriggerRowViewModel : ObservableObject
{
    private readonly bool _ready;

    [ObservableProperty] private TriggerType _type;
    [ObservableProperty] private string _timeText = "02:00";
    [ObservableProperty] private string _dayText = "1";
    [ObservableProperty] private string _everyHoursText = "4";
    [ObservableProperty] private string _fromText = "08:00";
    [ObservableProperty] private string _toText = "20:00";
    [ObservableProperty] private string? _error;

    public TriggerRowViewModel(ScheduleTrigger trigger)
    {
        // Mon … Sun; DayOfWeek counts from Sunday.
        Weekdays = ScheduleTriggers.ShortDayNames
            .Select((name, index) => new WeekdayChoice(name, (DayOfWeek)((index + 1) % 7)))
            .ToList();

        // A field the trigger's type uses shows what the trigger holds, empty when it is missing (so the row shows its
        // error instead of a default that was never saved). Fields of the other types keep their defaults, which
        // pre-fill the row when its type is switched.
        Type = trigger.Type;
        var usesTime = trigger.Type is TriggerType.Daily or TriggerType.Weekly or TriggerType.Monthly;
        if (usesTime || trigger.Time is not null)
            TimeText = trigger.Time ?? "";
        var chosen = ScheduleTriggers.WeekdaysOf(trigger);
        foreach (var choice in Weekdays)
        {
            choice.IsChecked = chosen.Contains(choice.Day);
            choice.PropertyChanged += (_, _) => OnEdited();
        }
        if (trigger.Type == TriggerType.Monthly || trigger.Day is not null)
            DayText = trigger.Day?.ToString(CultureInfo.InvariantCulture) ?? "";
        if (trigger.Type == TriggerType.Interval)
        {
            EveryHoursText = trigger.EveryHours?.ToString(CultureInfo.InvariantCulture) ?? "";
            // Empty bounds mean the whole day; showing the defaults of a new row would change the trigger.
            FromText = trigger.From ?? "";
            ToText = trigger.To ?? "";
        }
        else if (trigger.EveryHours is { } hours)
        {
            EveryHoursText = hours.ToString(CultureInfo.InvariantCulture);
        }

        _ready = true;
        Error = ScheduleTriggers.Validate(ToTrigger());
    }

    /// <summary>Raised after every edit of this trigger.</summary>
    public event Action? Changed;

    public static IReadOnlyList<TriggerType> Types { get; } = Enum.GetValues<TriggerType>();

    public IReadOnlyList<WeekdayChoice> Weekdays { get; }

    partial void OnTypeChanged(TriggerType value) => OnEdited();
    partial void OnTimeTextChanged(string value) => OnEdited();
    partial void OnDayTextChanged(string value) => OnEdited();
    partial void OnEveryHoursTextChanged(string value) => OnEdited();
    partial void OnFromTextChanged(string value) => OnEdited();
    partial void OnToTextChanged(string value) => OnEdited();

    /// <summary>The trigger as edited, with only the fields its type uses.</summary>
    public ScheduleTrigger ToTrigger() => Type switch
    {
        TriggerType.Daily => new ScheduleTrigger { Type = Type, Time = Trimmed(TimeText) },
        TriggerType.Weekly => new ScheduleTrigger
        {
            Type = Type,
            Time = Trimmed(TimeText),
            Days = Weekdays.Where(w => w.IsChecked).Select(w => w.Name).ToList(),
        },
        TriggerType.Monthly => new ScheduleTrigger { Type = Type, Time = Trimmed(TimeText), Day = ParseNumber(DayText, NumberStyles.AllowLeadingSign) },
        _ => new ScheduleTrigger
        {
            Type = Type,
            EveryHours = ParseNumber(EveryHoursText, NumberStyles.None),
            From = Trimmed(FromText),
            To = Trimmed(ToText),
        },
    };

    private void OnEdited()
    {
        if (!_ready)
            return;
        Error = ScheduleTriggers.Validate(ToTrigger());
        Changed?.Invoke();
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static int? ParseNumber(string? text, NumberStyles styles) =>
        int.TryParse(text?.Trim(), styles, CultureInfo.InvariantCulture, out var value) ? value : null;
}
