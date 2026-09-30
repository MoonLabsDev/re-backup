using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Retention;

namespace ReBackup.App.ViewModels;

/// <summary>One retention rule of a plan while it is edited.</summary>
public sealed partial class RetentionRuleViewModel : ObservableObject
{
    private readonly bool _ready;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnchorHint))]
    private RetentionPeriod _period;

    [ObservableProperty] private string _anchorText = "";
    [ObservableProperty] private string _keepText = "1";
    [ObservableProperty] private string? _error;

    public RetentionRuleViewModel(RetentionRule rule)
    {
        Period = rule.Period;
        // The weekday list of the view holds full names.
        AnchorText = rule.Period == RetentionPeriod.Weekly && RetentionRules.TryGetWeekday(rule.Anchor, out var day)
            ? day.ToString()
            : rule.Anchor ?? "";
        KeepText = rule.Keep.ToString(CultureInfo.InvariantCulture);
        _ready = true;
        Refresh();
    }

    /// <summary>Raised after every edit of this rule.</summary>
    public event Action? Changed;

    public static IReadOnlyList<RetentionPeriod> Periods { get; } = Enum.GetValues<RetentionPeriod>();

    public static IReadOnlyList<string> Weekdays { get; } =
        ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    public string AnchorHint => Period switch
    {
        RetentionPeriod.Daily => "the last backup of each day",
        RetentionPeriod.Weekly => "the backup of that weekday, otherwise the first one after it",
        RetentionPeriod.Monthly => "day of the month: 1 to 31, 0 = last day, -1 = the day before the last day, …",
        _ => "date as MM-DD, for example 01-01",
    };

    partial void OnPeriodChanged(RetentionPeriod value)
    {
        if (!_ready)
            return;
        AnchorText = value switch
        {
            RetentionPeriod.Weekly => "Sunday",
            RetentionPeriod.Monthly => "1",
            RetentionPeriod.Yearly => "01-01",
            _ => "",
        };
        OnEdited();
    }

    partial void OnAnchorTextChanged(string value) => OnEdited();

    partial void OnKeepTextChanged(string value) => OnEdited();

    public RetentionRule ToRule() => new()
    {
        Period = Period,
        Anchor = Period == RetentionPeriod.Daily || string.IsNullOrWhiteSpace(AnchorText) ? null : AnchorText.Trim(),
        Keep = int.TryParse((KeepText ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var keep) ? keep : 0,
    };

    private void OnEdited()
    {
        if (!_ready)
            return;
        Refresh();
        Changed?.Invoke();
    }

    private void Refresh() => Error = RetentionRules.Validate(ToRule());
}
