using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReBackup.Core.Settings;

/// <summary>The languages of the app and how the one to use is chosen.</summary>
public static class AppLanguages
{
    public const string English = "en-US";
    public const string German = "de-DE";

    public static IReadOnlyList<string> Supported { get; } = [German, English];

    /// <summary>The supported tag written like <paramref name="tag"/> (case and surrounding spaces aside); null otherwise.</summary>
    public static string? Normalize(string? tag)
    {
        var trimmed = tag?.Trim();
        return Supported.FirstOrDefault(language => string.Equals(language, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>German when Windows speaks German (any region), otherwise English.</summary>
    public static string DefaultFor(CultureInfo uiCulture) =>
        uiCulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase) ? German : English;

    /// <summary>The stored choice when it is a supported language, otherwise the default for Windows' language.</summary>
    public static string Resolve(string? stored, CultureInfo uiCulture) => Normalize(stored) ?? DefaultFor(uiCulture);
}

/// <summary>
/// Reads the language leniently: a supported tag (any case) is kept, anything else (unknown, empty, non-text) is null,
/// so a bad value never discards the rest of settings.json. Set on the property, like <see cref="ThemeModeConverter"/>.
/// </summary>
public sealed class LanguageConverter : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return AppLanguages.Normalize(reader.GetString());

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
