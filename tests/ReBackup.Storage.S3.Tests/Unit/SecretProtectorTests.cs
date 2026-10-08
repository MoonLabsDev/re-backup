using FluentAssertions;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Storage.S3.Tests.Unit;

public class SecretProtectorTests
{
    [Fact]
    public void Protect_then_unprotect_round_trips()
    {
        const string secret = "s3cr3t/+=äö";

        var blob = SecretProtector.Protect(secret);

        blob.Should().NotContain(secret);
        SecretProtector.TryUnprotect(blob).Should().Be(secret);
    }

    [Fact]
    public void Unprotect_returns_null_for_garbage()
    {
        var randomBytes = new byte[64];
        new Random(42).NextBytes(randomBytes);

        SecretProtector.TryUnprotect("not base64").Should().BeNull();
        SecretProtector.TryUnprotect(Convert.ToBase64String(randomBytes)).Should().BeNull();
        SecretProtector.TryUnprotect(null).Should().BeNull();
    }
}
