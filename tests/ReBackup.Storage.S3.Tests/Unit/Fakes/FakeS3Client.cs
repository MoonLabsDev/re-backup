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
    public List<ListObjectsV2Request> ListRequests { get; } = [];
    public List<GetObjectMetadataRequest> HeadRequests { get; } = [];

    /// <summary>Replaces the body stream a <c>GetObject</c> returns (to inject read failures).</summary>
    public Func<Obj, Stream>? BodyFactory { get; set; }

    /// <summary>Fails the named SDK method (e.g. <c>ListObjectsV2Async</c>) with the given exception.</summary>
    public Dictionary<string, Exception> Failures { get; } = [];

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
                case "Dispose":
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
            IsTruncated = more,
            NextContinuationToken = more ? (start + size).ToString() : null,
            S3Objects = page.Where(e => e.Object is not null)
                .Select(e => new S3Object { Key = e.Object!.Key, Size = e.Object.Size, ETag = e.Object.ETag, LastModified = e.Object.LastModified }).ToList(),
            CommonPrefixes = page.Where(e => e.Common is not null).Select(e => e.Common!).ToList(),
        };
    }

    private GetObjectMetadataResponse Head(GetObjectMetadataRequest request)
    {
        HeadRequests.Add(request);
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
}
