using System.Windows.Controls;
using ReBackup.App.ViewModels;

namespace ReBackup.App.Views;

public partial class RetentionView : UserControl
{
    public RetentionView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => EnsureLoaded();
        DataContextChanged += (_, _) => EnsureLoaded();
    }

    /// <summary>Reads the target of the shown plan the first time its Retention tab is visible.</summary>
    private void EnsureLoaded()
    {
        if (IsVisible && DataContext is PlanEditorViewModel editor)
            editor.RetentionPreview.EnsureLoaded();
    }
}
