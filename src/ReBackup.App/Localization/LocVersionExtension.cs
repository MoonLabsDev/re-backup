using System.Globalization;
using System.Reflection;
using System.Windows.Data;
using System.Windows.Markup;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.Localization;

/// <summary>
/// <c>{l:LocVersion}</c>: "ReBackup v1.0.0" (label <c>app.versionLabel</c>) with the version read from the assembly;
/// follows a language switch.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocVersionExtension : MarkupExtension
{
    private static readonly Lazy<string> VersionText = new(ReadVersion);

    /// <summary>The assembly's informational version without a "+commit" suffix.</summary>
    public static string Version => VersionText.Value;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(Loc.Language)) { Source = Loc.Instance, Converter = LabelConverter.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }

    private static string ReadVersion()
    {
        var assembly = typeof(LocVersionExtension).Assembly;
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString(3)
                   ?? "";
        var plus = text.IndexOf('+');
        return plus >= 0 ? text[..plus] : text;
    }

    private sealed class LabelConverter : IValueConverter
    {
        public static readonly LabelConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Loc.F("app.versionLabel", ("version", Version));

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
