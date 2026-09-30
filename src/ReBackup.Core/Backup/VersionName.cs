using System.Globalization;

namespace ReBackup.Core.Backup;

/// <summary>Names of version folders in a target: <c>YYYY_MM_DD-hh_mm PlanName</c>.</summary>
public static class VersionName
{
    public const string PartialSuffix = ".partial";
    public const string ManifestFileName = "re-manifest.json";
    private const string TimestampFormat = "yyyy_MM_dd-HH_mm";

    public static string Format(DateTime localTime, string planName) =>
        localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture) + " " + planName;

    /// <summary>True when the folder name is exactly a timestamp, one space and the plan name.</summary>
    public static bool TryParse(string folderName, string planName, out DateTime localTime)
    {
        localTime = default;
        var stampLength = TimestampFormat.Length;
        if (folderName.Length != stampLength + 1 + planName.Length || folderName[stampLength] != ' ')
            return false;
        if (!folderName.AsSpan(stampLength + 1).Equals(planName, StringComparison.OrdinalIgnoreCase))
            return false;
        return DateTime.TryParseExact(folderName.AsSpan(0, stampLength), TimestampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out localTime);
    }
}
