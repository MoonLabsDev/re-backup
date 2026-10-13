using System.Net.Sockets;

namespace ReBackup.Storage.GoogleDrive;

/// <summary>
/// Maps Drive API and network failures to <see cref="StorageException"/> subtypes. Messages are fixed texts plus the path; the
/// original exception stays as <c>InnerException</c>.
/// </summary>
internal static class DriveErrors
{
    /// <summary>Reasons that mean the account may not do this (or its key is not valid).</summary>
    private static readonly HashSet<string> AccessReasons = new(StringComparer.Ordinal)
    {
        "authError", "invalid_grant", "invalidCredentials", "unauthorized_client", "insufficientPermissions",
        "insufficientFilePermissions", "appNotAuthorizedToFile", "forbidden",
    };

    /// <summary>Reasons that mean the shared drive takes nothing more: the run stops.</summary>
    private static readonly HashSet<string> FullReasons = new(StringComparer.Ordinal)
    {
        "storageQuotaExceeded", "teamDriveFileLimitExceeded", "numChildrenInNonRootLimitExceeded", "teamDriveHierarchyTooDeep",
    };

    public const string ExportSizeLimitExceeded = "exportSizeLimitExceeded";

    /// <summary>
    /// The exception to throw for <paramref name="ex"/> raised by a call about <paramref name="path"/>. Cancellation by
    /// <paramref name="callerToken"/> comes back unchanged; a timeout (cancellation without the caller's token) is "unavailable".
    /// </summary>
    public static Exception Map(Exception ex, string path, CancellationToken callerToken)
    {
        if (ex is StorageException) return ex;

        if (callerToken.IsCancellationRequested)
            for (var e = ex; e is not null; e = e.InnerException)
                if (e is OperationCanceledException cancelled) return cancelled;

        for (var e = ex; e is not null; e = e.InnerException)
            if (e is DriveApiException api) return MapApi(api, path, ex);

        for (var e = ex; e is not null; e = e.InnerException)
            if (e is HttpRequestException or SocketException or TimeoutException or OperationCanceledException or IOException)
                return Unavailable(path, ex);

        return new StorageIOException(path, $"I/O error at '{path}'.", ex);
    }

    /// <summary>Whether the call was refused for its rate (429, 403 <c>rateLimitExceeded</c> / <c>userRateLimitExceeded</c>): not carried out, safe to repeat.</summary>
    public static bool IsRateLimit(DriveApiException ex) =>
        ex.Status == 429 || (ex.Status == 403 && ex.Reason is "rateLimitExceeded" or "userRateLimitExceeded");

    /// <summary>Whether the call failed on Google's side (5xx) or on the way (network, timeout): it may or may not have been carried out.</summary>
    public static bool IsServerOrNetworkFailure(Exception ex, CancellationToken callerToken)
    {
        if (ex is DriveApiException api) return api.Status >= 500;
        if (callerToken.IsCancellationRequested) return false;
        return ex is HttpRequestException or SocketException or TimeoutException or OperationCanceledException or IOException;
    }

    private static Exception MapApi(DriveApiException api, string path, Exception ex)
    {
        if (api.Reason == ExportSizeLimitExceeded)
            return new StorageIOException(path, $"'{path}' is a Google file larger than Google's 10 MB export limit and cannot be exported.", ex)
            {
                NotReadable = true,
            };
        if (FullReasons.Contains(api.Reason))
            return new StorageIOException(path, $"The shared drive takes no more files: its storage quota or file limit is reached ({api.Reason}).", ex);
        if (IsRateLimit(api) || api.Status >= 500)
            return Unavailable(path, ex);
        if (api.Status == 401 || AccessReasons.Contains(api.Reason))
            return new StorageAccessDeniedException(path, $"Access denied: '{path}'.", ex);
        if (api.Status == 404)
            return new StorageNotFoundException(path, $"Not found: '{path}'.", ex);
        if (api.Status == 403)
            return new StorageAccessDeniedException(path, $"Access denied: '{path}'.", ex);
        return new StorageIOException(path, $"I/O error at '{path}' (Drive error {api.Status}{(api.Reason.Length > 0 ? " " + api.Reason : "")}).", ex);
    }

    private static StorageUnavailableException Unavailable(string path, Exception ex) =>
        new(path, "The Google Drive storage is not available.", ex);
}
