using System.Text.Json;
using ReBackup.Core.IO;
using ReBackup.Core.Json;

namespace ReBackup.Core.Settings;

public sealed class SettingsStore
{
    public SettingsStore(string settingsFile) => SettingsFile = settingsFile;

    public string SettingsFile { get; }

    /// <summary>Set when the last <see cref="Load"/> found a corrupt file and fell back to defaults.</summary>
    public string? LastLoadError { get; private set; }

    public AppSettings Load()
    {
        LastLoadError = null;
        if (!File.Exists(SettingsFile))
            return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonDefaults.Options)
                ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            LastLoadError = ex.Message;
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings) =>
        AtomicFile.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, JsonDefaults.Options));
}
