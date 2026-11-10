using ReBackup.Core.Settings;
using ReBackup.Shared.Wpf.Services;

namespace ReBackup.App.Services;

/// <summary>ReBackup's autostart entry: what it starts with, and keeping it pointed at the current exe.</summary>
public static class AppStartup
{
    public const string ValueName = "ReBackup";

    /// <summary>Windows starts ReBackup into the tray.</summary>
    public const string Arguments = "--minimized";

    public static void Apply(bool enabled) => StartupRegistration.Apply(ValueName, enabled, Arguments);

    public static void Apply(bool enabled, IRunKey runKey, string? exePath) =>
        StartupRegistration.Apply(ValueName, enabled, Arguments, runKey, exePath);

    /// <summary>
    /// On every start: when StartWithWindows is on and the entry is missing, it is created for this exe. An entry that
    /// starts another exe stays: <see cref="Control.StaleRegistrations"/> asks before switching it to a newer version.
    /// </summary>
    public static void RefreshOnStart(AppSettings settings) =>
        RefreshOnStart(settings, new RegistryRunKey(), Environment.ProcessPath);

    public static void RefreshOnStart(AppSettings settings, IRunKey runKey, string? exePath)
    {
        if (settings.StartWithWindows && runKey.GetValue(ValueName) is null)
            Apply(true, runKey, exePath);
    }
}
