using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace ReBackup.Shared.Wpf.Localization;

/// <summary>
/// <c>{l:LocBind Type, Prefix=enum.triggerType.}</c>: the label <c>Prefix + value</c> for a bound value (an enum, a
/// weekday name); without a path the data context itself. Follows the value and a language switch.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocBindExtension : MarkupExtension
{
    public LocBindExtension()
    {
    }

    public LocBindExtension(string path) => Path = path;

    [ConstructorArgument("path")]
    public string Path { get; set; } = "";

    /// <summary>Put before the value's text to make the key, e.g. <c>enum.weekday.</c>.</summary>
    public string Prefix { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Converter = KeyConverter.Instance, ConverterParameter = Prefix, Mode = BindingMode.OneWay };
        binding.Bindings.Add(Path.Length == 0 ? new Binding() : new Binding(Path));
        binding.Bindings.Add(new Binding(nameof(Loc.Language)) { Source = Loc.Instance });
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class KeyConverter : IMultiValueConverter
    {
        public static readonly KeyConverter Instance = new();

        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values.Length > 0 && values[0] is { } value && value != DependencyProperty.UnsetValue
                ? Loc.T($"{parameter}{value}")
                : "";

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
