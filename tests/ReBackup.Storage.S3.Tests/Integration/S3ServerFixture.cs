using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using ReBackup.Storage.S3.Connections;
using Testcontainers.LocalStack;

namespace ReBackup.Storage.S3.Tests.Integration;

/// <summary>The integration tests share one S3-compatible server; xUnit runs the tests of a collection one after another.</summary>
[CollectionDefinition(Name)]
public sealed class S3ServerCollection : ICollectionFixture<S3ServerFixture>
{
    public const string Name = "s3server";
}

/// <summary>
/// An S3-compatible server in a Docker container (Testcontainers) with one bucket for the tests. Without a reachable Docker the
/// fixture is not <see cref="Available"/> and the tests skip themselves; any other start failure fails them.
/// </summary>
public sealed class S3ServerFixture : IAsyncLifetime
{
    // The official MinIO images are no longer pullable (Docker Hub and quay.io), and third-party MinIO forks are not used.
    // LocalStack 4.9 is the vendor's official image and supports conditional writes (If-None-Match: *) on PutObject and
    // CompleteMultipartUpload, as S3StorageIntegrationTests proves. It does not serialize two concurrent conditional
    // CompleteMultipartUpload calls, so that one race test is skipped.
    private const string Image = "localstack/localstack:4.9";

    // LocalStack accepts any credentials; the region only has to be a valid one.
    private const string Region = "us-east-1";
    private const string AccessKey = "test";
    private const string SecretKey = "test";

    private LocalStackContainer? _container;
    private AmazonS3Client? _client;

    public bool Available { get; private set; }
    public string? SkipReason { get; private set; }

    /// <summary>The client every storage of this fixture uses: path-style requests to the container's endpoint.</summary>
    public IAmazonS3 Client => _client ?? throw new InvalidOperationException(SkipReason ?? "The S3 server is not running.");

    /// <summary>The bucket <see cref="CreateStorage(string)"/> works in.</summary>
    public string Bucket { get; } = NewBucketName();

    public async Task InitializeAsync()
    {
        // xUnit does not dispose a fixture whose start failed, so a half-started container and client are cleaned up here.
        try
        {
            _container = new LocalStackBuilder(Image).Build();
            await _container.StartAsync();
            _client = new AmazonS3Client(
                new BasicAWSCredentials(AccessKey, SecretKey),
                new AmazonS3Config { ServiceURL = _container.GetConnectionString(), ForcePathStyle = true, AuthenticationRegion = Region });
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
            Available = true;
        }
        catch (DockerUnavailableException ex)
        {
            await DisposeAsync();
            SkipReason = $"Docker is not available: {ex.Message}";
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        if (_container is not null) await _container.DisposeAsync();
        _container = null;
    }

    /// <summary>A storage rooted at <paramref name="prefix"/> in <see cref="Bucket"/>.</summary>
    public S3Storage CreateStorage(string prefix) => CreateStorage(Bucket, prefix);

    /// <summary>A storage rooted at <paramref name="prefix"/> in <paramref name="bucket"/>.</summary>
    public S3Storage CreateStorage(string bucket, string prefix) =>
        new(new S3Connection("it", "integration", Region, bucket, AccessKey, SecretKey), prefix, Client);

    /// <summary>A connection to <paramref name="bucket"/> on this server.</summary>
    public S3Connection CreateConnection(string bucket) => new("it", "integration", Region, bucket, AccessKey, SecretKey);

    /// <summary>A tester whose clients talk to this server (the tester disposes each client it creates, so every call builds a new one).</summary>
    public S3ConnectionTester CreateTester()
    {
        var endpoint = _container?.GetConnectionString() ?? throw new InvalidOperationException(SkipReason ?? "The S3 server is not running.");
        return new S3ConnectionTester(c => new AmazonS3Client(
            new BasicAWSCredentials(c.AccessKeyId, c.Secret),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = c.Region }));
    }

    /// <summary>Creates a fresh, empty bucket (for tests that use the whole bucket as the storage root).</summary>
    public async Task<string> CreateBucketAsync()
    {
        var bucket = NewBucketName();
        await Client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        return bucket;
    }

    private static string NewBucketName() => "rebackup-" + Guid.NewGuid().ToString("N");
}
