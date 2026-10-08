namespace ReBackup.Storage.S3.Connections;

/// <summary>Combines a stored connection and its account into the <see cref="S3Connection"/> storages, the factory and the tester use.</summary>
public static class S3ConnectionResolver
{
    /// <summary>
    /// The resolved connection; <c>null</c> when <paramref name="account"/> is missing. Its secret is <c>null</c> when the
    /// account's secret is not decryptable.
    /// </summary>
    public static S3Connection? Resolve(S3ConnectionInfo connection, S3Account? account)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return account is null
            ? null
            : new S3Connection(connection.Id, connection.Name, connection.Region, connection.Bucket, account.AccessKeyId, account.Secret);
    }
}
