using System.Reflection;
using Amazon.S3;
using Amazon.S3.Model;

namespace ReBackup.Shared.Wpf.Tests.Fakes;

/// <summary>
/// The few <see cref="IAmazonS3"/> calls <c>S3ConnectionTester</c> makes, all succeeding (<c>ListBuckets</c> unless
/// <see cref="ListBucketsError"/> is set); every other call throws
/// <see cref="NotImplementedException"/>. <see cref="Block"/> holds <c>HeadBucket</c> until the caller's token is cancelled.
/// </summary>
public class FakeS3Client : DispatchProxy
{
    /// <summary>The region <c>HeadBucket</c> reports; <c>null</c> = not reported.</summary>
    public string? BucketRegion { get; set; }

    /// <summary>When true, <c>HeadBucket</c> waits for cancellation instead of answering.</summary>
    public bool Block { get; set; }

    /// <summary>What <c>ListBuckets</c> (the account test) throws; <c>null</c> = it succeeds.</summary>
    public Exception? ListBucketsError { get; set; }

    /// <summary>Completes when a blocked <c>HeadBucket</c> has started waiting.</summary>
    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static (IAmazonS3 Client, FakeS3Client Fake) Create()
    {
        var proxy = Create<IAmazonS3, FakeS3Client>();
        return (proxy, (FakeS3Client)(object)proxy);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
    {
        "HeadBucketAsync" => HeadBucketAsync((CancellationToken)args![1]!),
        "ListBucketsAsync" => ListBucketsAsync((CancellationToken)args![1]!),
        "ListObjectsV2Async" => Task.FromResult(new ListObjectsV2Response()),
        "PutObjectAsync" => Task.FromResult(new PutObjectResponse()),
        "DeleteObjectAsync" => Task.FromResult(new DeleteObjectResponse()),
        "GetBucketVersioningAsync" => Task.FromResult(new GetBucketVersioningResponse
        {
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Off },
        }),
        "GetLifecycleConfigurationAsync" => Task.FromResult(new GetLifecycleConfigurationResponse
        {
            Configuration = new LifecycleConfiguration
            {
                Rules =
                [
                    new LifecycleRule
                    {
                        Status = LifecycleRuleStatus.Enabled,
                        AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload { DaysAfterInitiation = 7 },
                    },
                ],
            },
        }),
        "Dispose" => null,
        var name => throw new NotImplementedException(name),
    };

    private Task<ListBucketsResponse> ListBucketsAsync(CancellationToken ct) =>
        ct.IsCancellationRequested ? Task.FromCanceled<ListBucketsResponse>(ct)
        : ListBucketsError is { } error ? Task.FromException<ListBucketsResponse>(error)
        : Task.FromResult(new ListBucketsResponse());

    private async Task<HeadBucketResponse> HeadBucketAsync(CancellationToken ct)
    {
        if (Block)
        {
            Blocked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        return new HeadBucketResponse { BucketRegion = BucketRegion };
    }
}
