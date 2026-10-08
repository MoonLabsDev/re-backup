namespace ReBackup.Storage.S3.Connections;

/// <summary>
/// An Amazon S3 connection: region, bucket and the access key. <see cref="Secret"/> is the plain secret access key,
/// held in memory only; it is <c>null</c> when the stored secret cannot be decrypted on this machine or account.
/// </summary>
/// <param name="Id">Stable identifier a storage location refers to.</param>
/// <param name="Name">Display name chosen by the user.</param>
/// <param name="Region">AWS region system name, e.g. <c>eu-central-1</c>.</param>
/// <param name="Bucket">Bucket name.</param>
/// <param name="AccessKeyId">Access key ID.</param>
/// <param name="Secret">Secret access key, or <c>null</c> when it must be entered again.</param>
public sealed record S3Connection(string Id, string Name, string Region, string Bucket, string AccessKeyId, string? Secret)
{
    /// <summary>True when there is no usable secret and the user has to re-enter it.</summary>
    public bool NeedsSecret => Secret is null;

    /// <summary>Never includes <see cref="Secret"/>.</summary>
    public override string ToString() =>
        $"S3Connection {{ Id = {Id}, Name = {Name}, Region = {Region}, Bucket = {Bucket}, AccessKeyId = {AccessKeyId} }}";
}
