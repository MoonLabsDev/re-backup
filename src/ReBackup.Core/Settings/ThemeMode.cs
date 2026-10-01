using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReBackup.Core.Settings;

/// <summary>The app's colour theme: follow the Windows app mode, or always dark, or always light.</summary>
public enum ThemeMode { System, Dark, Light }

/// <summary>
/// Writes the mode as text like the other enums; reads it leniently — an unknown, empty or non-text value is
/// <see cref="ThemeMode.System"/>, so a bad theme never discards the rest of settings.json. Set on the property:
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
        return ThemeMode.System;
    }

    public override void Write(Utf8JsonWriter writer, ThemeMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
