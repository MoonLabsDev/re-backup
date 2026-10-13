using System.ComponentModel;
using System.Windows;
using ReBackup.App.ViewModels;
using ReBackup.Shared.Wpf.Localization;
using ReBackup.Shared.Wpf.Services;

namespace ReBackup.App;

/// <summary>
/// Add or edit a Drive connection. <see cref="Window.ShowDialog"/> returns true after Save; the connection is then in
/// <see cref="DriveConnectionDialogViewModel.Result"/>, for the caller to store. The shared drives are listed when the dialog
/// opens and whenever another account is chosen.
/// </summary>
public partial class DriveConnectionDialog : Window
{
    private readonly DriveConnectionDialogViewModel _viewModel;

    public DriveConnectionDialog(DriveConnectionDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Loc.Bind(this, TitleProperty, viewModel.IsNew ? "driveconnections.titleNew" : "driveconnections.titleEdit");
        DataContext = viewModel;
        viewModel.Saved += OnSaved;
        viewModel.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) => LoadSharedDrives();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DriveConnectionDialogViewModel.SelectedAccount) && IsLoaded)
            LoadSharedDrives();
    }

    private void LoadSharedDrives()
    {
        if (_viewModel.LoadSharedDrivesCommand.CanExecute(null))
            _viewModel.LoadSharedDrivesCommand.Execute(null);
    }

    private void OnSaved(object? sender, EventArgs e) => DialogResult = true;

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Saved -= OnSaved;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.Cancel();   // a listing or folder lookup still running ends with the dialog
        base.OnClosed(e);
    }
}
