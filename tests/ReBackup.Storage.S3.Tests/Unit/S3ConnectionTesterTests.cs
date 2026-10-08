using System.Net;
using System.Reflection;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Transform;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using ReBackup.Storage.S3.Connections;
using ReBackup.Storage.S3.Tests.Unit.Fakes;

namespace ReBackup.Storage.S3.Tests.Unit;

public class S3ConnectionTesterTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly S3Connection Conn = new("c1", "n", "eu-central-1", "bucket", "AKIA", "super-secret");

    /// <summary>A response that only knows its headers, as the SDK keeps it on the inner exception of a bodyless HEAD error.</summary>
    private class HeaderResponse : DispatchProxy
    {
        public Dictionary<string, string> Headers { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "IsHeaderPresent" => Headers.ContainsKey((string)args![0]!),
            "GetHeaderValue" => Headers.GetValueOrDefault((string)args![0]!),
            "get_StatusCode" => HttpStatusCode.MovedPermanently,
            _ => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null,
        };
    }

    private static AmazonS3Exception S3(string code, HttpStatusCode status, string? region = null)
    {
        Exception? inner = null;
        if (region is not null)
        {
            var response = DispatchProxy.Create<IWebResponseData, HeaderResponse>();
            ((HeaderResponse)(object)response).Headers["x-amz-bucket-region"] = region;
            inner = new HttpErrorResponseException(response);
        }
        return new AmazonS3Exception("boom super-secret", inner, ErrorType.Unknown, code, "req", status);
    }

    private static (S3ConnectionTester Tester, FakeS3Client Fake) Create()
    {
        var (client, fake) = FakeS3Client.Create();
        fake.Lifecycle = [Rule(abort: true)];
        return (new S3ConnectionTester(_ => client), fake);
    }

    private static LifecycleRule Rule(bool abort, bool enabled = true) => new()
    {
        Status = enabled ? LifecycleRuleStatus.Enabled : LifecycleRuleStatus.Disabled,
        AbortIncompleteMultipartUpload = abort ? new LifecycleRuleAbortIncompleteMultipartUpload { DaysAfterInitiation = 7 } : null,
    };

    private static S3CheckResult Of(IReadOnlyList<S3CheckResult> results, S3Check check) => results.Single(r => r.Check == check);

    [Fact]
    public async Task All_checks_ok()
    {
        var (tester, fake) = Create();
        fake.BucketRegion = "eu-central-1";

        var results = await tester.RunAsync(Conn, checkWrite: true, Ct);

        results.Select(r => r.Check).Should().Equal(S3Check.Bucket, S3Check.Versioning, S3Check.List, S3Check.WriteDelete, S3Check.Lifecycle);
        results.Should().OnlyContain(r => r.State == S3CheckState.Ok && r.MessageKey == S3MessageKeys.Ok);
        fake.Puts.Should().ContainSingle().Which.Key.Should().StartWith(".rebackup-connection-test-");
        fake.Objects.Should().BeEmpty("the test object is deleted again");
        fake.ListRequests.Single().MaxKeys.Should().Be(1);
        fake.ListRequests.Single().Prefix.Should().BeNull();
    }

    [Fact]
    public async Task A_prefix_scopes_the_list_and_the_test_object()
    {
        var (tester, fake) = Create();
        fake.Add("other/x").Add("wp/elvora/backup.zip");

        var results = await tester.RunAsync(Conn, "wp/elvora", checkWrite: true, Ct);

        results.Should().OnlyContain(r => r.State == S3CheckState.Ok);
        fake.ListRequests.Single().Prefix.Should().Be("wp/elvora/");
        fake.Puts.Should().ContainSingle().Which.Key.Should().StartWith("wp/elvora/.rebackup-connection-test-");
        fake.DeleteRequests.Should().ContainSingle().Which.Should().Be(fake.Puts[0].Key);
        fake.Objects.Select(o => o.Key).Should().BeEquivalentTo("other/x", "wp/elvora/backup.zip");
    }

    [Fact]
    public async Task An_empty_prefix_is_the_whole_bucket()
    {
        var (tester, fake) = Create();

        await tester.RunAsync(Conn, "", checkWrite: true, Ct);

        fake.ListRequests.Single().Prefix.Should().BeNull();
        fake.Puts.Should().ContainSingle().Which.Key.Should().StartWith(".rebackup-connection-test-");
    }

    [Theory]
    [InlineData("/wp")]
    [InlineData("wp/")]
    [InlineData("wp//elvora")]
    [InlineData("wp/../x")]
    public async Task An_invalid_prefix_is_rejected_before_any_request(string prefix)
    {
        var created = false;
        var tester = new S3ConnectionTester(_ =>
        {
            created = true;
            return FakeS3Client.Create().Client;
        });

        var act = () => tester.RunAsync(Conn, prefix, checkWrite: true, Ct);

        await act.Should().ThrowAsync<ArgumentException>();
        created.Should().BeFalse();
    }

    [Fact]
    public async Task Region_mismatch_in_the_response_is_a_warning_with_the_real_region()
    {
        var (tester, fake) = Create();
        fake.BucketRegion = "us-west-2";

        var results = await tester.RunAsync(Conn, true, Ct);

        var bucket = Of(results, S3Check.Bucket);
        bucket.State.Should().Be(S3CheckState.Warning);
        bucket.MessageKey.Should().Be(S3MessageKeys.RegionMismatch);
        bucket.Detail.Should().Be("us-west-2");
        Of(results, S3Check.List).State.Should().Be(S3CheckState.Ok);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently, "PermanentRedirect")]
    [InlineData(HttpStatusCode.BadRequest, "AuthorizationHeaderMalformed")]
    public async Task Region_mismatch_in_the_error_is_a_warning_with_the_real_region(HttpStatusCode status, string code)
    {
        var (tester, fake) = Create();
        fake.Failures["HeadBucketAsync"] = S3(code, status, region: "ap-south-1");

        var results = await tester.RunAsync(Conn, true, Ct);

        var bucket = Of(results, S3Check.Bucket);
        bucket.State.Should().Be(S3CheckState.Warning);
        bucket.MessageKey.Should().Be(S3MessageKeys.RegionMismatch);
        bucket.Detail.Should().Be("ap-south-1");
    }

    [Fact]
    public async Task Same_region_in_a_different_case_is_ok()
    {
        var (tester, fake) = Create();
        fake.BucketRegion = "EU-Central-1";
        (await tester.RunAsync(Conn, true, Ct)).Should().OnlyContain(r => r.State == S3CheckState.Ok);
    }

    [Fact]
    public async Task Bucket_access_denied_skips_the_rest()
    {
        var (tester, fake) = Create();
        fake.Failures["HeadBucketAsync"] = S3("Forbidden", HttpStatusCode.Forbidden);

        var results = await tester.RunAsync(Conn, true, Ct);

        Of(results, S3Check.Bucket).Should().Be(new S3CheckResult(S3Check.Bucket, S3CheckState.Failed, S3MessageKeys.AccessDenied, null));
        foreach (var check in new[] { S3Check.Versioning, S3Check.List, S3Check.WriteDelete, S3Check.Lifecycle })
            Of(results, check).Should().Be(new S3CheckResult(check, S3CheckState.Skipped, S3MessageKeys.Skipped, null));
        fake.Puts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NotFound", S3MessageKeys.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown", S3MessageKeys.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, "Whatever", S3MessageKeys.Failed)]
    public async Task Bucket_failures_are_classified(HttpStatusCode status, string code, string key)
    {
        var (tester, fake) = Create();
        fake.Failures["HeadBucketAsync"] = S3(code, status);

        var bucket = Of(await tester.RunAsync(Conn, true, Ct), S3Check.Bucket);

        bucket.State.Should().Be(S3CheckState.Failed);
        bucket.MessageKey.Should().Be(key);
    }

    [Fact]
    public async Task Network_failure_is_unavailable()
    {
        var (tester, fake) = Create();
        fake.Failures["HeadBucketAsync"] = new HttpRequestException("no route");

        Of(await tester.RunAsync(Conn, true, Ct), S3Check.Bucket).MessageKey.Should().Be(S3MessageKeys.Unavailable);
    }

    [Fact]
    public async Task Failed_list_skips_write_but_lifecycle_still_runs()
    {
        var (tester, fake) = Create();
        fake.Failures["ListObjectsV2Async"] = S3("AccessDenied", HttpStatusCode.Forbidden);

        var results = await tester.RunAsync(Conn, true, Ct);

        Of(results, S3Check.Bucket).State.Should().Be(S3CheckState.Ok);
        Of(results, S3Check.List).State.Should().Be(S3CheckState.Failed);
        Of(results, S3Check.WriteDelete).State.Should().Be(S3CheckState.Skipped);
        Of(results, S3Check.Lifecycle).State.Should().Be(S3CheckState.Ok);
        Of(results, S3Check.Versioning).State.Should().Be(S3CheckState.Ok);
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Suspended")]
    public async Task A_versioned_bucket_is_a_warning(string status)
    {
        // DeleteObjects without a version id only adds delete markers there: deleting frees no space.
        var (tester, fake) = Create();
        fake.Versioning = VersionStatus.FindValue(status);

        var results = await tester.RunAsync(Conn, true, Ct);

        Of(results, S3Check.Versioning).Should().Be(new S3CheckResult(S3Check.Versioning, S3CheckState.Warning, S3MessageKeys.VersioningEnabled, null));
        results.Where(r => r.Check != S3Check.Versioning).Should().OnlyContain(r => r.State == S3CheckState.Ok);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Off")]
    public async Task An_unversioned_bucket_is_ok(string? status)
    {
        var (tester, fake) = Create();
        fake.Versioning = status is null ? null : VersionStatus.FindValue(status);

        Of(await tester.RunAsync(Conn, true, Ct), S3Check.Versioning).Should().Be(new S3CheckResult(S3Check.Versioning, S3CheckState.Ok, S3MessageKeys.Ok, null));
    }

    [Fact]
    public async Task Versioning_access_denied_is_skipped_as_not_checkable()
    {
        var (tester, fake) = Create();
        fake.Failures["GetBucketVersioningAsync"] = S3("AccessDenied", HttpStatusCode.Forbidden);

        var results = await tester.RunAsync(Conn, true, Ct);

        Of(results, S3Check.Versioning).Should().Be(new S3CheckResult(S3Check.Versioning, S3CheckState.Skipped, S3MessageKeys.NotCheckable, null));
        Of(results, S3Check.List).State.Should().Be(S3CheckState.Ok, "an unreadable versioning state stops nothing");
    }

    [Fact]
    public async Task Other_versioning_failures_are_classified_and_stop_nothing()
    {
        var (tester, fake) = Create();
        fake.Failures["GetBucketVersioningAsync"] = S3("InternalError", HttpStatusCode.InternalServerError);

        var results = await tester.RunAsync(Conn, true, Ct);

        Of(results, S3Check.Versioning).Should().Be(new S3CheckResult(S3Check.Versioning, S3CheckState.Failed, S3MessageKeys.Unavailable, null));
        results.Where(r => r.Check != S3Check.Versioning).Should().OnlyContain(r => r.State == S3CheckState.Ok);
    }

    [Fact]
    public async Task Versioning_runs_after_a_region_warning()
    {
        var (tester, fake) = Create();
        fake.BucketRegion = "us-west-2";
        fake.Versioning = VersionStatus.Enabled;

        Of(await tester.RunAsync(Conn, true, Ct), S3Check.Versioning).State.Should().Be(S3CheckState.Warning);
    }

    [Fact]
    public async Task Write_denied_fails_the_write_check()
    {
        var (tester, fake) = Create();
        fake.Failures["PutObjectAsync"] = S3("AccessDenied", HttpStatusCode.Forbidden);

        var write = Of(await tester.RunAsync(Conn, true, Ct), S3Check.WriteDelete);

        write.State.Should().Be(S3CheckState.Failed);
        write.MessageKey.Should().Be(S3MessageKeys.AccessDenied);
    }

    [Fact]
    public async Task Delete_denied_fails_the_write_check()
    {
        var (tester, fake) = Create();
        fake.Failures["DeleteObjectAsync"] = S3("AccessDenied", HttpStatusCode.Forbidden);

        Of(await tester.RunAsync(Conn, true, Ct), S3Check.WriteDelete).State.Should().Be(S3CheckState.Failed);
    }

    [Fact]
    public async Task Without_checkWrite_nothing_is_written()
    {
        var (tester, fake) = Create();

        var results = await tester.RunAsync(Conn, checkWrite: false, Ct);

        Of(results, S3Check.WriteDelete).Should().Be(new S3CheckResult(S3Check.WriteDelete, S3CheckState.Skipped, S3MessageKeys.Skipped, null));
        fake.Puts.Should().BeEmpty();
        Of(results, S3Check.Lifecycle).State.Should().Be(S3CheckState.Ok);
    }

    [Theory]
    [InlineData(false, false)] // no rules at all
    [InlineData(true, false)]  // a rule without abort
    [InlineData(true, true)]   // an abort rule that is disabled
    public async Task Missing_abort_rule_is_a_warning(bool anyRule, bool disabledAbort)
    {
        var (tester, fake) = Create();
        fake.Lifecycle = !anyRule ? [] : disabledAbort ? [Rule(abort: true, enabled: false)] : [Rule(abort: false)];

        var lifecycle = Of(await tester.RunAsync(Conn, true, Ct), S3Check.Lifecycle);

        lifecycle.State.Should().Be(S3CheckState.Warning);
        lifecycle.MessageKey.Should().Be(S3MessageKeys.LifecycleMissing);
    }

    [Fact]
    public async Task No_lifecycle_configuration_is_a_warning()
    {
        var (tester, fake) = Create();
        fake.Failures["GetLifecycleConfigurationAsync"] = S3("NoSuchLifecycleConfiguration", HttpStatusCode.NotFound);

        var lifecycle = Of(await tester.RunAsync(Conn, true, Ct), S3Check.Lifecycle);

        lifecycle.State.Should().Be(S3CheckState.Warning);
        lifecycle.MessageKey.Should().Be(S3MessageKeys.LifecycleMissing);
    }

    [Fact]
    public async Task Lifecycle_access_denied_is_skipped_as_not_checkable()
    {
        var (tester, fake) = Create();
        fake.Failures["GetLifecycleConfigurationAsync"] = S3("AccessDenied", HttpStatusCode.Forbidden);

        var lifecycle = Of(await tester.RunAsync(Conn, true, Ct), S3Check.Lifecycle);

        lifecycle.Should().Be(new S3CheckResult(S3Check.Lifecycle, S3CheckState.Skipped, S3MessageKeys.NotCheckable, null));
    }

    [Fact]
    public async Task Other_lifecycle_failures_are_classified_as_failed()
    {
        var (tester, fake) = Create();
        fake.Failures["GetLifecycleConfigurationAsync"] = S3("InternalError", HttpStatusCode.InternalServerError);

        var lifecycle = Of(await tester.RunAsync(Conn, true, Ct), S3Check.Lifecycle);

        lifecycle.State.Should().Be(S3CheckState.Failed);
        lifecycle.MessageKey.Should().Be(S3MessageKeys.Unavailable);
    }

    [Fact]
    public async Task Results_never_contain_the_secret()
    {
        var (tester, fake) = Create();
        fake.Failures["HeadBucketAsync"] = S3("Forbidden", HttpStatusCode.Forbidden);

        var results = await tester.RunAsync(Conn, true, Ct);

        results.Should().OnlyContain(r => !(r.MessageKey ?? "").Contains("super-secret") && !(r.Detail ?? "").Contains("super-secret"));
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var (tester, fake) = Create();
        using var cts = new CancellationTokenSource();
        fake.Failures["ListObjectsV2Async"] = new OperationCanceledException(cts.Token);
        await cts.CancelAsync();

        var act = () => tester.RunAsync(Conn, true, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Cancellation_during_the_bucket_check_propagates()
    {
        var (tester, fake) = Create();
        using var cts = new CancellationTokenSource();
        fake.Failures["HeadBucketAsync"] = new OperationCanceledException(cts.Token);
        await cts.CancelAsync();

        var act = () => tester.RunAsync(Conn, true, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Cancellation_after_the_put_still_deletes_the_test_object()
    {
        var (tester, fake) = Create();
        using var cts = new CancellationTokenSource();
        fake.AfterPut = () => cts.Cancel();

        var act = () => tester.RunAsync(Conn, true, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fake.Puts.Should().ContainSingle();
        fake.Objects.Should().BeEmpty("the test object is removed although the run was cancelled");
    }

    [Fact]
    public async Task A_failed_put_still_tries_to_delete_the_test_object()
    {
        var (tester, fake) = Create();
        fake.Failures["PutObjectAsync"] = new HttpRequestException("timeout after the object was stored");

        var write = Of(await tester.RunAsync(Conn, true, Ct), S3Check.WriteDelete);

        write.State.Should().Be(S3CheckState.Failed);
        fake.DeleteRequests.Should().ContainSingle().Which.Should().StartWith(".rebackup-connection-test-");
    }

    [Theory]
    [InlineData("eu-central")]
    [InlineData("not a region!")]
    [InlineData("EU-CENTRAL-1")]
    public async Task An_invalid_region_fails_the_bucket_check_without_a_client(string region)
    {
        // The default client factory: the SDK would throw its own exception or build a bogus endpoint.
        var results = await new S3ConnectionTester().RunAsync(Conn with { Region = region }, checkWrite: true, Ct);

        results.Select(r => r.Check).Should().Equal(Enum.GetValues<S3Check>());
        Of(results, S3Check.Bucket).Should().Be(new S3CheckResult(S3Check.Bucket, S3CheckState.Failed, S3MessageKeys.RegionInvalid, null));
        results.Where(r => r.Check != S3Check.Bucket).Should().OnlyContain(r => r.State == S3CheckState.Skipped && r.MessageKey == S3MessageKeys.Skipped);
    }

    [Fact]
    public async Task An_invalid_region_never_reaches_an_injected_client_factory_either()
    {
        var created = false;
        var tester = new S3ConnectionTester(_ =>
        {
            created = true;
            return FakeS3Client.Create().Client;
        });

        var results = await tester.RunAsync(Conn with { Region = "eu-central" }, checkWrite: true, Ct);

        created.Should().BeFalse();
        Of(results, S3Check.Bucket).MessageKey.Should().Be(S3MessageKeys.RegionInvalid);
    }

    [Fact]
    public async Task Connection_without_secret_is_rejected()
    {
        var (tester, _) = Create();
        var act = () => tester.RunAsync(Conn with { Secret = null }, true, Ct);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task The_client_is_disposed_after_the_run()
    {
        var (tester, fake) = Create();

        await tester.RunAsync(Conn, true, Ct);

        fake.Disposed.Should().BeTrue();
    }
}
