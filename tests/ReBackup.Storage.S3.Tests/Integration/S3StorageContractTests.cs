namespace ReBackup.Storage.S3.Tests.Integration;

/// <summary>The storage contract against a real S3-compatible server; every test gets its own key prefix.</summary>
[Collection(S3ServerCollection.Name)]
public sealed class S3StorageContractTests(S3ServerFixture server) : ReBackup.Storage.Tests.StorageContractTests
{
    protected override IStorage CreateEmpty()
    {
        Skip.IfNot(server.Available, server.SkipReason);
        return server.CreateStorage("t-" + Guid.NewGuid().ToString("N"));
    }
}
