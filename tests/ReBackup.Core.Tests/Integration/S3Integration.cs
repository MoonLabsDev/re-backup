using Amazon.S3.Model;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Storage;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Tests.Integration;

namespace ReBackup.Core.Tests.Integration;

/// <summary>xUnit finds collection definitions only in the test assembly itself, so the S3 server collection is declared here too.</summary>
[CollectionDefinition(S3ServerCollection.Name)]
public sealed class CoreS3ServerCollection : ICollectionFixture<S3ServerFixture>;

/// <summary>What the LocalStack tests of the runner and the restore share.</summary>
internal static class S3Integration
{
    /// <summary>The id of the one saved connection the test plans name.</summary>
    public const string ConnectionId = "it";

    public const int MiB = 1024 * 1024;

    /// <summary>A plan location on the fixture's bucket, below <paramref name="prefix"/>.</summary>
    public static StorageLocation Location(string prefix) => new(S3StorageFactory.Kind, prefix, ConnectionId);

    /// <summary>
    /// Opens locations as the App does: <c>"s3"</c> locations on the saved connection they name, everything else with the plain
    /// <see cref="StorageFactory"/>. <see cref="S3StorageFactory"/> builds its clients for the AWS endpoints, so the S3 part is composed
    /// here with the fixture's client (path style, LocalStack endpoint); <paramref name="wrap"/> may wrap any opened storage, e.g. in
    /// a <see cref="FaultyStorage"/>.
    /// </summary>
    public static IStorageFactory Storages(S3ServerFixture server, Func<StorageLocation, IStorage, IStorage>? wrap = null) =>
        new TestStorageFactory(location =>
        {
            IStorage storage;
            if (location.Kind == S3StorageFactory.Kind)
            {
                if (location.ConnectionId != ConnectionId)
                    throw new StorageNotFoundException(location.Path, $"The S3 connection '{location.ConnectionId}' was not found.");
                storage = server.CreateStorage(location.Path);
            }
            else
            {
                storage = new StorageFactory().Open(location);
            }
            return wrap?.Invoke(location, storage) ?? storage;
        });

    /// <summary>A fresh key prefix for one test.</summary>
    public static string NewPrefix(string kind) => kind + "-" + Guid.NewGuid().ToString("N");

    /// <summary>Every object key below <paramref name="prefix"/>, relative to it, in order.</summary>
    public static async Task<List<string>> KeysAsync(S3ServerFixture server, string prefix)
    {
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = server.Bucket, Prefix = prefix + "/" };
        ListObjectsV2Response response;
        do
        {
            response = await server.Client.ListObjectsV2Async(request);
            keys.AddRange((response.S3Objects ?? []).Select(o => o.Key[(prefix.Length + 1)..]));
            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    /// <summary>The multipart uploads below <paramref name="prefix"/> that were neither completed nor aborted.</summary>
    public static async Task<List<string>> OpenUploadsAsync(S3ServerFixture server, string prefix)
    {
        var response = await server.Client.ListMultipartUploadsAsync(
            new ListMultipartUploadsRequest { BucketName = server.Bucket, Prefix = prefix + "/" });
        return (response.MultipartUploads ?? []).Select(u => u.Key).ToList();
    }

    /// <summary>Writes <paramref name="data"/> to <paramref name="path"/> in <paramref name="storage"/>, replacing what is there.</summary>
    public static async Task PutAsync(IStorage storage, string path, byte[] data)
    {
        await using var writer = await storage.CreateAsync(path, new CreateOptions(Overwrite: true), CancellationToken.None);
        await writer.WriteAsync(data);
        await writer.CommitAsync(CancellationToken.None);
    }

    /// <summary>The whole content of <paramref name="path"/> in <paramref name="storage"/>.</summary>
    public static async Task<byte[]> ReadAsync(IStorage storage, string path)
    {
        await using var stream = await storage.OpenReadAsync(path, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    public static byte[] RandomData(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}
