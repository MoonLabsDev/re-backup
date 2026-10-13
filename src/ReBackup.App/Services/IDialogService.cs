using ReBackup.App.ViewModels;
using ReBackup.Core.Versions;
using ReBackup.Storage.GoogleDrive.Connections;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.App.Services;

public interface IDialogService
{
    bool Confirm(string title, string message);

    /// <summary>Like <see cref="Confirm"/>, with No as the default answer (Enter): for actions that are hard to undo.</summary>
    bool ConfirmDefaultNo(string title, string message);

    /// <summary>Yes → true, No → false, Cancel → null.</summary>
    bool? AskYesNoCancel(string title, string message);

    string? PickFolder(string title, string? initialFolder);

    void ShowError(string title, string message);

    void ShowInfo(string title, string message);

    /// <summary>
    /// Overwrite / Skip / Keep both; null when the user cancels. <paramref name="details"/> (e.g. parts of the version
    /// that cannot be read) are listed below the question when there are any.
    /// </summary>
    ConflictPolicy? AskConflictPolicy(string title, string message, IReadOnlyList<string>? details = null);

    /// <summary>A message with a scrollable, copyable list (e.g. the files a restore could not write).</summary>
    void ShowFailures(string title, string message, IReadOnlyList<string> lines);

    /// <summary>Like <see cref="ShowFailures"/>, with <paramref name="confirmText"/> / Cancel; true to go on.</summary>
    bool ConfirmFailures(string title, string message, IReadOnlyList<string> lines, string confirmText);

    /// <summary>
    /// The shared S3 connection dialog on <paramref name="existing"/> (null = a new connection; its account is picked or
    /// created there). The connection as saved in the dialog, not yet stored; null when cancelled.
    /// </summary>
    S3ConnectionInfo? EditS3Connection(S3ConnectionInfo? existing);

    /// <summary>The shared S3 accounts dialog; its changes are stored at once.</summary>
    void ManageS3Accounts();

    /// <summary>
    /// The Drive connection dialog on <paramref name="existing"/> (null = a new connection; its account is picked or created
    /// there). The connection as saved in the dialog, not yet stored; null when cancelled.
    /// </summary>
    DriveConnectionInfo? EditDriveConnection(DriveConnectionInfo? existing);

    /// <summary>The Google account dialog on <paramref name="existing"/> (null = a new account): name and key, not yet stored; null when cancelled.</summary>
    DriveAccountInput? EditDriveAccount(DriveAccount? existing);
}
