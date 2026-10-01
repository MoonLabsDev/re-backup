using System.Globalization;
using System.Windows.Data;

namespace ReBackup.App.Theme;

/// <summary>Shows a text in capitals (table column headers); other values pass through unchanged.</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? text.ToUpper(culture) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
