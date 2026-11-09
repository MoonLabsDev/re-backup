using System.Windows.Controls;
using ReBackup.App.ViewModels;

namespace ReBackup.App.Views;

/// <summary>
/// The Cleanup tab: the plan's versions to check, Scan / Clean up… / Cancel, the totals and the scan by rule. Reads the
/// target (through the Versions tab) whenever it becomes visible, so the grid is filled even before the Versions tab
/// was opened.
/// </summary>
public partial class CleanupView : UserControl
{
    public CleanupView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => EnsureLoaded();
        DataContextChanged += (_, _) => EnsureLoaded();
    }

    private void EnsureLoaded()
    {
        if (IsVisible && DataContext is CleanupViewModel cleanup)
            cleanup.Versions.EnsureLoaded();
    }
}
