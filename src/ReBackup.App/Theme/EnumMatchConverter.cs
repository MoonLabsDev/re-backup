using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ReBackup.App.Theme;

/// <summary>
/// True when the value's name equals the parameter (<c>ConverterParameter=Weekly</c>); as Visibility when bound to one
/// (Visible or Collapsed). Back: true gives the parameter as that enum value, false leaves the source alone, so a group
/// of radio buttons can share one enum property.
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var match = value is not null && parameter is not null &&
                    string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
        if (targetType == typeof(Visibility))
            return match ? Visibility.Visible : Visibility.Collapsed;
        return match;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return value is true && parameter is not null && enumType.IsEnum
            ? Enum.Parse(enumType, parameter.ToString()!)
            : Binding.DoNothing;
    }
}
