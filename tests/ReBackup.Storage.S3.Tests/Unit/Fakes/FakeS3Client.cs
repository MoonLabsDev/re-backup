using System.Reflection;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace ReBackup.Storage.S3.Tests.Unit.Fakes;

/// <summary>
/// An in-memory <see cref="IAmazonS3"/> for unit tests: only the members set up here work, every other call throws
/// <see cref="NotImplementedException"/>. Keys are listed like S3 does (prefix, delimiter, continuation token paging).
/// </summary>
public class FakeS3Client : DispatchProxy
{
    public sealed record Obj(string Key, long Size = 1, string ETag = "\"etag\"", DateTime? LastModified = null, byte[]? Body = null);

    public List<Obj> Objects { get; } = [];
    public int PageSize { get; set; } = 1000;

    /// <summary>Every listing page says it is truncated but carries no continuation token (a broken server answer).</summary>
    public bool TruncatedWithoutToken { get; set; }
    public List<ListObjectsV2Request> ListRequests { get; } = [];
    public List<GetObjectMetadataRequest> HeadRequests { get; } = [];

    /// <summary>Replaces the body stream a <c>GetObject</c> returns (to inject read failures).</summary>
    public Func<Obj, Stream>? BodyFactory { get; set; }

    /// <summary>Fails the named SDK method (e.g. <c>ListObjectsV2Async</c>) with the given exception.</summary>
    public Dictionary<string, Exception> Failures { get; } = [];

    /// <summary>A write request as received: the key, the body bytes (copied when the call came in) and the <c>If-None-Match</c> value.</summary>
    public sealed record Upload(string Key, byte[] Body, string? IfNoneMatch, long? ContentLength, string? IfMatch = null);

    public List<Upload> Puts { get; } = [];
    public List<InitiateMultipartUploadRequest> Initiates { get; } = [];
    public List<(int PartNumber, byte[] Body, long? PartSize)> Parts { get; } = [];
    public List<CompleteMultipartUploadRequest> Completes { get; } = [];
    public List<AbortMultipartUploadRequest> Aborts { get; } = [];
    public List<List<string>> DeleteBatches { get; } = [];

    /// <summary>Per-key errors a <c>DeleteObjects</c> reports in its response (the key is not deleted).</summary>
    public Dictionary<string, string> DeleteErrors { get; } = [];

    /// <summary>Runs inside every <c>UploadPart</c> before it completes (to hold an upload in flight).</summary>
    public Func<UploadPartRequest, CancellationToken, Task>? UploadPartHook { get; set; }

    /// <summary>The lifecycle rules <c>GetLifecycleConfiguration</c> returns.</summary>
    public List<LifecycleRule> Lifecycle { get; set; } = [];

    /// <summary>The status <c>GetBucketVersioning</c> reports (<c>null</c>: never versioned, as AWS answers then).</summary>
    public VersionStatus? Versioning { get; set; }

    /// <summary>The region <c>HeadBucket</c> reports (<c>BucketRegion</c>); <c>null</c> = not reported.</summary>
    public string? BucketRegion { get; set; }

    /// <summary>The keys <c>DeleteObject</c> was called for.</summary>
    public List<string> DeleteRequests { get; } = [];

    /// <summary>Runs at the start of every <c>HeadObject</c> (to cancel or fail while one is in flight).</summary>
    public Action? BeforeHead { get; set; }

    /// <summary>Runs after every successful <c>PutObject</c>.</summary>
    public Action? AfterPut { get; set; }

    /// <summary>
    /// An exclusive <c>PutObject</c> / <c>CompleteMultipartUpload</c> stores the object and then fails with 412, as the SDK's retry of a
    /// first attempt that succeeded unseen does.
    /// </summary>
    public bool RetryAfterSuccess { get; set; }

    public bool Disposed { get; private set; }

    public int UploadsInFlight => _uploadsInFlight;
    public int MaxUploadsInFlight { get; private set; }
    private int _uploadsInFlight;
    private readonly Dictionary<string, string> _multipartKeys = [];

    public static (IAmazonS3 Client, FakeS3Client Fake) Create()
    {
        var proxy = Create<IAmazonS3, FakeS3Client>();
        return (proxy, (FakeS3Client)(object)proxy);
    }

