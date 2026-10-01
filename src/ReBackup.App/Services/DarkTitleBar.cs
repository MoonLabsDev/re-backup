using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ReBackup.App.Services;

/// <summary>Switches a window's title bar to the dark (immersive dark mode) look of Windows 10 20H1+ and 11.</summary>
public static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>Applies the dark title bar now, or as soon as the window has its handle.</summary>
    public static void Apply(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            SetDark(window);
        else
            window.SourceInitialized += (_, _) => SetDark(window);
    }

    private static void SetDark(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var enabled = 1;
            // Older Windows versions reject the attribute; the title bar then simply stays light.
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
