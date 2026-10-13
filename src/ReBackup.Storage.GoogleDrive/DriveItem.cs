namespace ReBackup.Storage.GoogleDrive;

/// <summary>A file or folder on Google Drive, as far as <see cref="DriveStorage"/> needs it.</summary>
/// <param name="Id">The Drive file ID.</param>
/// <param name="Name">The name; Drive allows any text, including <c>/</c>, and siblings with the same name.</param>
/// <param name="MimeType">The MIME type; <see cref="DriveMimeTypes.Folder"/> for folders, <c>application/vnd.google-apps.*</c> for Google files.</param>
/// <param name="Size">The size in bytes; <c>null</c> for folders and Google files.</param>
/// <param name="ModifiedUtc">The modified time (UTC).</param>
/// <param name="Md5">The MD5 of the content (hex); <c>null</c> for folders and Google files.</param>
/// <param name="Version">Drive's version number, which grows with every change.</param>
/// <param name="DriveId">The shared drive the item lives in; <c>null</c> for an item in someone's My Drive.</param>
public sealed record DriveItem(string Id, string Name, string MimeType, long? Size, DateTime ModifiedUtc, string? Md5, long Version,
    string? DriveId = null)
{
    public bool IsFolder => MimeType == DriveMimeTypes.Folder;
}

/// <summary>A shared drive the account is a member of.</summary>
public sealed record SharedDrive(string Id, string Name);

/// <summary>The Google MIME types <see cref="DriveStorage"/> knows and how Google files are exported.</summary>
public static class DriveMimeTypes
{
    public const string Folder = "application/vnd.google-apps.folder";
    public const string GooglePrefix = "application/vnd.google-apps.";

    public const string Document = "application/vnd.google-apps.document";
    public const string Spreadsheet = "application/vnd.google-apps.spreadsheet";
    public const string Presentation = "application/vnd.google-apps.presentation";
    public const string Drawing = "application/vnd.google-apps.drawing";

    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Pptx = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    public const string Pdf = "application/pdf";

    /// <summary>Google file types that are exported on read: the type, the extension added to the name, the export type.</summary>
    public static IReadOnlyList<(string GoogleType, string Extension, string ExportType)> Exports { get; } =
    [
        (Document, ".docx", Docx),
        (Spreadsheet, ".xlsx", Xlsx),
        (Presentation, ".pptx", Pptx),
        (Drawing, ".pdf", Pdf),
    ];

    /// <summary>The export of a Google file type; <c>null</c> for any other type.</summary>
    public static (string Extension, string ExportType)? ExportOf(string mimeType)
    {
        foreach (var (googleType, extension, exportType) in Exports)
            if (googleType == mimeType) return (extension, exportType);
        return null;
    }

    /// <summary>Whether the type is a Google type (folder, document, shortcut, form, …).</summary>
    public static bool IsGoogleType(string mimeType) => mimeType.StartsWith(GooglePrefix, StringComparison.Ordinal);
}
