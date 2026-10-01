using System.Text.Json.Serialization;

namespace ReBackup.Core.Settings;

public sealed class AppSettings
{
    public static readonly IReadOnlyList<string> BuiltInIgnoreDefaults =
        ["Thumbs.db", "desktop.ini", "$RECYCLE.BIN/", "System Volume Information/"];

    public List<string> DefaultIgnorePatterns { get; set; } = [.. BuiltInIgnoreDefaults];
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    [JsonConverter(typeof(ThemeModeConverter))]
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
}
