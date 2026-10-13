using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;

namespace ReBackup.Storage.GoogleDrive.Tests.Unit;

/// <summary>What can be checked of the real client without Google: the key and the query text.</summary>
public sealed class GoogleDriveApiTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"authorized_user\",\"client_id\":\"x\",\"client_secret\":\"y\",\"refresh_token\":\"z\"}")]
    public void A_key_that_is_no_service_account_key_is_access_denied_without_quoting_it(string key)
    {
        var act = () => new GoogleDriveApi(key);

        act.Should().Throw<StorageAccessDeniedException>().Which.ToString().Should().NotContain("client_secret").And.NotContain("not json");
    }

    [Fact]
    public void A_service_account_key_builds_a_client_without_calling_google()
    {
        using var rsa = RSA.Create(2048);
        var key = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account",
            ["project_id"] = "test",
            ["private_key_id"] = "k1",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["client_email"] = "backup@test.iam.gserviceaccount.com",
            ["client_id"] = "1",
            ["token_uri"] = "https://oauth2.googleapis.com/token",
        });

        using var api = new GoogleDriveApi(key, "sd1");

        api.Should().NotBeNull();
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("it's", "it\\'s")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("\\'", "\\\\\\'")]
    public void Query_literals_escape_backslashes_and_quotes(string value, string expected)
    {
        GoogleDriveApi.Escape(value).Should().Be(expected);
    }
}
