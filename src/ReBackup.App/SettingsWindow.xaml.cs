using System.Windows;
using ReBackup.App.Services;
using ReBackup.App.ViewModels;

namespace ReBackup.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, result) => DialogResult = result;
    }
}
