using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ReBackup.Core.Backup;
using ReBackup.Shared.IO;

namespace ReBackup.App.Services;

/// <summary>Shows version folders and the files in them in Explorer.</summary>
public interface IFolderOpener
{
    /// <summary>
    /// Opens the folder <paramref name="versionName"/> directly inside <paramref name="target"/>. Nothing is started
    /// (false) unless the name is one plain folder name and that folder exists right now.
    /// </summary>
    bool OpenVersionFolder(string? target, string? versionName);

    /// <summary>
    /// Opens a file of a version with the program Windows associates with it. False (nothing started) unless the path
    /// stays inside the version folder and the file exists right now.
    /// </summary>
    bool OpenFile(string versionFolder, string relativePath);

    /// <summary>Opens Explorer on the entry's folder with the entry selected; false like <see cref="OpenFile"/>.</summary>
    bool ShowInExplorer(string versionFolder, string relativePath);
}

/// <summary>Starts <c>explorer.exe</c> or the associated program; only for existing entries of a version (never arbitrary text).</summary>
public sealed class ExplorerFolderOpener : IFolderOpener
{
    private static string Explorer =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    public bool OpenVersionFolder(string? target, string? versionName)
    {
        if (VersionName.ExistingFolderIn(target, versionName) is not { } folder)
            return false;
        // Quoted, so commas and spaces in the name are part of the path; a folder name cannot hold a quote.
        return Start(new ProcessStartInfo(Explorer, $"\"{folder}\"") { UseShellExecute = false });
    }

    public bool OpenFile(string versionFolder, string relativePath) =>
        EntryIn(versionFolder, relativePath) is { } path && File.Exists(path) &&
        Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public bool ShowInExplorer(string versionFolder, string relativePath) =>
        EntryIn(versionFolder, relativePath) is { } path && (File.Exists(path) || Directory.Exists(path)) &&
        Start(new ProcessStartInfo(Explorer, $"/select,\"{path}\"") { UseShellExecute = false });

    /// <summary>
    /// The full path of the entry; null when it would leave the version folder or reaches it through a link (a
    /// junction or symbolic link in the version is never followed, as in the restore).
    /// </summary>
    private static string? EntryIn(string versionFolder, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(versionFolder) || !Path.IsPathFullyQualified(versionFolder) ||
            string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('"'))
            return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(versionFolder, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathUtil.IsSameOrInside(path, versionFolder))
                return null;
            var root = PathUtil.Normalize(versionFolder);
            for (var current = PathUtil.Normalize(path);
                 current.Length > root.Length;
                 current = Path.GetDirectoryName(current) ?? root)
            {
                var info = new FileInfo(current);
                if (info.Exists || Directory.Exists(current))
                {
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                        return null;
                }
            }
            return path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Start(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
