using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace ReBackup.Shared.Localization;

/// <summary>
/// One label file: nested JSON objects whose leaves are texts. A label's key is its dotted path,
/// <c>{"ui":{"x":"…"}}</c> → <c>ui.x</c>. An object whose members are exactly the texts <c>"one"</c> and
/// <c>"other"</c> is a plural label: <c>{"files":{"one":"{count} file","other":"{count:N0} files"}}</c> is the label
/// <c>files</c>, whose form the <c>count</c> argument chooses (see <see cref="LabelFormat.IsOne"/>); its forms are also
/// listed in <see cref="Entries"/> as <c>files.one</c> and <c>files.other</c>.
/// </summary>
public sealed class LabelSet
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public const string One = "one";
    public const string Other = "other";

    private readonly Dictionary<string, string> _entries;
    private readonly HashSet<string> _plurals;

    private LabelSet(Dictionary<string, string> entries, HashSet<string> plurals)
    {
        _entries = entries;
        _plurals = plurals;
    }

    /// <summary>Every text by its dotted key; a plural label's forms as <c>key.one</c> and <c>key.other</c>.</summary>
    public IReadOnlyDictionary<string, string> Entries => _entries;

    /// <summary>The keys of the plural labels.</summary>
    public IReadOnlySet<string> Plurals => _plurals;

    /// <exception cref="FormatException">
    /// Not valid JSON, not an object, a leaf that is not a text, or a key that is empty, holds a dot or appears twice.
    /// </exception>
    public static LabelSet Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The label file is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("A label file holds one JSON object.");
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            var plurals = new HashSet<string>(StringComparer.Ordinal);
            Collect(document.RootElement, "", entries, plurals);
            return new LabelSet(entries, plurals);
        }
    }

    /// <inheritdoc cref="Parse(string)"/>
    public static LabelSet Parse(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>The labels of both sets; on a key in both, <paramref name="other"/> wins.</summary>
    public LabelSet Merge(LabelSet other)
    {
        var entries = new Dictionary<string, string>(_entries, StringComparer.Ordinal);
        foreach (var (key, value) in other._entries)
            entries[key] = value;
        var plurals = new HashSet<string>(_plurals, StringComparer.Ordinal);
        plurals.UnionWith(other._plurals);
        return new LabelSet(entries, plurals);
    }

    public bool IsPlural(string key) => _plurals.Contains(key);

    /// <summary>The text of <paramref name="key"/>; for a plural label its "other" form.</summary>
    public bool TryGet(string key, [MaybeNullWhen(false)] out string value) =>
        _plurals.Contains(key) ? _entries.TryGetValue(key + "." + Other, out value) : _entries.TryGetValue(key, out value);

    /// <summary>The text of <paramref name="key"/>; for a plural label the form <paramref name="count"/> chooses.</summary>
    public bool TryGet(string key, object? count, [MaybeNullWhen(false)] out string value) =>
        _plurals.Contains(key)
            ? _entries.TryGetValue(key + "." + (LabelFormat.IsOne(count) ? One : Other), out value)
            : _entries.TryGetValue(key, out value);

    private static bool IsPluralObject(JsonElement element)
    {
        var names = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                return false;
            names.Add(property.Name);
        }
        return names.Count == 2 && names.Contains(One) && names.Contains(Other);
    }

    private static void Collect(JsonElement element, string prefix, Dictionary<string, string> entries,
        HashSet<string> plurals)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var key = prefix + property.Name;
            if (property.Name.Length == 0 || property.Name.Contains('.'))
                throw new FormatException($"\"{key}\": a key must not be empty or hold a dot.");
            if (!names.Add(property.Name))
                throw new FormatException($"\"{key}\" appears twice.");

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (IsPluralObject(property.Value))
                        plurals.Add(key);
                    Collect(property.Value, key + ".", entries, plurals);
                    break;
                case JsonValueKind.String:
                    entries[key] = property.Value.GetString()!;
                    break;
                default:
                    throw new FormatException($"\"{key}\" must be a text or an object.");
            }
        }
    }
}
