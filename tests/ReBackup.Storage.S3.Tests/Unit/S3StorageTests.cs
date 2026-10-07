using FluentAssertions;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Storage.S3.Tests.Unit;

public class S3StorageTests
{
    private static S3Connection Connection(string? secret) => new("id", "n", "eu-central-1", "bucket", "AKIA", secret);

    [Fact]
    public void Constructor_rejects_a_connection_without_secret()
    {
        var act = () => new S3Storage(Connection(null), "");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rejects_an_invalid_prefix()
    {
        var act = () => new S3Storage(Connection("s"), "/bad");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Offline_members_and_capabilities()
    {
        var storage = new S3Storage(Connection("s"), "p");

        storage.Capabilities.Should().Be(StorageCapabilities.CaseSensitive);
        (await storage.GetFreeSpaceAsync(CancellationToken.None)).Should().BeNull();
        await storage.EnsureDirectoryAsync("a/b", CancellationToken.None);
    }
}
