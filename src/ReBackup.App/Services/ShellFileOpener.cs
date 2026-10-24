using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace ReBackup.App.Services;

/// <summary>Opens a file with the program Windows associates with it.</summary>
public static class ShellFileOpener
{
    /// <summary>Opens <paramref name="path"/> in Notepad (quoted, so spaces stay part of the path); false when it did not start.</summary>
    public static bool OpenInNotepad(string path)
    {
        try
        {
            var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            using var process = Process.Start(new ProcessStartInfo(notepad, $"\"{path}\"") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

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
