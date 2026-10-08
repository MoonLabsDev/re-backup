using System.ComponentModel;
using System.Windows;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.Services;

namespace ReBackup.Shared.Wpf.S3;

/// <summary>
/// Add or edit an S3 account (an access key). <see cref="Window.ShowDialog"/> returns true after Save; the account is then in
/// <see cref="S3AccountDialogViewModel.Result"/> (<c>Secret</c> null = keep the stored one). The caller stores it.
/// </summary>
public partial class S3AccountDialog : Window
{
    private readonly S3AccountDialogViewModel _viewModel;

    public S3AccountDialog(S3AccountDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Loc.Bind(this, TitleProperty, viewModel.IsNew ? "s3.accountDialog.titleNew" : "s3.accountDialog.titleEdit");
        DataContext = viewModel;
        viewModel.Saved += OnSaved;
    }

    /// <summary>The PasswordBox has no bindable password: its text goes to the view model here and nowhere else.</summary>
    private void OnSecretChanged(object sender, RoutedEventArgs e) => _viewModel.Secret = SecretBox.Password;

    private void OnSaved(object? sender, EventArgs e) => DialogResult = true;

    /// <summary>A running test is cancelled with the dialog.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && _viewModel.CancelTestCommand.CanExecute(null))
            _viewModel.CancelTestCommand.Execute(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Saved -= OnSaved;
        base.OnClosed(e);
    }
}
