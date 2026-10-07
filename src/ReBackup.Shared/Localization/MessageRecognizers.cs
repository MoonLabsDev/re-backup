namespace ReBackup.Shared.Localization;

/// <summary>
/// Recognizes a stored English text through every registered recognizer (e.g. <c>CoreTexts.Recognize</c>,
/// <see cref="SharedTexts.Recognize"/>). A recognizer knows only its own templates, so a message that nests another
/// library's text ("Retention rule 2: keep must be a number from 1 to 100.", Core around Shared) is finished here: the
/// text arguments of the recognized message are recognized through the whole chain again.
/// </summary>
public static class MessageRecognizers
{
    /// <summary>Guards against a recognizer whose argument is its own text; real messages nest two or three levels.</summary>
    private const int MaxDepth = 8;

    /// <summary>
    /// The message <paramref name="text"/> came from: the first recognizer that knows it decides, then every text
    /// argument is recognized the same way (and stays text when no recognizer knows it). Null when none knows the text.
    /// </summary>
    public static Message? Recognize(string? text, IReadOnlyList<Func<string, Message?>> recognizers) =>
        Recognize(text, recognizers, 0);

    private static Message? Recognize(string? text, IReadOnlyList<Func<string, Message?>> recognizers, int depth)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        foreach (var recognizer in recognizers)
        {
            if (recognizer(text) is { } message)
                return depth < MaxDepth ? Resolve(message, recognizers, depth + 1) : message;
        }
        return null;
    }

    /// <summary>The message with its text arguments (and those of nested messages) recognized.</summary>
    private static Message Resolve(Message message, IReadOnlyList<Func<string, Message?>> recognizers, int depth)
    {
        Dictionary<string, object?>? resolved = null;
        foreach (var (name, value) in message.Args)
        {
            var replacement = value switch
            {
                string inner => (object?)Recognize(inner, recognizers, depth) ?? inner,
                Message nested => Resolve(nested, recognizers, depth),
                _ => value,
            };
            if (ReferenceEquals(replacement, value))
                continue;
            resolved ??= new Dictionary<string, object?>(message.Args, StringComparer.Ordinal);
            resolved[name] = replacement;
        }
        return resolved is null ? message : new Message(message.Key, resolved);
    }
}
