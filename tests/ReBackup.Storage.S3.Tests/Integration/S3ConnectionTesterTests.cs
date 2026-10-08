using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Storage.S3.Tests.Integration;

/// <summary>The connection tester against a real S3-compatible server.</summary>
[Collection(S3ServerCollection.Name)]
public sealed class S3ConnectionTesterIntegrationTests(S3ServerFixture server)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static S3CheckResult Of(IReadOnlyList<S3CheckResult> results, S3Check check) => results.Single(r => r.Check == check);

    [SkippableFact]
    public async Task Fresh_bucket_is_ok_except_for_the_missing_lifecycle_rule()
    {
        Skip.IfNot(server.Available, server.SkipReason);
        var bucket = await server.CreateBucketAsync();

        var results = await server.CreateTester().RunAsync(server.CreateConnection(bucket), checkWrite: true, Ct);

        foreach (var check in new[] { S3Check.Bucket, S3Check.Versioning, S3Check.List, S3Check.WriteDelete })
            Of(results, check).State.Should().Be(S3CheckState.Ok, check.ToString());
        Of(results, S3Check.Lifecycle).Should().Be(new S3CheckResult(S3Check.Lifecycle, S3CheckState.Warning, S3MessageKeys.LifecycleMissing, null));

        var left = await server.Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, Ct);
        left.S3Objects.Should().BeNullOrEmpty("the test object is deleted again");
    }

    [SkippableFact]
    public async Task Lifecycle_rule_that_aborts_incomplete_uploads_makes_every_check_ok()
    {
        Skip.IfNot(server.Available, server.SkipReason);
        var bucket = await server.CreateBucketAsync();
        await server.Client.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules =
                [
                    new LifecycleRule
                    {
                        Id = "abort-incomplete",
                        Status = LifecycleRuleStatus.Enabled,
                        Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = "" } },
                        AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload { DaysAfterInitiation = 7 },
                    },
                ],
            },
        }, Ct);

        var results = await server.CreateTester().RunAsync(server.CreateConnection(bucket), checkWrite: true, Ct);

        results.Should().OnlyContain(r => r.State == S3CheckState.Ok);
    }

    [SkippableFact]
    public async Task Versioned_bucket_is_a_warning()
    {
        Skip.IfNot(server.Available, server.SkipReason);
        var bucket = await server.CreateBucketAsync();
        await server.Client.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        }, Ct);

        var results = await server.CreateTester().RunAsync(server.CreateConnection(bucket), checkWrite: true, Ct);

        Of(results, S3Check.Versioning).Should().Be(new S3CheckResult(S3Check.Versioning, S3CheckState.Warning, S3MessageKeys.VersioningEnabled, null));
        foreach (var check in new[] { S3Check.Bucket, S3Check.List, S3Check.WriteDelete })
            Of(results, check).State.Should().Be(S3CheckState.Ok, check.ToString());
    }

    [SkippableFact]
    public async Task Missing_bucket_fails_and_skips_the_rest()
    {
        Skip.IfNot(server.Available, server.SkipReason);

        var results = await server.CreateTester().RunAsync(server.CreateConnection("rebackup-does-not-exist"), checkWrite: true, Ct);

        Of(results, S3Check.Bucket).Should().Be(new S3CheckResult(S3Check.Bucket, S3CheckState.Failed, S3MessageKeys.NotFound, null));
        results.Where(r => r.Check != S3Check.Bucket).Should().OnlyContain(r => r.State == S3CheckState.Skipped);
    }

    [SkippableFact]
    public async Task Account_with_valid_keys_is_ok()
    {
        Skip.IfNot(server.Available, server.SkipReason);
        var connection = server.CreateConnection("unused");

        var result = await server.CreateTester().RunAccountAsync(
            new S3Account("acc", "Account", connection.AccessKeyId, connection.Secret), Ct);

        result.Should().Be(new S3CheckResult(S3Check.Account, S3CheckState.Ok, S3MessageKeys.Ok, null));
    }
}