    public FakeS3Client Add(string key, long size = 1, string etag = "\"etag\"", DateTime? modified = null, byte[]? body = null)
    {
        Objects.Add(new Obj(key, size, etag, modified, body));
        return this;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        if (Failures.TryGetValue(name, out var failure)) return Fail(targetMethod, failure);
        try
        {
            switch (name)
            {
                case "ListObjectsV2Async":
                    return Task.FromResult(List((ListObjectsV2Request)args![0]!));
                case "GetObjectMetadataAsync":
                    return Task.FromResult(Head((GetObjectMetadataRequest)args![0]!));
                case "GetObjectAsync":
                    return Task.FromResult(Get((GetObjectRequest)args![0]!));
                case "PutObjectAsync":
                    return Task.FromResult(Put((PutObjectRequest)args![0]!));
                case "InitiateMultipartUploadAsync":
                    return Task.FromResult(Initiate((InitiateMultipartUploadRequest)args![0]!));
                case "UploadPartAsync":
                    return UploadPartAsync((UploadPartRequest)args![0]!, (CancellationToken)args[1]!);
                case "CompleteMultipartUploadAsync":
                    return Task.FromResult(Complete((CompleteMultipartUploadRequest)args![0]!));
                case "AbortMultipartUploadAsync":
                    return Task.FromResult(Abort((AbortMultipartUploadRequest)args![0]!));
                case "DeleteObjectsAsync":
                    return Task.FromResult(Delete((DeleteObjectsRequest)args![0]!));
                case "HeadBucketAsync":
                    return Task.FromResult(new HeadBucketResponse { BucketRegion = BucketRegion });
                case "GetBucketVersioningAsync":
                    return Task.FromResult(new GetBucketVersioningResponse { VersioningConfig = new S3BucketVersioningConfig { Status = Versioning } });
                case "GetLifecycleConfigurationAsync":
                    return Task.FromResult(new GetLifecycleConfigurationResponse { Configuration = new LifecycleConfiguration { Rules = Lifecycle } });
                case "DeleteObjectAsync":
                    if (((CancellationToken)args![1]!).IsCancellationRequested)
                        return Fail(targetMethod, new OperationCanceledException((CancellationToken)args[1]!));
                    var deleteKey = ((DeleteObjectRequest)args[0]!).Key;
                    DeleteRequests.Add(deleteKey);
                    Objects.RemoveAll(o => o.Key == deleteKey);
                    return Task.FromResult(new DeleteObjectResponse());
                case "Dispose":
                    Disposed = true;
                    return null;
                default:
                    throw new NotImplementedException(name);
            }
        }
        catch (AmazonServiceException ex)
        {
            return Fail(targetMethod, ex);
        }
    }

    private static object Fail(MethodInfo method, Exception ex)
    {
        var resultType = method.ReturnType.GetGenericArguments()[0];
        var source = typeof(TaskCompletionSource<>).MakeGenericType(resultType);
        var instance = Activator.CreateInstance(source)!;
        source.GetMethod("SetException", [typeof(Exception)])!.Invoke(instance, [ex]);
        return source.GetProperty("Task")!.GetValue(instance)!;
    }

    private ListObjectsV2Response List(ListObjectsV2Request request)
    {
        ListRequests.Add(request);
        var prefix = request.Prefix ?? "";
        var matching = Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(o => o.Key, StringComparer.Ordinal);

        // Delimiter grouping: keys with a delimiter after the prefix collapse into one common prefix.
        var entries = new List<(string? Common, Obj? Object)>();
        var seen = new HashSet<string>();
        foreach (var o in matching)
        {
            var rest = o.Key[prefix.Length..];
            var delimiter = request.Delimiter;
            var cut = !string.IsNullOrEmpty(delimiter) ? rest.IndexOf(delimiter, StringComparison.Ordinal) : -1;
            if (cut < 0) entries.Add((null, o));
            else
            {
                var common = prefix + rest[..(cut + delimiter!.Length)];
                if (seen.Add(common)) entries.Add((common, null));
            }
        }

        var start = request.ContinuationToken is null ? 0 : int.Parse(request.ContinuationToken);
        var size = Math.Min(PageSize, request.MaxKeys ?? int.MaxValue);
        var page = entries.Skip(start).Take(size).ToList();
        var more = start + size < entries.Count;
        return new ListObjectsV2Response
        {
            IsTruncated = more || TruncatedWithoutToken,
            NextContinuationToken = more && !TruncatedWithoutToken ? (start + size).ToString() : null,
            S3Objects = page.Where(e => e.Object is not null)
                .Select(e => new S3Object { Key = e.Object!.Key, Size = e.Object.Size, ETag = e.Object.ETag, LastModified = e.Object.LastModified }).ToList(),
            CommonPrefixes = page.Where(e => e.Common is not null).Select(e => e.Common!).ToList(),
        };
    }

    private GetObjectMetadataResponse Head(GetObjectMetadataRequest request)
    {
        HeadRequests.Add(request);
        BeforeHead?.Invoke();
        var o = Objects.FirstOrDefault(x => x.Key == request.Key)
            ?? throw new AmazonS3Exception("not found", ErrorType.Unknown, "NotFound", "req", System.Net.HttpStatusCode.NotFound);
        return new GetObjectMetadataResponse { ContentLength = o.Size, ETag = o.ETag, LastModified = o.LastModified };
    }

    private GetObjectResponse Get(GetObjectRequest request)
    {
        var o = Objects.FirstOrDefault(x => x.Key == request.Key)
            ?? throw new AmazonS3Exception("no such key", ErrorType.Unknown, "NoSuchKey", "req", System.Net.HttpStatusCode.NotFound);
        return new GetObjectResponse { ResponseStream = BodyFactory?.Invoke(o) ?? new MemoryStream(o.Body ?? []) };
    }

