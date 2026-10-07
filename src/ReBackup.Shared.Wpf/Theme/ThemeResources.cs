using System.Windows;
using System.Windows.Media;

namespace ReBackup.Shared.Wpf.Theme;

/// <summary>Reads theme colours (Theme/Colors.*.xaml) for code that draws itself, with a fallback outside the app.</summary>
internal static class ThemeResources
{
    public static Color Color(FrameworkElement element, string key, Color fallback) =>
        element.TryFindResource(key) is Color color ? color : fallback;

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static Pen Pen(Color color, double thickness)
    {
        var pen = new Pen(Brush(color), thickness);
        pen.Freeze();
        return pen;
    }

    /// <summary>Linear blend: 0 = <paramref name="from"/>, 1 = <paramref name="to"/>.</summary>
    public static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }
}
