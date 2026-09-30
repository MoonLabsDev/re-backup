using System.Text.Json;
using ReBackup.Core.IO;
using ReBackup.Core.Json;

namespace ReBackup.Core.Settings;

public sealed class SettingsStore
{
    public SettingsStore(string settingsFile) => SettingsFile = settingsFile;

    public string SettingsFile { get; }

    /// <summary>Set when the last <see cref="Load"/> found a corrupt or unreadable file and fell back to defaults.</summary>
    public string? LastLoadError { get; private set; }

    public AppSettings Load()
    {
        LastLoadError = null;
        if (!File.Exists(SettingsFile))
            return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonDefaults.Options)
                ?? new AppSettings();
            settings.DefaultIgnorePatterns ??= [.. AppSettings.BuiltInIgnoreDefaults];
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LastLoadError = ex.Message;
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings) =>
        AtomicFile.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, JsonDefaults.Options));
}
