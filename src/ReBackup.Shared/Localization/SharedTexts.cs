using System.Globalization;
using System.Text.RegularExpressions;

namespace ReBackup.Shared.Localization;

/// <summary>
/// The English texts of the messages the shared library produces (retention rules, schedule triggers). The App shows
/// them in the chosen language from the label files of this library (Locales/shared.*.json; shared.en-US.json holds
/// exactly these texts, a test checks it). It needs English itself for exception messages. <see cref="Recognize"/> turns
/// such an English text back into its message, so the App can show it in the chosen language.
/// </summary>
public static class SharedTexts
{
    public static IReadOnlyDictionary<string, string> Templates { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["shared.trigger.empty"] = "the trigger is empty.",
        ["shared.trigger.unknownType"] = "the type is unknown.",
        ["shared.trigger.time"] = "the time must be written as HH:mm, for example 02:00.",
        ["shared.trigger.noWeekday"] = "choose at least one weekday.",
        ["shared.trigger.notWeekday"] = "\"{day}\" is not a weekday.",
        ["shared.trigger.monthDay"] =
            "the day must be from 1 to 31, 0 for the last day of the month, or -1 to -30 for days before the last day.",
        ["shared.trigger.interval"] = "the interval must be a whole number of hours from 1 to 24.",
        ["shared.trigger.from"] = "the start time must be written as HH:mm, for example 08:00.",
        ["shared.trigger.to"] = "the end time must be written as HH:mm, for example 20:00.",
        ["shared.trigger.fromAfterTo"] = "the start time must not be after the end time.",
        ["shared.trigger.invalid"] = "The trigger is not valid: {problem}",

        ["shared.retention.empty"] = "the rule is empty.",
        ["shared.retention.unknownPeriod"] = "the period is unknown.",
        ["shared.retention.keep"] = "keep must be a number from 1 to {max}.",
        ["shared.retention.weekday"] = "the anchor must be a weekday, for example Sunday.",
        ["shared.retention.monthDay"] =
            "the anchor must be a day from 1 to 31, 0 for the last day of the month, or -1 to -30 for days before the last day.",
        ["shared.retention.yearDate"] = "the anchor must be a date written as MM-DD, for example 01-01.",
        ["shared.retention.rule"] = "Retention rule {index}: {problem}",
        ["shared.retention.invalid"] = "The retention rule is not valid: {problem}",
    };

    /// <summary>The message in English; nested messages too. An unknown key is shown as the key.</summary>
    public static string English(Message message) =>
        LabelFormat.Format(Templates.TryGetValue(message.Key, out var template) ? template : message.Key,
            message.Args, CultureInfo.InvariantCulture, English);

    public static string English(string key, params (string Name, object? Value)[] args) => English(Message.Of(key, args));

    private static readonly Regex ParameterSuffix = new(@" \(Parameter '[^']*'\)\z", RegexOptions.CultureInvariant);

    /// <summary>The templates <see cref="Recognize"/> tries, the most literal text first (the most specific).</summary>
    private static readonly Lazy<string[]> RecognitionOrder = new(() => Templates
        .Where(pair => LabelFormat.LiteralLength(pair.Value) > 0)
        .OrderByDescending(pair => LabelFormat.LiteralLength(pair.Value))
        .ThenBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => pair.Key)
        .ToArray());

    /// <summary>
    /// The message an English text the library produced came from, its text arguments recognized as well; null when no
    /// template matches (e.g. a message from Windows). The <c> (Parameter '…')</c> an <see cref="ArgumentException"/>
    /// appends is ignored.
    /// </summary>
    public static Message? Recognize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        if (MatchTemplate(text) is { } message)
            return message;
        var suffix = ParameterSuffix.Match(text);
        return suffix.Success ? MatchTemplate(text[..suffix.Index]) : null;
    }

    private static Message? MatchTemplate(string text)
    {
        foreach (var key in RecognitionOrder.Value)
        {
            if (LabelFormat.Match(Templates[key], text) is not { } args)
                continue;
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in args)
                values[name] = Recognize(value) is { } inner ? inner : value;
            return new Message(key, values);
        }
        return null;
    }
}
