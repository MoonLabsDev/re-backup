using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;

namespace ReBackup.Storage.S3;

/// <summary>
/// Maps SDK and network failures to <see cref="StorageException"/> subtypes. Messages are fixed texts plus the path: the SDK
/// message is never copied, as it can carry request details. The original exception stays as <c>InnerException</c>.
/// </summary>
internal static class S3Errors
{
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

        if (ex is AmazonServiceException service && (service.StatusCode != 0 || !string.IsNullOrEmpty(service.ErrorCode)))
            return MapService(service, path);

        // The SDK may wrap network failures (DNS, socket, timeout) in its own exception types.
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is HttpRequestException or SocketException or WebException or TimeoutException or OperationCanceledException or IOException)
                return Unavailable(path, ex);

        return new StorageIOException(path, $"I/O error at '{path}'.", ex);
    }

    private static Exception MapService(AmazonServiceException ex, string path)
    {
        var code = ex.ErrorCode;
        var status = ex.StatusCode;

        if (code == "InvalidObjectState")
            return new StorageIOException(path, $"'{path}' is archived (Glacier or Deep Archive) and must be restored first.", ex);
        if (code is "NoSuchKey" or "NoSuchBucket" || status == HttpStatusCode.NotFound)
            return new StorageNotFoundException(path, $"Not found: '{path}'.", ex);
        if (code is "AccessDenied" or "InvalidAccessKeyId" or "SignatureDoesNotMatch" || status == HttpStatusCode.Forbidden)
            return new StorageAccessDeniedException(path, $"Access denied: '{path}'.", ex);
        // 409 ConditionalRequestConflict: AWS's answer when a conditional write races another write to the same key.
        if (code is "PreconditionFailed" or "ConditionalRequestConflict" || status == HttpStatusCode.PreconditionFailed)
            return new StorageConflictException(path, $"Conflict at '{path}'.", ex);
        if ((int)status >= 500)
            return Unavailable(path, ex);
        return new StorageIOException(path, $"I/O error at '{path}' (S3 error {(string.IsNullOrEmpty(code) ? (int)status : code)}).", ex);
    }

    private static StorageUnavailableException Unavailable(string path, Exception ex) =>
        new(path, "The S3 storage is not available.", ex);
}
