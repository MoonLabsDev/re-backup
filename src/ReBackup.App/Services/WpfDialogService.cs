using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Text.Json;
using ReBackup.App.ViewModels;
using ReBackup.App.Views;
using ReBackup.Core.Versions;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.S3;
using ReBackup.Storage.GoogleDrive.Connections;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.App.Services;

/// <param name="connections">
/// The S3 connections of the configuration folder (null before it is loaded): the "name taken" check and the account
/// picker of the connection dialog, and the accounts dialog.
/// </param>
/// <param name="drive">The Google accounts and Drive connections of the configuration folder (null before it is loaded).</param>
public sealed class WpfDialogService(Func<S3ConnectionStore?> connections, Func<DriveConnections?>? drive = null) : IDialogService
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

    public S3ConnectionInfo? EditS3Connection(S3ConnectionInfo? existing)
    {
        if (connections() is not { } store)
            return null;
        var tester = new S3ConnectionTester();
        var accountDialogs = new S3AccountDialogs(tester, () => Owner);
        var viewModel = new S3ConnectionDialogViewModel(existing, name => IsNameTaken(store, existing, name), tester,
            () => LoadAccounts(store), () => accountDialogs.CreateIn(store));
        return ShowOwned(new S3ConnectionDialog(viewModel)) == true ? viewModel.Result : null;
    }

    public void ManageS3Accounts()
    {
        if (connections() is { } store)
            ShowOwned(new S3AccountsDialog(store, new S3ConnectionTester()));
    }

    public DriveConnectionInfo? EditDriveConnection(DriveConnectionInfo? existing)
    {
        if (drive?.Invoke() is not { } store)
            return null;
        var viewModel = new DriveConnectionDialogViewModel(existing,
            () => Safe(() => store.Store.LoadAccounts(), []),
            name => Safe(() => store.Store.LoadAll().Any(c => !string.Equals(c.Id, existing?.Id, StringComparison.OrdinalIgnoreCase)
                                                              && string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)), false),
            store.ApiForAccount,
            () => CreateDriveAccount(store));
        return ShowOwned(new DriveConnectionDialog(viewModel)) == true ? viewModel.Result : null;
    }

    public DriveAccountInput? EditDriveAccount(DriveAccount? existing)
    {
        if (drive?.Invoke() is not { } store)
            return null;
        var viewModel = new DriveAccountDialogViewModel(existing,
            name => Safe(() => store.Store.LoadAccounts().Any(a => !string.Equals(a.Id, existing?.Id, StringComparison.OrdinalIgnoreCase)
                                                                  && string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)), false),
            PickKeyFile);
        return ShowOwned(new DriveAccountDialog(viewModel)) == true ? viewModel.Result : null;
    }

    public void ShowCopyPlan(CopyPlanViewModel viewModel) => ShowOwned(new CopyPlanDialog(viewModel));

    public void ShowVerify(VerifyViewModel viewModel) => ShowOwned(new VerifyDialog(viewModel));

    /// <summary>"New account…" in the connection dialog: the account dialog, then the account is stored.</summary>
    private DriveAccount? CreateDriveAccount(DriveConnections store)
    {
        if (EditDriveAccount(null) is not { } input)
            return null;
        var id = Guid.NewGuid().ToString("N");
        try
        {
            store.SaveAccount(id, input.Name, input.KeyJson);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError(Loc.T("driveaccounts.title"), Loc.F("driveconnections.saveFailed", ("error", ex.Message)));
            return null;
        }
        return store.Accounts.FirstOrDefault(a => a.Id == id);
    }

    /// <summary>The service account's key file (JSON).</summary>
    private static string? PickKeyFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("driveaccounts.chooseKeyTitle"),
            Filter = Loc.T("driveaccounts.keyFilter") + "|*.json",
            CheckFileExists = true,
        };
        var owner = Owner;
        var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return ok == true ? dialog.FileName : null;
    }

    /// <summary>Reads for a picker or a name check; an unreadable file blocks nothing here, saving reports it.</summary>
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Another saved connection than <paramref name="existing"/> has <paramref name="name"/> (case-insensitive); an
    /// unreadable file blocks nothing here, saving reports it.
    /// </summary>
    private static bool IsNameTaken(S3ConnectionStore store, S3ConnectionInfo? existing, string name)
    {
        try
        {
            return store.LoadAll().Any(c => !string.Equals(c.Id, existing?.Id, StringComparison.OrdinalIgnoreCase)
                                            && string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The saved S3 accounts for the picker; none when the file cannot be read (saving reports it).</summary>
    private static IReadOnlyList<S3Account> LoadAccounts(S3ConnectionStore store)
    {
        try
        {
            return store.LoadAccounts();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Shows a dialog over <see cref="Owner"/>; without one, centered and in the taskbar so it cannot hide.</summary>
    internal static bool? ShowOwned(Window dialog)
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
