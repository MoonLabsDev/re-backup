using System.Runtime.ExceptionServices;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3;
using Amazon.S3.Model;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Storage.S3;

/// <summary>
/// What <see cref="S3ConnectionTester"/> checks: <c>Account</c> alone for an account (<see cref="S3ConnectionTester.RunAccountAsync"/>),
/// the others in this order for a connection.
/// </summary>
public enum S3Check { Account, Bucket, Versioning, List, WriteDelete, Lifecycle }

/// <summary>How a check ended. <c>Warning</c> works but needs attention, <c>Skipped</c> was not run or cannot be judged.</summary>
public enum S3CheckState { Ok, Failed, Warning, Skipped }

/// <summary>The outcome of one check: <paramref name="MessageKey"/> is an <see cref="S3MessageKeys"/> value, <paramref name="Detail"/> extra data for the text (the bucket's region for a region warning). Never holds the secret.</summary>
public sealed record S3CheckResult(S3Check Check, S3CheckState State, string? MessageKey, string? Detail);

/// <summary>
/// Probes an S3 connection: bucket reachable, versioning off (deletes free space), listing, writing and deleting, and the lifecycle
/// rule that cleans up abandoned multipart uploads.
/// </summary>
public sealed class S3ConnectionTester
{
    private readonly Func<S3Connection, IAmazonS3> _clientFactory;

    /// <param name="clientFactory">Builds the client for a run (tests); the tester disposes it. Default: a client for the connection's region and access key.</param>
    public S3ConnectionTester(Func<S3Connection, IAmazonS3>? clientFactory = null)
    {
        _clientFactory = clientFactory ?? (c => new AmazonS3Client(
            new BasicAWSCredentials(c.AccessKeyId, c.Secret),
            RegionEndpoint.GetBySystemName(c.Region)));
    }

    /// <summary>
    /// Runs the checks on the whole bucket and returns one result per <see cref="S3Check"/>, in order. A failed check skips the ones
    /// after it, except Versioning and Lifecycle, which run whenever Bucket did not fail. A region that is no AWS region name fails Bucket with
    /// <see cref="S3MessageKeys.RegionInvalid"/> before any client is built. With <paramref name="checkWrite"/> false, WriteDelete is
    /// skipped. Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    public Task<IReadOnlyList<S3CheckResult>> RunAsync(S3Connection connection, bool checkWrite, CancellationToken ct) =>
        RunAsync(connection, "", checkWrite, ct);

