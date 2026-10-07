using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReBackup.Shared.Settings;

/// <summary>The app's colour theme: dark (the default) or light.</summary>
public enum ThemeMode { Dark, Light }

/// <summary>
/// Writes the mode as text like the other enums; reads it leniently — an unknown (also the retired "System"), empty or non-text value is
/// <see cref="ThemeMode.Dark"/>, so a bad theme never discards the rest of settings.json. Set on the property:
/// a converter in the serializer options would win over one on the type.
/// </summary>
public sealed class ThemeModeConverter : JsonConverter<ThemeMode>
{
    public override bool HandleNull => true;

    public override ThemeMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse<ThemeMode>(reader.GetString(), ignoreCase: true, out var mode)
            && Enum.IsDefined(mode)
            && !int.TryParse(reader.GetString(), out _))
            return mode;

        reader.Skip();
        return ThemeMode.Dark;
    }

    public override void Write(Utf8JsonWriter writer, ThemeMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
