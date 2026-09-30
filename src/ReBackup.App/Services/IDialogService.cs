namespace ReBackup.App.Services;

public interface IDialogService
{
    bool Confirm(string title, string message);

    /// <summary>Yes → true, No → false, Cancel → null.</summary>
    bool? AskYesNoCancel(string title, string message);

    string? PickFolder(string title, string? initialFolder);

    void ShowError(string title, string message);

    void ShowInfo(string title, string message);
}
