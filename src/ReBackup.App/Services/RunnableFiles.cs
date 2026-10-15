using System.Runtime.InteropServices;

namespace ReBackup.App.Services;

/// <summary>
/// Types Windows runs rather than shows when they are opened: a file of one of them is never opened from a backup
/// (in place or through a temporary copy) — its folder is shown instead, since it would start a program or script.
/// Windows' own list (<see cref="AssocIsDangerous"/>) is asked as well.
/// </summary>
public static class RunnableFiles
{
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".msi", ".scr", ".lnk", ".url", ".reg",
        ".hta", ".cpl",
        // further types the shell executes in the same way
        ".pif", ".vbe", ".wsh", ".msc", ".msp", ".jar", ".psm1", ".appref-ms", ".application", ".scf",
        ".ws", ".vb", ".chm", ".py", ".pyw", ".pyz", ".settingcontent-ms", ".library-ms", ".search-ms", ".sct", ".shb",
        ".shs", ".website", ".diagcab", ".appinstaller", ".msix", ".appx", ".gadget", ".ps1xml", ".psd1", ".mst", ".ins",
        ".isp",
    };

    /// <summary>True when a file with <paramref name="extension"/> (with its dot) would be run rather than shown.</summary>
    public static bool IsRunnable(string extension)
    {
        if (extension.Length == 0)
            return false;
        if (RunnableExtensions.Contains(extension))
            return true;
        try
        {
            return AssocIsDangerous(extension);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Windows' list of file types that are dangerous to open (shlwapi, Windows XP SP2 and later).</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssocIsDangerous(string pszAssoc);
}
