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
        Closed += (_, _) => viewModel.OnClosed();
    }

    /// <summary>Opens the company's site in the default browser.</summary>
    private void OnCompanyLinkClicked(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser is registered; the address is in the tool tip.
        }
        e.Handled = true;
    }
}
