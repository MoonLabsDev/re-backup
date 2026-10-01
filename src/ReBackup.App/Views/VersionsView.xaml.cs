using System.Windows.Controls;
using ReBackup.App.ViewModels;

namespace ReBackup.App.Views;

/// <summary>The Versions tab. Reads the target and syncs the index whenever it becomes visible.</summary>
public partial class VersionsView : UserControl
{
    public VersionsView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => EnsureLoaded();
        DataContextChanged += (_, _) => EnsureLoaded();
    }

    private void EnsureLoaded()
    {
        if (IsVisible && DataContext is PlanEditorViewModel editor)
            editor.Versions.EnsureLoaded();
    }
}
