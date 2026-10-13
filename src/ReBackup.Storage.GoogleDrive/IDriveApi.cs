namespace ReBackup.Storage.GoogleDrive;

/// <summary>
/// The thin seam <see cref="DriveStorage"/> talks to Google Drive through. Implementations cover all drives (shared drives
/// included), leave out trashed files, and report every API failure as <see cref="DriveApiException"/>; network failures may
/// surface as <see cref="HttpRequestException"/>, <see cref="IOException"/> or <see cref="TimeoutException"/>. Streams handed in
/// are read from their current position; implementations do not dispose them.
/// </summary>
public interface IDriveApi
{
    /// <summary>
    /// The children of the folder (all pages), or only those named exactly <paramref name="name"/> when it is given.
    /// 404 when the folder does not exist.
    /// </summary>
    Task<IReadOnlyList<DriveItem>> ListChildrenAsync(string folderId, CancellationToken ct, string? name = null);

    /// <summary>The item with that ID; 404 when it does not exist.</summary>
    Task<DriveItem> GetAsync(string id, CancellationToken ct);

    /// <summary>Creates a folder in the parent folder.</summary>
    Task<DriveItem> CreateFolderAsync(string parentId, string name, CancellationToken ct);

    /// <summary>Uploads a new file into the parent folder, with <paramref name="modifiedUtc"/> as its modified time when given.</summary>
    Task<DriveItem> UploadAsync(string parentId, string name, Stream content, DateTime? modifiedUtc, CancellationToken ct);

    /// <summary>Replaces the content of an existing file (same ID), with <paramref name="modifiedUtc"/> as its modified time when given.</summary>
    Task<DriveItem> UpdateContentAsync(string id, Stream content, DateTime? modifiedUtc, CancellationToken ct);

    /// <summary>Deletes the item permanently (no trash); a folder with everything below it.</summary>
    Task DeleteAsync(string id, CancellationToken ct);

    /// <summary>The content of a file that is not a Google file.</summary>
    Task<Stream> DownloadAsync(string id, CancellationToken ct);

    /// <summary>A Google file exported as <paramref name="mimeType"/>; 403 <c>exportSizeLimitExceeded</c> over Google's 10 MB limit.</summary>
    Task<Stream> ExportAsync(string id, string mimeType, CancellationToken ct);

    /// <summary>The shared drives the account is a member of.</summary>
    Task<IReadOnlyList<SharedDrive>> ListSharedDrivesAsync(CancellationToken ct);
}

/// <summary>A Drive API call failed with an HTTP status and Google's error reason (e.g. <c>rateLimitExceeded</c>, <c>notFound</c>).</summary>
public sealed class DriveApiException(int status, string reason, string? message = null, Exception? inner = null)
    : Exception(message ?? $"Drive API error {status} ({reason}).", inner)
{
    /// <summary>The HTTP status (0 when there was no response, e.g. an authentication failure before the call).</summary>
    public int Status { get; } = status;

    /// <summary>Google's error reason; <c>""</c> when none was given.</summary>
    public string Reason { get; } = reason;
}
