using System.Globalization;

namespace ReBackup.Shared.Localization;

/// <summary>The labels of one language: that language's text, else the English one, else the key itself.</summary>
public sealed class Labels
{
    private readonly LabelSet _english;
    private readonly LabelSet _chosen;

    public Labels(LabelSet english, LabelSet chosen, CultureInfo culture)
    {
        _english = english;
        _chosen = chosen;
        Culture = culture;
    }

    /// <summary>Formats numbers and dates in arguments.</summary>
    public CultureInfo Culture { get; }

    public string Get(string key) =>
        _chosen.TryGet(key, out var text) || _english.TryGet(key, out text) ? text : key;

    /// <summary>
    /// The label with its placeholders filled; a plural label in the form its <c>count</c> argument chooses (from the
    /// chosen language when it has the label, else from English).
    /// </summary>
    public string Format(string key, IReadOnlyDictionary<string, object?> args)
    {
        args.TryGetValue("count", out var count);
        var template = _chosen.TryGet(key, count, out var text) || _english.TryGet(key, count, out text) ? text : key;
        return LabelFormat.Format(template, args, Culture, Format);
    }

    public string Format(Message message) => Format(message.Key, message.Args);
}
