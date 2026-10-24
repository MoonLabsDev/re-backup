using System.IO;
using System.Runtime.InteropServices;
using ReBackup.Core.Ignore;

namespace ReBackup.App.Services;

/// <summary>Sends a file to the Windows recycle bin through the shell, without prompts.</summary>
public sealed class ShellRecycleBin : IRecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    public RecycleOutcome Recycle(string path)
    {
        path = Path.GetFullPath(path);
        if (!IsOnFixedDrive(path))
        {
            // Without a recycle bin the shell would delete permanently, so keep the file under another name.
            File.Move(path, IgnoreRelocation.IntegratedFileName(Path.GetDirectoryName(path)!));
            return RecycleOutcome.Renamed;
        }

        // FOF_WANTNUKEWARNING: on a fixed drive whose recycle bin is disabled (or for a file over the bin's size limit) the
        // shell deletes permanently. FOF_NOCONFIRMATION would do that silently; with this flag it asks first, and a "No"
        // sets fAnyOperationsAborted, which throws below so the caller restores the plan.
        var operation = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = Path.GetFullPath(path) + "\0\0",   // a double-null-terminated list
            fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning),
        };
        var result = SHFileOperationW(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted)
            throw new IOException($"Could not move '{path}' to the recycle bin (shell error {result}).");
        return RecycleOutcome.Recycled;
    }

    private static bool IsOnFixedDrive(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        try { return new DriveInfo(root).DriveType == DriveType.Fixed; }
        catch (ArgumentException) { return false; }
    }

    // Natural alignment matches the 64-bit layout of SHFILEOPSTRUCTW; the app runs as x64.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct fileOp);
}
