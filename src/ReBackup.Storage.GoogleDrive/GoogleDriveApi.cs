using System.Net.Http.Headers;
using System.Text;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Upload;
using ReBackup.Storage.GoogleDrive.Connections;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace ReBackup.Storage.GoogleDrive;

/// <summary>
/// <see cref="IDriveApi"/> on Google's Drive v3 API with a service-account key (scope <c>drive</c>). Every call covers shared drives
/// (<c>supportsAllDrives</c>, <c>includeItemsFromAllDrives</c>); listings search the connection's shared drive (<c>corpora=drive</c>)
/// or, for a root in someone's My Drive, all drives the account sees. Files under 5 MB are sent in one request (multipart),
/// larger ones as a resumable upload that is resumed after a server or network failure before the call gives up. The client does
/// not retry by itself: <see cref="DriveStorage"/> does (rate limits, server errors).
/// </summary>
/// <remarks>
/// Failures: Google's API errors become <see cref="DriveApiException"/> (HTTP status and the first error reason), a key Google
/// refuses becomes one too (the token endpoint's status, its error code such as <c>invalid_grant</c> as reason); network errors
/// stay <see cref="HttpRequestException"/> / <see cref="IOException"/>, and a request that times out (cancelled, but not by the
/// caller) becomes <see cref="TimeoutException"/>.
/// </remarks>
public sealed class GoogleDriveApi : IDriveApi, IDisposable
{
    /// <summary>Files from this size on are sent as resumable upload.</summary>
    public const long ResumableThreshold = 5 * 1024 * 1024;

    /// <summary>How often a failed resumable upload is resumed (after a server or network failure) before the call gives up.</summary>
    private const int MaxResumes = 5;

    private const string UploadBase = "https://www.googleapis.com/upload/drive/v3/files";
    private const string ItemFields = "id,name,mimeType,size,modifiedTime,md5Checksum,version,parents,driveId,trashed";
    private const string OctetStream = "application/octet-stream";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private readonly DriveService _service;
    private readonly string? _sharedDriveId;

    /// <summary>A client for the connection's key; listings search the connection's shared drive when it has one.</summary>
    public GoogleDriveApi(DriveConnection connection)
        : this((connection ?? throw new ArgumentNullException(nameof(connection))).KeyJson, connection.Info.SharedDriveId)
    {
    }

