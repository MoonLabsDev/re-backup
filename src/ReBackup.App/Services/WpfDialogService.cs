using System.IO;
using System.Windows;
using Microsoft.Win32;
using ReBackup.Core.Versions;

namespace ReBackup.App.Services;

public sealed class WpfDialogService : IDialogService
{
    /// <summary>
    /// The active window, else the main window while it is shown (e.g. a result after a long restore, when another
    /// program has the focus); null when ReBackup has no visible window.
    /// </summary>
    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? (Application.Current?.MainWindow is { IsVisible: true } main ? main : null);

    public bool Confirm(string title, string message) =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool ConfirmDefaultNo(string title, string message) =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool? AskYesNoCancel(string title, string message) =>
        Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
            dialog.InitialDirectory = initialFolder;

        var owner = Owner;
        var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return ok == true ? dialog.FolderName : null;
    }

    public void ShowError(string title, string message) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string title, string message) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public ConflictPolicy? AskConflictPolicy(string title, string message, IReadOnlyList<string>? details = null)
    {
        var dialog = new ConflictDialog(title, message, details ?? []);
        return ShowOwned(dialog) == true ? dialog.Choice : null;
    }

    public void ShowFailures(string title, string message, IReadOnlyList<string> lines)
    {
        ShowOwned(new FailuresDialog(title, message, lines, confirmText: null));
    }

    public bool ConfirmFailures(string title, string message, IReadOnlyList<string> lines, string confirmText)
    {
        return ShowOwned(new FailuresDialog(title, message, lines, confirmText)) == true;
    }

    /// <summary>Shows a dialog over <see cref="Owner"/>; without one, centered and in the taskbar so it cannot hide.</summary>
    private static bool? ShowOwned(Window dialog)
    {
        if (Owner is { } owner)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.ShowInTaskbar = true;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dialog.ShowDialog();
    }

    private static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var owner = Owner;
        return owner is null
            ? MessageBox.Show(text, caption, buttons, image, defaultResult)
            : MessageBox.Show(owner, text, caption, buttons, image, defaultResult);
    }
}
