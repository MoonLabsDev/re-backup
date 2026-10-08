using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage.S3;

namespace ReBackup.Shared.Wpf.S3;

/// <summary>
/// The texts <see cref="S3ConnectionDialog"/> builds from view-model values (label keys, check results). The converters
/// take the value and <see cref="Loc.Language"/> in a MultiBinding, so a language switch re-reads them.
/// </summary>
public static class S3DialogTexts
{
    /// <summary>The label of a key, or "" for <c>null</c>.</summary>
    public static IMultiValueConverter Label { get; } = new Converter(value => value is string key ? Loc.T(key) : "");

    /// <summary>The name of the check of an <see cref="S3CheckResult"/>.</summary>
    public static IMultiValueConverter CheckName { get; } = new Converter(value => value is S3CheckResult result ? NameOf(result.Check) : "");

    /// <summary>✓ / ✗ / ! / – for the state of an <see cref="S3CheckResult"/>.</summary>
    public static IMultiValueConverter Symbol { get; } = new Converter(value => value is S3CheckResult result ? SymbolOf(result.State) : "");

    /// <summary>The reason of an <see cref="S3CheckResult"/> (a region warning names the bucket's region).</summary>
    public static IMultiValueConverter Reason { get; } = new Converter(value => value is S3CheckResult result ? ReasonOf(result) : "");

    /// <summary>"Use &lt;region&gt;" for the region of a region warning.</summary>
    public static IMultiValueConverter UseRegion { get; } = new Converter(value =>
        value is S3CheckResult { Detail: { } region } ? Loc.F("s3.test.useRegion", ("region", region)) : "");

    public static string NameOf(S3Check check) => check switch
    {
        S3Check.Account => Loc.T("s3.test.check.account"),
        S3Check.Bucket => Loc.T("s3.test.check.bucket"),
        S3Check.Versioning => Loc.T("s3.test.check.versioning"),
        S3Check.List => Loc.T("s3.test.check.list"),
        S3Check.WriteDelete => Loc.T("s3.test.check.writeDelete"),
        S3Check.Lifecycle => Loc.T("s3.test.check.lifecycle"),
        _ => check.ToString(),
    };

    public static string SymbolOf(S3CheckState state) => state switch
    {
        S3CheckState.Ok => "✓",
        S3CheckState.Failed => "✗",
        S3CheckState.Warning => "!",
        _ => "–",
    };

    public static string ReasonOf(S3CheckResult result) =>
        result.MessageKey is null ? "" : Loc.F(result.MessageKey, ("region", result.Detail));

    private sealed class Converter(Func<object?, string> convert) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
            convert(values.Length > 0 && values[0] != DependencyProperty.UnsetValue ? values[0] : null);

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
