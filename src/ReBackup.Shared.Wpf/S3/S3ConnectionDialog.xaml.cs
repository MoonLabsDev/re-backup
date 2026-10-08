using System.ComponentModel;
using System.Windows;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.Services;

namespace ReBackup.Shared.Wpf.S3;

/// <summary>
/// Add or edit an S3 connection. <see cref="Window.ShowDialog"/> returns true after Save; the connection is then in
/// <see cref="S3ConnectionDialogViewModel.Result"/>. The app stores it and keeps its own connection list.
/// </summary>
public partial class S3ConnectionDialog : Window
{
    private readonly S3ConnectionDialogViewModel _viewModel;

    public S3ConnectionDialog(S3ConnectionDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Loc.Bind(this, TitleProperty, viewModel.IsNew ? "s3.dialog.titleNew" : "s3.dialog.titleEdit");
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
