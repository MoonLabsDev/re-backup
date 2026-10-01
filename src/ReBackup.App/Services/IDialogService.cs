using ReBackup.Core.Versions;

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
}
