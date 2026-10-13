namespace ReBackup.Storage.GoogleDrive.Connections;

/// <summary>A Drive connection resolved with its account: what a storage needs to open the root. Held in memory only.</summary>
/// <param name="Info">The stored connection.</param>
/// <param name="ClientEmail">The service account's e-mail.</param>
/// <param name="KeyJson">The plain service-account key JSON.</param>
public sealed record DriveConnection(DriveConnectionInfo Info, string ClientEmail, string KeyJson)
{
    /// <summary>Never includes <see cref="KeyJson"/>.</summary>
    public override string ToString() => $"DriveConnection {{ Info = {Info}, ClientEmail = {ClientEmail} }}";
}
