using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;

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

    [Fact]
    public void A_listing_on_a_shared_drive_searches_that_drive_and_asks_whether_the_search_was_complete()
    {
        using var service = new DriveService();
        var request = service.Files.List();

        GoogleDriveApi.ConfigureList(request, "'p' in parents", "tok", "sd1");

        request.Corpora.Should().Be("drive");
        request.DriveId.Should().Be("sd1");
        request.SupportsAllDrives.Should().BeTrue();
        request.IncludeItemsFromAllDrives.Should().BeTrue();
        request.Q.Should().Be("'p' in parents");
        request.PageToken.Should().Be("tok");
        request.Fields.Should().StartWith("nextPageToken,incompleteSearch,files(");
    }

    [Fact]
    public void A_listing_in_my_drive_searches_the_accounts_own_view_not_all_drives()
    {
        using var service = new DriveService();
        var request = service.Files.List();

        GoogleDriveApi.ConfigureList(request, "'p' in parents", null, null);

        request.Corpora.Should().Be("user");
        request.DriveId.Should().BeNull();
        request.SupportsAllDrives.Should().BeTrue();
        request.IncludeItemsFromAllDrives.Should().BeTrue();
        request.Fields.Should().Contain("incompleteSearch");
    }

    [Fact]
    public void A_page_drive_reports_as_an_incomplete_search_fails_the_listing()
    {
        var act = () => GoogleDriveApi.EnsureComplete(new FileList { IncompleteSearch = true, Files = [] });

        var ex = act.Should().Throw<DriveApiException>().Which;
        ex.Status.Should().Be(503);
        ex.Reason.Should().Be("incompleteSearch");
        ex.Message.Should().Be("Drive could not search every drive; the listing may be incomplete.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void A_complete_page_passes(bool? incomplete)
    {
        var act = () => GoogleDriveApi.EnsureComplete(new FileList { IncompleteSearch = incomplete, Files = [] });

        act.Should().NotThrow();
    }
}
