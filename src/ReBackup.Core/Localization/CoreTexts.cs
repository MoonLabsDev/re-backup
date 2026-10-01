using System.Globalization;

namespace ReBackup.Core.Localization;

/// <summary>
/// The English texts of the messages Core produces. The App shows messages in the chosen language from its label files
/// (en-US.json holds exactly these texts; a test checks it). Core needs English itself for what it writes to disk (the
/// run log stays English) and for exception messages.
/// </summary>
public static class CoreTexts
{
    public static IReadOnlyDictionary<string, string> Templates { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["core.plan.nameRequired"] = "Name is required.",
        ["core.plan.nameSpaces"] = "Name must not start or end with spaces.",
        ["core.plan.nameDot"] = "Name must not end with a dot.",
        ["core.plan.namePartial"] = "Name must not end with \".partial\".",
        ["core.plan.nameDeleting"] = "Name must not end with \".deleting\".",
        ["core.plan.nameChars"] = "Name contains characters that are not allowed in folder names.",
        ["core.plan.nameTaken"] = "Another plan is already named \"{name}\".",
        ["core.plan.sourceRequired"] = "Source folder is required.",
        ["core.plan.targetRequired"] = "Target folder is required.",
        ["core.plan.sourceNotAbsolute"] = "Source must be an absolute path.",
        ["core.plan.targetNotAbsolute"] = "Target must be an absolute path.",
        ["core.plan.sourceMissing"] = "Source folder does not exist.",
        ["core.plan.targetInsideSource"] = "Target must not be inside the source.",
        ["core.plan.sourceInsideTarget"] = "Source must not be inside the target.",
        ["core.plan.retentionRule"] = "Retention rule {index}: {problem}",
        ["core.plan.trigger"] = "Trigger {index}: {problem}",

        ["core.trigger.empty"] = "the trigger is empty.",
        ["core.trigger.unknownType"] = "the type is unknown.",
        ["core.trigger.time"] = "the time must be written as HH:mm, for example 02:00.",
        ["core.trigger.noWeekday"] = "choose at least one weekday.",
        ["core.trigger.notWeekday"] = "\"{day}\" is not a weekday.",
        ["core.trigger.monthDay"] =
            "the day must be from 1 to 31, 0 for the last day of the month, or -1 to -30 for days before the last day.",
        ["core.trigger.interval"] = "the interval must be a whole number of hours from 1 to 24.",
        ["core.trigger.from"] = "the start time must be written as HH:mm, for example 08:00.",
        ["core.trigger.to"] = "the end time must be written as HH:mm, for example 20:00.",
        ["core.trigger.fromAfterTo"] = "the start time must not be after the end time.",
        ["core.trigger.invalid"] = "The trigger is not valid: {problem}",

        ["core.retention.empty"] = "the rule is empty.",
        ["core.retention.unknownPeriod"] = "the period is unknown.",
        ["core.retention.keep"] = "keep must be a number from 1 to {max}.",
        ["core.retention.weekday"] = "the anchor must be a weekday, for example Sunday.",
        ["core.retention.monthDay"] =
            "the anchor must be a day from 1 to 31, 0 for the last day of the month, or -1 to -30 for days before the last day.",
        ["core.retention.yearDate"] = "the anchor must be a date written as MM-DD, for example 01-01.",
        ["core.retention.invalid"] = "The retention rule is not valid: {problem}",

        ["core.run.nameUnusable"] = "The plan name \"{name}\" cannot be used: {problem}",
    };

    /// <summary>The message in English; nested messages too. An unknown key is shown as the key.</summary>
    public static string English(Message message) =>
        LabelFormat.Format(Templates.TryGetValue(message.Key, out var template) ? template : message.Key,
            message.Args, CultureInfo.InvariantCulture, English);

    public static string English(string key, params (string Name, object? Value)[] args) => English(Message.Of(key, args));

    /// <summary><see cref="English(Message)"/>, or null for no message.</summary>
    public static string? ToEnglish(this Message? message) => message is null ? null : English(message);
}
