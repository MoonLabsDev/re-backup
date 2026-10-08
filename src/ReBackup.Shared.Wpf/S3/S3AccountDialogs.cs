using System.IO;
using System.Text.Json;
using System.Windows;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Storage.S3;
using ReBackup.Storage.S3.Connections;

namespace ReBackup.Shared.Wpf.S3;

/// <summary>
/// The windows behind <see cref="IS3AccountDialogs"/>: <see cref="S3AccountDialog"/> and message boxes, owned by the window
/// <paramref name="owner"/> returns (none: centred on the screen).
/// </summary>
public sealed class S3AccountDialogs(S3ConnectionTester tester, Func<Window?> owner) : IS3AccountDialogs
{
    private const string CaptionKey = "s3.accounts.title";

    public S3Account? EditAccount(S3Account? existing, Func<string, bool> isNameTaken)
    {
        var viewModel = new S3AccountDialogViewModel(existing, isNameTaken, tester);
        var dialog = new S3AccountDialog(viewModel);
        if (owner() is { } window) dialog.Owner = window;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? viewModel.Result : null;
    }

    public bool Confirm(string titleKey, string message) =>
        Show(message, Loc.T(titleKey), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowError(string message) => Show(message, Loc.T(CaptionKey), MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK);

    /// <summary>
    /// "New account…" of <see cref="S3ConnectionDialogViewModel"/>: opens the account dialog, stores the account in
    /// <paramref name="store"/> and returns it as stored (with its secret), or <c>null</c> when cancelled or not saved.
    /// </summary>
    public S3Account? CreateIn(S3ConnectionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            var names = store.LoadAccounts().Select(a => a.Name).ToList();
            var created = EditAccount(null, name => names.Exists(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (created is null) return null;
            store.SaveAccount(created);
            return store.TryGetAccount(created.Id);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError(Loc.T(S3AccountsViewModel.SaveFailedKey));
            return null;
        }
    }

    private MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult) =>
        owner() is { } window
            ? MessageBox.Show(window, text, caption, buttons, image, defaultResult)
            : MessageBox.Show(text, caption, buttons, image, defaultResult);
}
