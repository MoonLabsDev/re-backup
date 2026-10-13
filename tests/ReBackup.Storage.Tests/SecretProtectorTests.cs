using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using ReBackup.Storage;

namespace ReBackup.Storage.Tests;

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

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Protect_keeps_the_fixed_entropy_so_secrets_stored_by_earlier_versions_stay_readable()
    {
        const string secret = "s3cr3t/+=äö";

        var blob = SecretProtector.Protect(secret);
        var plain = ProtectedData.Unprotect(Convert.FromBase64String(blob), "ReBackup.S3.v1"u8.ToArray(), DataProtectionScope.CurrentUser);

        Encoding.UTF8.GetString(plain).Should().Be(secret);
    }
}
