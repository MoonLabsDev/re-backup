using Microsoft.Win32;

namespace ReBackup.Shared.Wpf.Services;

public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Adds or removes the app's autostart entry (Run key of the current user) named <paramref name="valueName"/>.</summary>
    public static void Apply(string valueName, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(valueName, $"\"{Environment.ProcessPath}\" --minimized");
        else
            key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
