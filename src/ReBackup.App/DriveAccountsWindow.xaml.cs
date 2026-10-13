using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReBackup.App.ViewModels;
using ReBackup.Shared.Wpf.Services;

namespace ReBackup.App;

/// <summary>Settings → "Google accounts…". Closed with Close or Esc; <see cref="Window.ShowDialog"/>'s result carries no meaning.</summary>
public partial class DriveAccountsWindow : Window
{
    private readonly DriveAccountsViewModel _viewModel;

    public DriveAccountsWindow(DriveAccountsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Detach();
    }

    /// <summary>A double-click on a row edits it (not on the header or the empty area).</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(AccountList, (DependencyObject)e.OriginalSource) is ListViewItem
            && _viewModel.EditCommand.CanExecute(null))
            _viewModel.EditCommand.Execute(null);
    }
}
