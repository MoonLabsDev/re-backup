using System.ComponentModel;
using System.Diagnostics;

namespace ReBackup.App.Services;

/// <summary>Opens a file with the program Windows associates with it.</summary>
public static class ShellFileOpener
{
    /// <summary>Starts the associated program for <paramref name="path"/>; false when nothing could be started.</summary>
    public static bool Open(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
