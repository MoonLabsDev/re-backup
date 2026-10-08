namespace ReBackup.Storage.S3.Connections;

/// <summary>
/// Stored S3 credentials: an access key that one or more <see cref="S3ConnectionInfo"/>s refer to. <see cref="Secret"/> is the
/// plain secret access key, held in memory only; it is <c>null</c> when the stored secret cannot be decrypted on this machine
/// or account (and, when saving, when the stored secret is to be kept).
/// </summary>
/// <param name="Id">Stable identifier connections refer to.</param>
/// <param name="Name">Display name chosen by the user.</param>
/// <param name="AccessKeyId">Access key ID.</param>
/// <param name="Secret">Secret access key, or <c>null</c> when it must be entered again.</param>
public sealed record S3Account(string Id, string Name, string AccessKeyId, string? Secret)
{
    /// <summary>True when there is no usable secret and the user has to re-enter it.</summary>
    public bool NeedsSecret => Secret is null;

    /// <summary>Never includes <see cref="Secret"/>.</summary>
    public override string ToString() => $"S3Account {{ Id = {Id}, Name = {Name}, AccessKeyId = {AccessKeyId} }}";
}
