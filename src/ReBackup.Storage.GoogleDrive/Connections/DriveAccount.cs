namespace ReBackup.Storage.GoogleDrive.Connections;

/// <summary>
/// A stored Google service account: the e-mail of the account (read from its key) and the key JSON, DPAPI-protected. Drive
/// connections refer to it by <see cref="Id"/>. The plain key is never part of this record.
/// </summary>
/// <param name="Id">Stable identifier connections refer to.</param>
/// <param name="Name">Display name chosen by the user.</param>
/// <param name="ClientEmail">The service account's e-mail (<c>client_email</c> of the key); the address folders and shared drives are shared with.</param>
/// <param name="ProtectedKey">The key JSON as DPAPI blob (Base64), or <c>null</c> when none is stored.</param>
public sealed record DriveAccount(string Id, string Name, string ClientEmail, string? ProtectedKey)
{
    /// <summary>True when the stored key cannot be decrypted here (another Windows account or machine) and has to be entered again.</summary>
    public bool NeedsKey => SecretProtector.TryUnprotect(ProtectedKey) is null;

    /// <summary>Never includes <see cref="ProtectedKey"/>.</summary>
    public override string ToString() => $"DriveAccount {{ Id = {Id}, Name = {Name}, ClientEmail = {ClientEmail} }}";
}
