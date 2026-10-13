namespace ReBackup.Storage.GoogleDrive.Connections;

/// <summary>A stored Drive connection: the root (a shared drive or a folder) plus the <see cref="DriveAccount"/> that opens it.</summary>
/// <param name="Id">Stable identifier a storage location refers to.</param>
/// <param name="Name">Display name chosen by the user.</param>
/// <param name="AccountId">Id of the <see cref="DriveAccount"/>.</param>
/// <param name="RootId">Drive id of the root: the shared drive itself or the folder.</param>
/// <param name="RootName">Name of the root, shown in the UI.</param>
/// <param name="SharedDriveId">The shared drive the root lives in (equal to <paramref name="RootId"/> for a shared drive); <c>null</c> for My Drive.</param>
public sealed record DriveConnectionInfo(string Id, string Name, string AccountId, string RootId, string RootName, string? SharedDriveId);
