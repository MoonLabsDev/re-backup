using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using FluentAssertions;

namespace ReBackup.Storage.S3.Tests.Unit;

public class S3ErrorsTests
{
    private static AmazonS3Exception S3(string? code, HttpStatusCode status, string message = "boom") =>
        new(message, ErrorType.Unknown, code, "req", status);

    public static TheoryData<string?, HttpStatusCode, Type> Rows => new()
    {
        { "NoSuchKey", HttpStatusCode.NotFound, typeof(StorageNotFoundException) },
        { "NoSuchBucket", HttpStatusCode.NotFound, typeof(StorageNotFoundException) },
        { null, HttpStatusCode.NotFound, typeof(StorageNotFoundException) },
        { "AccessDenied", HttpStatusCode.Forbidden, typeof(StorageAccessDeniedException) },
        { null, HttpStatusCode.Forbidden, typeof(StorageAccessDeniedException) },
        { "InvalidAccessKeyId", HttpStatusCode.Forbidden, typeof(StorageAccessDeniedException) },
        { "SignatureDoesNotMatch", HttpStatusCode.Forbidden, typeof(StorageAccessDeniedException) },
        { "PreconditionFailed", HttpStatusCode.PreconditionFailed, typeof(StorageConflictException) },
        { null, HttpStatusCode.PreconditionFailed, typeof(StorageConflictException) },
        { null, HttpStatusCode.InternalServerError, typeof(StorageUnavailableException) },
        { "SlowDown", HttpStatusCode.ServiceUnavailable, typeof(StorageUnavailableException) },
        { "InvalidObjectState", HttpStatusCode.Forbidden, typeof(StorageIOException) },
        { "Whatever", HttpStatusCode.BadRequest, typeof(StorageIOException) },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Maps_each_aws_condition(string? code, HttpStatusCode status, Type expected)
    {
        var mapped = S3Errors.Map(S3(code, status), "a/b", CancellationToken.None);

        mapped.Should().BeOfType(expected);
        ((StorageException)mapped).Path.Should().Be("a/b");
    }

    [Fact]
    public void Archived_object_message_mentions_restore()
    {
        var mapped = S3Errors.Map(S3("InvalidObjectState", HttpStatusCode.Forbidden), "a", CancellationToken.None);

        mapped.Message.Should().Contain("archived").And.Contain("restored");
    }

    [Fact]
    public void Http_request_exception_is_unavailable() =>
        S3Errors.Map(new HttpRequestException("dns"), "a", CancellationToken.None).Should().BeOfType<StorageUnavailableException>();

    [Fact]
    public void Wrapped_network_failures_are_unavailable()
    {
        var wrapped = new AmazonServiceException("x", new HttpRequestException("dns", new SocketException()));
        var client = new AmazonClientException("x", new IOException("io", new SocketException()));

        S3Errors.Map(wrapped, "a", CancellationToken.None).Should().BeOfType<StorageUnavailableException>();
        S3Errors.Map(client, "a", CancellationToken.None).Should().BeOfType<StorageUnavailableException>();
    }

    [Fact]
    public void Timeout_without_caller_cancellation_is_unavailable()
    {
        using var cts = new CancellationTokenSource();

        S3Errors.Map(new TaskCanceledException(), "a", cts.Token).Should().BeOfType<StorageUnavailableException>();
        S3Errors.Map(new AmazonClientException("t", new TaskCanceledException()), "a", cts.Token).Should().BeOfType<StorageUnavailableException>();
    }

    [Fact]
    public void Caller_cancellation_is_returned_unchanged()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = new OperationCanceledException(cts.Token);

        S3Errors.Map(ex, "a", cts.Token).Should().BeSameAs(ex);
        var tce = new TaskCanceledException("t", null, cts.Token);
        S3Errors.Map(tce, "a", cts.Token).Should().BeSameAs(tce);
    }

    [Fact]
    public void Wrapped_caller_cancellation_returns_the_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var inner = new TaskCanceledException("t", null, cts.Token);

        S3Errors.Map(new AmazonClientException("wrapped", inner), "a", cts.Token).Should().BeSameAs(inner);
    }

    [Fact]
    public void Unknown_exception_becomes_io()
    {
        S3Errors.Map(new AmazonClientException("odd"), "a", CancellationToken.None).Should().BeOfType<StorageIOException>();
        S3Errors.Map(new InvalidOperationException("odd"), "a", CancellationToken.None).Should().BeOfType<StorageIOException>();
    }

    [Fact]
    public void Existing_storage_exceptions_pass_through()
    {
        var ex = new StorageNotFoundException("a");
        S3Errors.Map(ex, "a", CancellationToken.None).Should().BeSameAs(ex);
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Mapped_messages_never_contain_the_sdk_message(string? code, HttpStatusCode status, Type _)
    {
        var mapped = S3Errors.Map(S3(code, status, "key SECRET123 leaked"), "a/b", CancellationToken.None);

        mapped.Message.Should().NotContain("SECRET123");
    }

    [Fact]
    public void Mapped_messages_of_network_errors_do_not_contain_the_sdk_message() =>
        S3Errors.Map(new HttpRequestException("SECRET123"), "a", CancellationToken.None).Message.Should().NotContain("SECRET123");
}
