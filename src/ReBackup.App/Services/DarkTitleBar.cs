using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ReBackup.App.Services;

/// <summary>
/// Switches a window's title bar between the dark (immersive dark mode) and the light look of Windows 10 20H1+ and 11,
/// matching the app theme.
/// </summary>
public static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010,
        SwpFrameChanged = 0x0020, SwpNoOwnerZOrder = 0x0200;

    /// <summary>
    /// Gives the window the title bar of the current theme (<see cref="ThemeManager.IsDark"/>) now, or as soon as it has
    /// its handle; later switches reach it through <see cref="ThemeManager"/>.
    /// </summary>
    public static void Apply(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Set(window, ThemeManager.IsDark);
        else
            window.SourceInitialized += (_, _) => Set(window, ThemeManager.IsDark);
    }

    /// <summary>Dark (true) or light (false) title bar, now or as soon as the window has its handle.</summary>
    public static void Apply(Window window, bool dark)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Set(window, dark);
        else
            window.SourceInitialized += (_, _) => Set(window, dark);
    }

    private static void Set(Window window, bool dark)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var enabled = dark ? 1 : 0;
            // Older Windows versions reject the attribute; the title bar then simply stays light.
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) == 0 && window.IsVisible)
            {
                // A shown window repaints its frame only when told to.
                SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
