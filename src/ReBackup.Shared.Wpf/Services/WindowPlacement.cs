using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ReBackup.Shared.Wpf.Services;

/// <summary>
/// Keeps a window inside the work area of its monitor. WPF centres a dialog over its owner without looking at the
/// screen, so on a smaller monitor its lower edge (and the OK button) can end up below the taskbar.
/// </summary>
public static class WindowPlacement
{
    /// <summary>Moves the window into the work area and shrinks it to fit, never below <paramref name="minHeight"/>.</summary>
    public static Rect FitInto(Rect window, Rect workArea, double minHeight)
    {
        var width = Math.Min(window.Width, workArea.Width);
        var height = Math.Max(Math.Min(window.Height, workArea.Height), minHeight);
        var left = Math.Max(workArea.Left, Math.Min(window.Left, workArea.Right - width));
        var top = Math.Max(workArea.Top, Math.Min(window.Top, workArea.Bottom - height));
        return new Rect(left, top, width, height);
    }

    /// <summary>Fits the window into its monitor's work area once it has its position, before it is shown.</summary>
    public static void KeepOnScreen(Window window) => window.SourceInitialized += (_, _) => Fit(window);

    private static void Fit(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) ||
            PresentationSource.FromVisual(window)?.CompositionTarget is not { } target)
            return;

        var fromDevice = target.TransformFromDevice;
        var workArea = new Rect(
            fromDevice.Transform(new Point(info.Work.Left, info.Work.Top)),
            fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom)));
        var fitted = FitInto(new Rect(window.Left, window.Top, window.Width, window.Height), workArea, window.MinHeight);
        window.Left = fitted.Left;
        window.Top = fitted.Top;
        window.Width = fitted.Width;
        window.Height = fitted.Height;
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