    /// <summary>
    /// A client for the service-account key <paramref name="keyJson"/>. Listings search the shared drive
    /// <paramref name="sharedDriveId"/>, or every drive the account sees when it is <c>null</c>. A key that cannot be read throws
    /// <see cref="StorageAccessDeniedException"/> (never quoting it).
    /// </summary>
    public GoogleDriveApi(string keyJson, string? sharedDriveId = null)
    {
        ArgumentNullException.ThrowIfNull(keyJson);
        GoogleCredential credential;
        try
        {
            credential = CredentialFactory.FromJson<ServiceAccountCredential>(keyJson).ToGoogleCredential().CreateScoped(DriveService.Scope.Drive);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new StorageAccessDeniedException("", "The Google service account key is not valid.", new InvalidOperationException(ex.GetType().Name));
        }

        _service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ReBackup",
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
        });
        _service.HttpClient.Timeout = RequestTimeout;
        _sharedDriveId = string.IsNullOrWhiteSpace(sharedDriveId) ? null : sharedDriveId;
    }

    public async Task<IReadOnlyList<DriveItem>> ListChildrenAsync(string folderId, CancellationToken ct, string? name = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folderId);
        var query = $"'{Escape(folderId)}' in parents and trashed = false";
        if (name is not null) query += $" and name = '{Escape(name)}'";

        var items = new List<DriveItem>();
        string? pageToken = null;
        do
        {
            var request = _service.Files.List();
            request.Q = query;
            request.Fields = $"nextPageToken,files({ItemFields})";
            request.PageSize = 1000;
            request.PageToken = pageToken;
            request.SupportsAllDrives = true;
            request.IncludeItemsFromAllDrives = true;
            if (_sharedDriveId is not null)
            {
                request.Corpora = "drive";
                request.DriveId = _sharedDriveId;
            }
            else
            {
                request.Corpora = "allDrives";
            }

            var page = await CallAsync(c => request.ExecuteAsync(c), ct).ConfigureAwait(false);
            if (page.Files is not null)
                items.AddRange(page.Files.Where(f => f.Trashed != true).Select(ToItem));
            pageToken = page.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        // Drive answers a query on a folder that is gone with an empty list: confirm the folder, so a missing one is a 404.
        if (items.Count == 0 && !(await GetAsync(folderId, ct).ConfigureAwait(false)).IsFolder)
            throw new DriveApiException(404, "notFound", "The item is not a folder.");
        return items;
    }

    public async Task<DriveItem> GetAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var request = _service.Files.Get(id);
        request.Fields = ItemFields;
        request.SupportsAllDrives = true;
        var file = await CallAsync(c => request.ExecuteAsync(c), ct).ConfigureAwait(false);
        if (file.Trashed == true) throw new DriveApiException(404, "notFound", "The item is in the trash.");
        return ToItem(file);
    }

    public async Task<DriveItem> CreateFolderAsync(string parentId, string name, CancellationToken ct)
    {
        var request = _service.Files.Create(new DriveFile { Name = name, MimeType = DriveMimeTypes.Folder, Parents = [parentId] });
        request.Fields = ItemFields;
        request.SupportsAllDrives = true;
        return ToItem(await CallAsync(c => request.ExecuteAsync(c), ct).ConfigureAwait(false));
    }

    public Task<DriveItem> UploadAsync(string parentId, string name, Stream content, DateTime? modifiedUtc, CancellationToken ct)
    {
        var metadata = new DriveFile { Name = name, Parents = [parentId], ModifiedTimeDateTimeOffset = ToOffset(modifiedUtc) };
        if (IsSmall(content))
            return SimpleUploadAsync(HttpMethod.Post, UploadBase, metadata, content, ct);

        var request = _service.Files.Create(metadata, content, OctetStream);
        request.Fields = ItemFields;
        request.SupportsAllDrives = true;
        return ResumableUploadAsync(request, () => request.ResponseBody, ct);
    }

    public Task<DriveItem> UpdateContentAsync(string id, Stream content, DateTime? modifiedUtc, CancellationToken ct)
    {
        var metadata = new DriveFile { ModifiedTimeDateTimeOffset = ToOffset(modifiedUtc) };
        if (IsSmall(content))
            return SimpleUploadAsync(HttpMethod.Patch, $"{UploadBase}/{Uri.EscapeDataString(id)}", metadata, content, ct);

        var request = _service.Files.Update(metadata, id, content, OctetStream);
        request.Fields = ItemFields;
        request.SupportsAllDrives = true;
        return ResumableUploadAsync(request, () => request.ResponseBody, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var request = _service.Files.Delete(id);
        request.SupportsAllDrives = true;
        await CallAsync(c => request.ExecuteAsync(c), ct).ConfigureAwait(false);
    }

    public Task<Stream> DownloadAsync(string id, CancellationToken ct) =>
        GetStreamAsync($"{_service.BaseUri}files/{Uri.EscapeDataString(id)}?alt=media&supportsAllDrives=true", ct);

    public Task<Stream> ExportAsync(string id, string mimeType, CancellationToken ct) =>
        GetStreamAsync($"{_service.BaseUri}files/{Uri.EscapeDataString(id)}/export?mimeType={Uri.EscapeDataString(mimeType)}", ct);

    public async Task<IReadOnlyList<SharedDrive>> ListSharedDrivesAsync(CancellationToken ct)
    {
        var drives = new List<SharedDrive>();
        string? pageToken = null;
        do
        {
            var request = _service.Drives.List();
            request.PageSize = 100;
            request.PageToken = pageToken;
            request.Fields = "nextPageToken,drives(id,name)";
            var page = await CallAsync(c => request.ExecuteAsync(c), ct).ConfigureAwait(false);
            if (page.Drives is not null)
                drives.AddRange(page.Drives.Select(d => new SharedDrive(d.Id, d.Name ?? d.Id)));
            pageToken = page.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
        return drives;
    }

    public void Dispose() => _service.Dispose();

    // ---- uploads ----

    private static bool IsSmall(Stream content) => content.CanSeek && content.Length - content.Position < ResumableThreshold;

    /// <summary>One multipart request: the metadata (JSON) and the content.</summary>
    private Task<DriveItem> SimpleUploadAsync(HttpMethod method, string url, DriveFile metadata, Stream content, CancellationToken ct) =>
        CallAsync(async c =>
        {
            var json = _service.Serializer.Serialize(metadata);
            using var body = new MultipartContent("related");
            body.Add(new StringContent(json, Encoding.UTF8, "application/json"));
            var media = new StreamContent(new NonDisposingStream(content));
            media.Headers.ContentType = new MediaTypeHeaderValue(OctetStream);
            body.Add(media);

            using var request = new HttpRequestMessage(method,
                $"{url}?uploadType=multipart&supportsAllDrives=true&fields={Uri.EscapeDataString(ItemFields)}") { Content = body };
            using var response = await _service.HttpClient.SendAsync(request, c).ConfigureAwait(false);
            await ThrowIfFailedAsync(response).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(c).ConfigureAwait(false);
            return ToItem(_service.Serializer.Deserialize<DriveFile>(text));
        }, ct);

    /// <summary>A resumable upload, resumed (same session, from where it stopped) after a server or network failure.</summary>
    private async Task<DriveItem> ResumableUploadAsync(ResumableUpload upload, Func<DriveFile?> result, CancellationToken ct)
    {
        return await CallAsync(async c =>
        {
            var progress = await upload.UploadAsync(c).ConfigureAwait(false);
            for (var resumes = 0; progress.Status != UploadStatus.Completed && resumes < MaxResumes && IsResumable(progress.Exception, c); resumes++)
            {
                try
                {
                    progress = await upload.ResumeAsync(c).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    break;   // no session to resume (it was never started): the first failure stands
                }
            }
            c.ThrowIfCancellationRequested();
            if (progress.Status != UploadStatus.Completed)
                throw progress.Exception ?? new IOException("The upload did not complete.");
            return ToItem(result() ?? throw new IOException("The upload gave no answer."));
        }, ct).ConfigureAwait(false);
    }

    private static bool IsResumable(Exception? failure, CancellationToken ct) =>
        !ct.IsCancellationRequested && failure switch
        {
            GoogleApiException api => (int)api.HttpStatusCode >= 500,
            HttpRequestException or IOException or TimeoutException or OperationCanceledException => true,
            _ => false,
        };

    // ---- downloads ----

    /// <summary>The body of a GET as a stream (read as it arrives); disposing it releases the response.</summary>
    private Task<Stream> GetStreamAsync(string url, CancellationToken ct) =>
        CallAsync(async c =>
        {
            var response = await _service.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, c).ConfigureAwait(false);
            try
            {
                await ThrowIfFailedAsync(response).ConfigureAwait(false);
                return (Stream)new ResponseStream(await response.Content.ReadAsStreamAsync(c).ConfigureAwait(false), response);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }, ct);

    // ---- errors ----

    private async Task ThrowIfFailedAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        Google.Apis.Requests.RequestError? error = null;
        try
        {
            error = await _service.DeserializeError(response).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No error document: the status alone.
        }
        throw new DriveApiException((int)response.StatusCode, error?.Errors?.FirstOrDefault()?.Reason ?? "",
            error?.Message is { Length: > 0 } message ? $"Drive API error {(int)response.StatusCode}: {message}" : null);
    }

    /// <summary>Runs a call and turns Google's exceptions into the ones <see cref="IDriveApi"/> promises.</summary>
    private static async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call(ct).ConfigureAwait(false);
        }
        catch (GoogleApiException ex)
        {
            throw new DriveApiException((int)ex.HttpStatusCode, ex.Error?.Errors?.FirstOrDefault()?.Reason ?? "",
                $"Drive API error {(int)ex.HttpStatusCode}: {ex.Error?.Message ?? ex.Message}", ex);
        }
        catch (TokenResponseException ex)
        {
            // The key was refused when the access token was fetched (deleted key, disabled account, wrong clock).
            var status = ex.StatusCode is { } code ? (int)code : 401;
            throw new DriveApiException(status >= 500 ? status : 401, ex.Error?.Error ?? "authError",
                $"The service account key was refused: {ex.Error?.Error ?? "unknown error"}.", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The Google Drive request timed out.", ex);
        }
    }

    // ---- mapping ----

    private static DriveItem ToItem(DriveFile file)
    {
        var mimeType = file.MimeType ?? OctetStream;
        var isFile = !DriveMimeTypes.IsGoogleType(mimeType);
        var modified = file.ModifiedTimeDateTimeOffset?.UtcDateTime ?? DateTime.UnixEpoch;
        return new DriveItem(file.Id, file.Name ?? "", mimeType, isFile ? file.Size ?? 0 : null, DateTime.SpecifyKind(modified, DateTimeKind.Utc),
            isFile ? file.Md5Checksum : null, file.Version ?? 0, string.IsNullOrEmpty(file.DriveId) ? null : file.DriveId);
    }

    private static DateTimeOffset? ToOffset(DateTime? utc) =>
        utc is { } value ? new DateTimeOffset(DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc)) : null;

    /// <summary>A text for a Drive query string literal: <c>\</c> and <c>'</c> escaped.</summary>
    internal static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    /// <summary>Hands a caller's stream to <see cref="StreamContent"/> without letting it dispose the stream (<see cref="IDriveApi"/> does not own it).</summary>
    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A response body that disposes its response with it.</summary>
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
