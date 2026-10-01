using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace ReBackup.Core.Localization;

/// <summary>
/// One label file: nested JSON objects whose leaves are texts. A label's key is its dotted path,
/// <c>{"ui":{"x":"…"}}</c> → <c>ui.x</c>.
/// </summary>
public sealed class LabelSet
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, string> _entries;

    private LabelSet(Dictionary<string, string> entries) => _entries = entries;

    public IReadOnlyDictionary<string, string> Entries => _entries;

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
            Collect(document.RootElement, "", entries);
            return new LabelSet(entries);
        }
    }

    /// <inheritdoc cref="Parse(string)"/>
    public static LabelSet Parse(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }

    public bool TryGet(string key, [MaybeNullWhen(false)] out string value) => _entries.TryGetValue(key, out value);

    private static void Collect(JsonElement element, string prefix, Dictionary<string, string> entries)
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
                    Collect(property.Value, key + ".", entries);
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