    /// <summary>
    /// Runs the checks like <see cref="RunAsync(S3Connection, bool, CancellationToken)"/>, with List and WriteDelete scoped to
    /// <paramref name="prefix"/> (a storage path, <c>""</c> = the whole bucket): the listing asks for <c>prefix/</c> and the test
    /// object is <c>prefix/.rebackup-connection-test-…</c>, so a key whose permissions are limited to the prefix passes.
    /// Throws <see cref="ArgumentException"/> for a prefix that is no valid storage path.
    /// </summary>
    public async Task<IReadOnlyList<S3CheckResult>> RunAsync(S3Connection connection, string prefix, bool checkWrite, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(prefix);
        if (connection.NeedsSecret) throw new ArgumentException("The connection has no secret.", nameof(connection));
        prefix = S3Keys.NormalizePrefix(prefix);

        // No client for a region that is no region name: the SDK would throw its own exception or build a bogus endpoint.
        if (!S3Regions.IsValid(connection.Region))
            return [new S3CheckResult(S3Check.Bucket, S3CheckState.Failed, S3MessageKeys.RegionInvalid, null),
                ..ConnectionChecks.Where(c => c != S3Check.Bucket).Select(Skipped)];

        var client = _clientFactory(connection);
        try
        {
            var bucket = await CheckBucketAsync(client, connection, ct).ConfigureAwait(false);
            var bucketUsable = bucket.State is S3CheckState.Ok or S3CheckState.Warning;
            var versioning = bucketUsable ? await CheckVersioningAsync(client, connection, ct).ConfigureAwait(false) : Skipped(S3Check.Versioning);

            var list = bucketUsable ? await CheckListAsync(client, connection, prefix, ct).ConfigureAwait(false) : Skipped(S3Check.List);
            var listOk = list.State == S3CheckState.Ok;

            var write = !checkWrite || !listOk ? Skipped(S3Check.WriteDelete) : await CheckWriteAsync(client, connection, prefix, ct).ConfigureAwait(false);
            var lifecycle = bucketUsable ? await CheckLifecycleAsync(client, connection, ct).ConfigureAwait(false) : Skipped(S3Check.Lifecycle);

            return [bucket, versioning, list, write, lifecycle];
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>The checks <see cref="RunAsync(S3Connection, string, bool, CancellationToken)"/> returns, in order.</summary>
    private static readonly S3Check[] ConnectionChecks = [S3Check.Bucket, S3Check.Versioning, S3Check.List, S3Check.WriteDelete, S3Check.Lifecycle];

    /// <summary>The region an account is checked in: <c>ListBuckets</c> works from any region and is answered by the global endpoint.</summary>
    private const string AccountRegion = "us-east-1";

    /// <summary>
    /// Checks only the credentials of <paramref name="account"/> with <c>ListBuckets</c> in <c>us-east-1</c>: success is Ok;
    /// <c>AccessDenied</c> is a Warning with <see cref="S3MessageKeys.AccountNoList"/> (the key is valid but may not list buckets,
    /// normal for bucket-scoped policies); <c>InvalidAccessKeyId</c> / <c>SignatureDoesNotMatch</c> fail with
    /// <see cref="S3MessageKeys.AccessDenied"/>, network errors with <see cref="S3MessageKeys.Unavailable"/>. The result has
    /// <see cref="S3Check.Account"/>. Throws <see cref="ArgumentException"/> for an account without secret and
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<S3CheckResult> RunAccountAsync(S3Account account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.NeedsSecret) throw new ArgumentException("The account has no secret.", nameof(account));

        // The client factory takes a connection: one without bucket, in the region ListBuckets is sent to.
        var client = _clientFactory(new S3Connection(account.Id, account.Name, AccountRegion, "", account.AccessKeyId, account.Secret));
        try
        {
            await client.ListBucketsAsync(new ListBucketsRequest(), ct).ConfigureAwait(false);
            return Ok(S3Check.Account);
        }
        catch (AmazonServiceException ex) when (!ct.IsCancellationRequested && ex.ErrorCode == "AccessDenied")
        {
            return new S3CheckResult(S3Check.Account, S3CheckState.Warning, S3MessageKeys.AccountNoList, null);
        }
        catch (Exception ex)
        {
            return Failure(S3Check.Account, ex, "", ct);
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task<S3CheckResult> CheckBucketAsync(IAmazonS3 client, S3Connection connection, CancellationToken ct)
    {
        try
        {
            var head = await client.HeadBucketAsync(new HeadBucketRequest { BucketName = connection.Bucket }, ct).ConfigureAwait(false);
            return BucketResult(connection, head.BucketRegion);
        }
        catch (AmazonS3Exception ex) when (!ct.IsCancellationRequested && RegionOf(ex) is not null)
        {
            // The SDK follows a wrong-region redirect itself (the response case above); when it cannot, the error (301/400) names the bucket's region.
            return BucketResult(connection, RegionOf(ex));
        }
        catch (Exception ex)
        {
            return Failure(S3Check.Bucket, ex, connection.Bucket, ct);
        }
    }

    /// <summary>
    /// The bucket's real region as a wrong-region error names it: the <c>x-amz-bucket-region</c> header of the HTTP response, which the
    /// SDK keeps on the inner <see cref="HttpErrorResponseException"/> (a HEAD response has no body to read it from).
    /// </summary>
    private static string? RegionOf(AmazonS3Exception ex) =>
        ex.InnerException is HttpErrorResponseException { Response: { } response } && response.IsHeaderPresent(RegionHeader)
            && response.GetHeaderValue(RegionHeader) is { Length: > 0 } header
            ? header.Trim()
            : null;

    private const string RegionHeader = "x-amz-bucket-region";

    private static S3CheckResult BucketResult(S3Connection connection, string? bucketRegion) =>
        !string.IsNullOrEmpty(bucketRegion) && !string.Equals(bucketRegion, connection.Region, StringComparison.OrdinalIgnoreCase)
            ? new S3CheckResult(S3Check.Bucket, S3CheckState.Warning, S3MessageKeys.RegionMismatch, bucketRegion)
            : Ok(S3Check.Bucket);

    private static async Task<S3CheckResult> CheckListAsync(IAmazonS3 client, S3Connection connection, string prefix, CancellationToken ct)
    {
        try
        {
            var request = new ListObjectsV2Request { BucketName = connection.Bucket, Prefix = prefix.Length == 0 ? null : prefix + "/", MaxKeys = 1 };
            await client.ListObjectsV2Async(request, ct).ConfigureAwait(false);
            return Ok(S3Check.List);
        }
        catch (Exception ex)
        {
            return Failure(S3Check.List, ex, connection.Bucket, ct);
        }
    }

    private static async Task<S3CheckResult> CheckWriteAsync(IAmazonS3 client, S3Connection connection, string prefix, CancellationToken ct)
    {
        var key = S3Keys.ToKey(prefix, ".rebackup-connection-test-" + Guid.NewGuid().ToString("N"));
        var putDone = false;
        try
        {
            await client.PutObjectAsync(new PutObjectRequest { BucketName = connection.Bucket, Key = key, InputStream = new MemoryStream() }, ct).ConfigureAwait(false);
            putDone = true;
            await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = connection.Bucket, Key = key }, ct).ConfigureAwait(false);
            return Ok(S3Check.WriteDelete);
        }
        catch (Exception ex)
        {
            // A failed or cancelled put may still have created the object (timeout, cancel after the response): remove it, whatever the token says.
            if (!putDone || ct.IsCancellationRequested) await TryDeleteAsync(client, connection.Bucket, key).ConfigureAwait(false);
            return Failure(S3Check.WriteDelete, ex, key, ct);
        }
    }

    /// <summary>Best-effort cleanup: a short timeout of its own, errors swallowed.</summary>
    private static async Task TryDeleteAsync(IAmazonS3 client, string bucket, string key)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Nothing more can be done; the leftover is an empty object.
        }
    }

    /// <summary>
    /// <c>DeleteObjects</c> without a version id only adds delete markers in a versioned bucket (also one whose versioning is
    /// suspended: the old versions stay), so deleting frees no space unless a lifecycle rule expires noncurrent versions.
    /// </summary>
    private static async Task<S3CheckResult> CheckVersioningAsync(IAmazonS3 client, S3Connection connection, CancellationToken ct)
    {
        try
        {
            var response = await client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = connection.Bucket }, ct).ConfigureAwait(false);
            var status = response.VersioningConfig?.Status;
            return status == VersionStatus.Enabled || status == VersionStatus.Suspended
                ? new S3CheckResult(S3Check.Versioning, S3CheckState.Warning, S3MessageKeys.VersioningEnabled, null)
                : Ok(S3Check.Versioning);
        }
        catch (Exception ex)
        {
            return NotCheckableWhenDenied(Failure(S3Check.Versioning, ex, connection.Bucket, ct));
        }
    }

    private static async Task<S3CheckResult> CheckLifecycleAsync(IAmazonS3 client, S3Connection connection, CancellationToken ct)
    {
        try
        {
            var response = await client.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = connection.Bucket }, ct).ConfigureAwait(false);
            var hasAbortRule = response.Configuration?.Rules?.Any(r => r.Status == LifecycleRuleStatus.Enabled && r.AbortIncompleteMultipartUpload is not null) == true;
            return hasAbortRule ? Ok(S3Check.Lifecycle) : Missing();
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchLifecycleConfiguration")
        {
            return Missing();
        }
        catch (Exception ex)
        {
            return NotCheckableWhenDenied(Failure(S3Check.Lifecycle, ex, connection.Bucket, ct));
        }

        static S3CheckResult Missing() => new(S3Check.Lifecycle, S3CheckState.Warning, S3MessageKeys.LifecycleMissing, null);
    }

    /// <summary>Not being allowed to read a bucket configuration is no problem with the connection itself: "cannot be checked".</summary>
    private static S3CheckResult NotCheckableWhenDenied(S3CheckResult failure) =>
        failure.MessageKey == S3MessageKeys.AccessDenied
            ? new S3CheckResult(failure.Check, S3CheckState.Skipped, S3MessageKeys.NotCheckable, null)
            : failure;

    /// <summary>Classifies <paramref name="ex"/> through <see cref="S3Errors"/>; the SDK message (it may carry request details) is dropped.</summary>
    private static S3CheckResult Failure(S3Check check, Exception ex, string path, CancellationToken ct)
    {
        var mapped = S3Errors.Map(ex, path, ct);
        if (mapped is OperationCanceledException cancelled) ExceptionDispatchInfo.Throw(cancelled);
        var key = mapped switch
        {
            StorageAccessDeniedException => S3MessageKeys.AccessDenied,
            StorageNotFoundException => S3MessageKeys.NotFound,
            StorageUnavailableException => S3MessageKeys.Unavailable,
            _ => S3MessageKeys.Failed,
        };
        return new S3CheckResult(check, S3CheckState.Failed, key, null);
    }

    private static S3CheckResult Ok(S3Check check) => new(check, S3CheckState.Ok, S3MessageKeys.Ok, null);

    private static S3CheckResult Skipped(S3Check check) => new(check, S3CheckState.Skipped, S3MessageKeys.Skipped, null);
}
