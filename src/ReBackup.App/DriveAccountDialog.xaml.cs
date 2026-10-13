using System.Windows;
using ReBackup.App.ViewModels;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.Services;

namespace ReBackup.App;

/// <summary>
/// Add or edit a Google service account. <see cref="Window.ShowDialog"/> returns true after Save; the name and the chosen key
/// are then in <see cref="DriveAccountDialogViewModel.Result"/>, for the caller to store.
/// </summary>
public partial class DriveAccountDialog : Window
{
    private readonly DriveAccountDialogViewModel _viewModel;

    public DriveAccountDialog(DriveAccountDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Loc.Bind(this, TitleProperty, viewModel.IsNew ? "driveaccounts.titleNew" : "driveaccounts.titleEdit");
        DataContext = viewModel;
        viewModel.Saved += OnSaved;
    }

    private void OnSaved(object? sender, EventArgs e) => DialogResult = true;

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Saved -= OnSaved;
        base.OnClosed(e);
    }
}
