namespace ReBackup.Core.Localization;

/// <summary>
/// A text to be shown in the user's language: the key of a label and its named arguments. Arguments are values that
/// format themselves (numbers, dates; see <see cref="LabelFormat"/>), texts, or other messages (rendered first, in the
/// same language). Two messages are equal when their keys and all their arguments are equal.
/// </summary>
public sealed class Message : IEquatable<Message>
{
    /// <summary>The label that shows a text as it is (e.g. a message from Windows): <c>"raw": "{text}"</c>.</summary>
    public const string RawKey = "raw";

    private static readonly IReadOnlyDictionary<string, object?> NoArgs =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    public Message(string key, IReadOnlyDictionary<string, object?>? args = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
        Args = args ?? NoArgs;
    }

    /// <summary>The dotted label key, e.g. <c>core.plan.nameRequired</c>.</summary>
    public string Key { get; }

    public IReadOnlyDictionary<string, object?> Args { get; }

    public static Message Of(string key, params (string Name, object? Value)[] args)
    {
        if (args.Length == 0)
            return new Message(key);
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in args)
            map[name] = value;
        return new Message(key, map);
    }

    /// <summary>A text shown as it is, in every language.</summary>
    public static Message Raw(string text) => Of(RawKey, ("text", text));

    public bool Equals(Message? other) =>
        other is not null &&
        string.Equals(Key, other.Key, StringComparison.Ordinal) &&
        Args.Count == other.Args.Count &&
        Args.All(pair => other.Args.TryGetValue(pair.Key, out var value) && Equals(pair.Value, value));

    public override bool Equals(object? obj) => Equals(obj as Message);

    public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Key), Args.Count);

    public override string ToString() =>
        Args.Count == 0
            ? Key
            : Key + "(" + string.Join(", ", Args.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + pair.Value)) + ")";
}
