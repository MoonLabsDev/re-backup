using System.Text.Json.Serialization;
using ReBackup.Shared.Settings;

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

    /// <summary>"de-DE" or "en-US"; null until the user chooses one (then the Windows language decides).</summary>
    [JsonConverter(typeof(LanguageConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }

    /// <summary>
    /// The plan ids in the order of the plan list; plans it does not name come after, by name (see
    /// <see cref="Plans.PlanOrder"/>). Lives in the configuration folder, so it moves and syncs with the plans.
    /// </summary>
    public List<string> PlanOrder { get; set; } = [];
}
