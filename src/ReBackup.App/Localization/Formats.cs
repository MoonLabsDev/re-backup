using ReBackup.Core.IO;
using ReBackup.Core.Localization;

namespace ReBackup.App.Localization;

/// <summary>Dates, numbers and sizes as the applied language writes them (<c>format.*</c> labels, <see cref="Loc.Culture"/>).</summary>
public static class Formats
{
    public static string Date(DateTime local) => local.ToString(Loc.T("format.date"), Loc.Culture);

    public static string DateAndTime(DateTime local) => local.ToString(Loc.T("format.dateTime"), Loc.Culture);

    public static string DateAndTimeSeconds(DateTime local) => local.ToString(Loc.T("format.dateTimeSeconds"), Loc.Culture);

    public static string Time(DateTime local) => local.ToString(Loc.T("format.time"), Loc.Culture);

    public static string WeekdayAndTime(DateTime local) => local.ToString(Loc.T("format.weekdayTime"), Loc.Culture);

    public static string NextRun(DateTime local) => local.ToString(Loc.T("format.nextRun"), Loc.Culture);

    public static string Count(long count) => count.ToString("N0", Loc.Culture);

    public static string Bytes(long bytes) => ByteSize.Format(bytes, Loc.Culture);

    /// <summary>"1 file" / "2 files", as an argument of a label that holds several counts.</summary>
    public static Message Files(long count) => Message.Of("common.fileCount", ("count", count));

    /// <summary>"1 folder" / "2 folders", as an argument of a label that holds several counts.</summary>
    public static Message Folders(long count) => Message.Of("common.folderCount", ("count", count));
}
