using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace ReBackup.Core.Localization;

/// <summary>
/// Label templates with named placeholders: <c>{name}</c>, or <c>{name:format}</c> for an argument that formats itself
/// (numbers, dates) with the language's culture, e.g. <c>{count:N0}</c>. <c>{{</c> and <c>}}</c> are literal braces.
/// A name starts with an ASCII letter and holds ASCII letters and digits only; anything else in braces is text.
/// </summary>
public static class LabelFormat
{
    private static readonly ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.Ordinal);

    /// <summary>
    /// The template with its placeholders replaced. A missing argument stays as written (a visible gap, not a crash);
    /// null is empty; a <see cref="Message"/> is rendered by <paramref name="render"/> (its key without one).
    /// </summary>
    public static string Format(string template, IReadOnlyDictionary<string, object?> args, IFormatProvider provider,
        Func<Message, string>? render = null)
    {
        var builder = new StringBuilder(template.Length + 32);
        foreach (var token in Tokenize(template))
        {
            if (!token.IsPlaceholder)
                builder.Append(token.Text);
            else if (args.TryGetValue(token.Text, out var value))
                builder.Append(FormatValue(value, token.Format, provider, render));
            else
                builder.Append('{').Append(token.Text).Append(token.Format is null ? "" : ":" + token.Format).Append('}');
        }
        return builder.ToString();
    }

    /// <summary>The placeholder names of a template, once each, in order of first appearance.</summary>
    public static IReadOnlyList<string> Placeholders(string template) =>
        Tokenize(template).Where(token => token.IsPlaceholder).Select(token => token.Text)
            .Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Reads the arguments back out of a text that <see cref="Format"/> produced from <paramref name="template"/>
    /// (as text); null when the text does not have the template's shape.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Match(string template, string text)
    {
        var match = Patterns.GetOrAdd(template, BuildPattern).Match(text);
        if (!match.Success)
            return null;
        return Placeholders(template).ToDictionary(name => name, name => match.Groups[name].Value, StringComparer.Ordinal);
    }

    /// <summary>The number of characters outside placeholders (an escaped brace counts once).</summary>
    public static int LiteralLength(string template) =>
        Tokenize(template).Where(token => !token.IsPlaceholder).Sum(token => token.Text.Length);

    private static string FormatValue(object? value, string? format, IFormatProvider provider, Func<Message, string>? render) =>
        value switch
        {
            null => "",
            Message message => render is null ? message.Key : render(message),
            string text => text,
            IFormattable formattable => formattable.ToString(format, provider),
            _ => value.ToString() ?? "",
        };

    private static Regex BuildPattern(string template)
    {
        var pattern = new StringBuilder("^");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Tokenize(template))
        {
            if (!token.IsPlaceholder)
                pattern.Append(Regex.Escape(token.Text));
            else if (seen.Add(token.Text))
                pattern.Append("(?<").Append(token.Text).Append(">.*?)");
            else
                pattern.Append(@"\k<").Append(token.Text).Append('>');
        }
        pattern.Append(@"\z");
        return new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    private readonly record struct Token(bool IsPlaceholder, string Text, string? Format);

    private static List<Token> Tokenize(string template)
    {
        var tokens = new List<Token>();
        var literal = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c is '{' or '}' && i + 1 < template.Length && template[i + 1] == c)
            {
                literal.Append(c);
                i++;
                continue;
            }
            if (c == '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (end > i + 1)
                {
                    var body = template.Substring(i + 1, end - i - 1);
                    var colon = body.IndexOf(':');
                    var name = colon < 0 ? body : body[..colon];
                    if (IsName(name))
                    {
                        if (literal.Length > 0)
                        {
                            tokens.Add(new Token(false, literal.ToString(), null));
                            literal.Clear();
                        }
                        tokens.Add(new Token(true, name, colon < 0 ? null : body[(colon + 1)..]));
                        i = end;
                        continue;
                    }
                }
            }
            literal.Append(c);
        }
        if (literal.Length > 0)
            tokens.Add(new Token(false, literal.ToString(), null));
        return tokens;
    }

    private static bool IsName(string text) =>
        text.Length > 0 && char.IsAsciiLetter(text[0]) && text.All(char.IsAsciiLetterOrDigit);
}
