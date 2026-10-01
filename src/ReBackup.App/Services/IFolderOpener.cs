using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ReBackup.Core.Backup;

namespace ReBackup.App.Services;

/// <summary>Shows a version folder of a plan's target in Explorer.</summary>
public interface IFolderOpener
{
    /// <summary>
    /// Opens the folder <paramref name="versionName"/> directly inside <paramref name="target"/>. Nothing is started
    /// (false) unless the name is one plain folder name and that folder exists right now.
    /// </summary>
    bool OpenVersionFolder(string? target, string? versionName);
}

/// <summary>Starts <c>explorer.exe "&lt;folder&gt;"</c>; only for existing version folders (never arbitrary text).</summary>
public sealed class ExplorerFolderOpener : IFolderOpener
{
    public bool OpenVersionFolder(string? target, string? versionName)
    {
        if (VersionName.ExistingFolderIn(target, versionName) is not { } folder)
            return false;

        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try
        {
            // Quoted, so commas and spaces in the name are part of the path; a folder name cannot hold a quote.
            using var process = Process.Start(new ProcessStartInfo(explorer, $"\"{folder}\"") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
