using System.Globalization;

namespace ReBackup.Shared.IO;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Base 1024 with one decimal, invariant ("1.5 KB"): the form written to logs.</summary>
    public static string Format(long bytes) => Format(bytes, CultureInfo.InvariantCulture);

    /// <summary>Like <see cref="Format(long)"/>, with the decimal separator of <paramref name="provider"/> ("1,5 KB").</summary>
    public static string Format(long bytes, IFormatProvider provider)
    {
        if (bytes < 1024)
            return string.Create(provider, $"{bytes} B");

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(provider, $"{value:0.0} {Units[unit]}");
    }
}
