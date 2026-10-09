using Microsoft.Win32;

namespace ReBackup.Shared.Wpf.Services;

/// <summary>The Run key of the current user (seam: tests use an in-memory fake, never the registry).</summary>
public interface IRunKey
{
    void SetValue(string name, string value);

    void DeleteValue(string name);
}

/// <summary>The real <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> key.</summary>
public sealed class RegistryRunKey : IRunKey
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public void SetValue(string name, string value)
    {
        using var key = Open();
        key.SetValue(name, value);
    }

    public void DeleteValue(string name)
    {
        using var key = Open();
        key.DeleteValue(name, throwOnMissingValue: false);
    }

    private static RegistryKey Open() =>
        Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
}

public static class StartupRegistration
{
    /// <summary>Adds or removes the autostart entry <paramref name="valueName"/>; the value is <c>"&lt;exe&gt;" &lt;arguments&gt;</c>.</summary>
    public static void Apply(string valueName, bool enabled, string arguments) =>
        Apply(valueName, enabled, arguments, new RegistryRunKey(), Environment.ProcessPath);

    /// <summary>Re-applies the entry when it is enabled, so it follows the current exe; does nothing when disabled.</summary>
    public static void Refresh(string valueName, bool enabled, string arguments) =>
        Refresh(valueName, enabled, arguments, new RegistryRunKey(), Environment.ProcessPath);

    public static void Refresh(string valueName, bool enabled, string arguments, IRunKey runKey, string? exePath)
    {
        if (enabled)
            Apply(valueName, true, arguments, runKey, exePath);
    }

    public static void Apply(string valueName, bool enabled, string arguments, IRunKey runKey, string? exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(runKey);
        if (!enabled)
        {
            runKey.DeleteValue(valueName);
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var command = $"\"{exePath}\"";
        runKey.SetValue(valueName, arguments.Length == 0 ? command : $"{command} {arguments}");
    }
}
