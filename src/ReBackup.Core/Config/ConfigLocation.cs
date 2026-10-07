using System.Text.Json;
using ReBackup.Core.Localization;
using ReBackup.Shared.IO;
using ReBackup.Shared.Json;

namespace ReBackup.Core.Config;

public enum ConfigMoveMode
{
    /// <summary>Copy settings, plans and logs into the new folder (it must not contain a configuration yet).</summary>
    CopyCurrent,
    /// <summary>Point at the new folder as-is without copying anything.</summary>
    UseExisting,
}

public static class ConfigLocation
{
    public const string PointerFileName = "location.json";

    public static string DefaultAppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReBackup");

    public static ConfigPaths Resolve(string appDataRoot)
    {
        var pointerFile = Path.Combine(appDataRoot, PointerFileName);
        if (File.Exists(pointerFile))
        {
            try
            {
                var pointer = JsonSerializer.Deserialize<LocationPointer>(File.ReadAllText(pointerFile), JsonDefaults.Options);
                if (!string.IsNullOrWhiteSpace(pointer?.ConfigFolder))
                    return new ConfigPaths(pointer.ConfigFolder);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Corrupt or unreadable pointer: fall back to the default location.
            }
        }
        return new ConfigPaths(appDataRoot);
    }

    public static bool ContainsConfiguration(string root)
    {
        var paths = new ConfigPaths(root);
        return File.Exists(paths.SettingsFile)
            || (Directory.Exists(paths.PlansDirectory) && Directory.EnumerateFiles(paths.PlansDirectory, "*.json").Any());
    }

    public static ConfigPaths Move(string appDataRoot, ConfigPaths current, string newRoot, ConfigMoveMode mode)
    {
        var target = new ConfigPaths(PathUtil.Normalize(newRoot));
        if (SamePath(current.Root, target.Root))
            return current;

        if (mode == ConfigMoveMode.CopyCurrent)
        {
            if (ContainsConfiguration(target.Root))
                throw new InvalidOperationException(CoreTexts.English("core.config.containsConfiguration"));

            Directory.CreateDirectory(target.PlansDirectory);
            Directory.CreateDirectory(target.LogsDirectory);
            if (File.Exists(current.SettingsFile))
                File.Copy(current.SettingsFile, target.SettingsFile);
            CopyFiles(current.PlansDirectory, target.PlansDirectory);
            CopyFiles(current.LogsDirectory, target.LogsDirectory);
        }

        Directory.CreateDirectory(appDataRoot);
        var pointerFile = Path.Combine(appDataRoot, PointerFileName);
        if (SamePath(appDataRoot, target.Root))
            File.Delete(pointerFile);
        else
            AtomicFile.WriteAllText(pointerFile, JsonSerializer.Serialize(new LocationPointer(target.Root), JsonDefaults.Options));

        return target;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(PathUtil.Normalize(a), PathUtil.Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static void CopyFiles(string fromDir, string toDir)
    {
        if (!Directory.Exists(fromDir))
            return;
        foreach (var file in Directory.EnumerateFiles(fromDir))
            File.Copy(file, Path.Combine(toDir, Path.GetFileName(file)));
    }

    internal sealed record LocationPointer(string ConfigFolder);
}
