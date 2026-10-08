namespace ReBackup.Storage.S3.Connections;

/// <summary>A stored S3 connection: bucket and region plus the <see cref="S3Account"/> whose key opens it.</summary>
/// <param name="Id">Stable identifier a storage location refers to.</param>
/// <param name="Name">Display name chosen by the user.</param>
/// <param name="Region">AWS region system name, e.g. <c>eu-central-1</c>.</param>
/// <param name="Bucket">Bucket name.</param>
/// <param name="AccountId">Id of the <see cref="S3Account"/>.</param>
public sealed record S3ConnectionInfo(string Id, string Name, string Region, string Bucket, string AccountId);