    private static AmazonS3Exception PreconditionFailed() =>
        new("At least one of the pre-conditions you specified did not hold", ErrorType.Sender, "PreconditionFailed", "req", System.Net.HttpStatusCode.PreconditionFailed);

    /// <summary>The ETag S3 gives a single-part object: the lowercase hex MD5 of its body.</summary>
    public static string Md5Hex(byte[] body) => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(body));

    /// <summary>The ETag S3 gives a multipart object: the MD5 of the concatenated binary part MD5s, "-", the part count.</summary>
    public static string MultipartETag(IEnumerable<byte[]> parts)
    {
        var list = parts.ToList();
        var md5s = list.SelectMany(p => System.Security.Cryptography.MD5.HashData(p)).ToArray();
        return Md5Hex(md5s) + "-" + list.Count;
    }

    public static string Quote(string etag) => "\"" + etag + "\"";

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary><c>If-Match</c>: the object must exist (else 404) and carry exactly this ETag (quotes ignored), else 412.</summary>
    private void CheckIfMatch(string key, string? ifMatch)
    {
        if (ifMatch is null) return;
        var current = Objects.FirstOrDefault(o => o.Key == key);
        // Like S3: a missing key answers 404 NoSuchKey, a different ETag 412.
        if (current is null) throw new AmazonS3Exception("The specified key does not exist.", ErrorType.Sender, "NoSuchKey", "req", System.Net.HttpStatusCode.NotFound);
        if (current.ETag?.Trim('"') != ifMatch.Trim('"')) throw PreconditionFailed();
    }

    private PutObjectResponse Put(PutObjectRequest request)
    {
        var body = ReadAll(request.InputStream);
        Puts.Add(new Upload(request.Key, body, request.IfNoneMatch, request.Headers.ContentLength, request.IfMatch));
        if (request.IfNoneMatch == "*" && Objects.Any(o => o.Key == request.Key)) throw PreconditionFailed();
        CheckIfMatch(request.Key, request.IfMatch);
        var etag = Quote(Md5Hex(body));
        Objects.RemoveAll(o => o.Key == request.Key);
        Objects.Add(new Obj(request.Key, body.Length, etag, Body: body));
        if (RetryAfterSuccess && request.IfNoneMatch == "*") throw PreconditionFailed();
        AfterPut?.Invoke();
        return new PutObjectResponse { ETag = etag };
    }

    private InitiateMultipartUploadResponse Initiate(InitiateMultipartUploadRequest request)
    {
        Initiates.Add(request);
        var id = "upload-" + Initiates.Count;
        _multipartKeys[id] = request.Key;
        return new InitiateMultipartUploadResponse { BucketName = request.BucketName, Key = request.Key, UploadId = id };
    }

    private async Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request, CancellationToken ct)
    {
        var inFlight = Interlocked.Increment(ref _uploadsInFlight);
        lock (Parts) MaxUploadsInFlight = Math.Max(MaxUploadsInFlight, inFlight);
        try
        {
            var body = ReadAll(request.InputStream);
            if (UploadPartHook is { } hook) await hook(request, ct);
            lock (Parts) Parts.Add((request.PartNumber ?? 0, body, request.PartSize));
            return new UploadPartResponse { PartNumber = request.PartNumber, ETag = Quote(Md5Hex(body)) };
        }
        finally
        {
            Interlocked.Decrement(ref _uploadsInFlight);
        }
    }

    private CompleteMultipartUploadResponse Complete(CompleteMultipartUploadRequest request)
    {
        Completes.Add(request);
        if (request.IfNoneMatch == "*" && Objects.Any(o => o.Key == request.Key)) throw PreconditionFailed();
        CheckIfMatch(request.Key, request.IfMatch);
        var parts = Parts.OrderBy(p => p.PartNumber).ToList();
        var body = parts.SelectMany(p => p.Body).ToArray();
        var etag = Quote(MultipartETag(parts.Select(p => p.Body)));
        Objects.RemoveAll(o => o.Key == request.Key);
        Objects.Add(new Obj(request.Key, body.Length, etag, Body: body));
        if (RetryAfterSuccess && request.IfNoneMatch == "*") throw PreconditionFailed();
        return new CompleteMultipartUploadResponse { Key = request.Key, ETag = etag };
    }

    private AbortMultipartUploadResponse Abort(AbortMultipartUploadRequest request)
    {
        Aborts.Add(request);
        return new AbortMultipartUploadResponse();
    }

    private DeleteObjectsResponse Delete(DeleteObjectsRequest request)
    {
        var keys = request.Objects.Select(o => o.Key).ToList();
        DeleteBatches.Add(keys);
        var errors = new List<DeleteError>();
        foreach (var key in keys)
        {
            if (DeleteErrors.TryGetValue(key, out var code)) errors.Add(new DeleteError { Key = key, Code = code, Message = "failed" });
            else Objects.RemoveAll(o => o.Key == key);
        }
        return new DeleteObjectsResponse { DeleteErrors = errors };
    }
}
