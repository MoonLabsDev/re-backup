using System.Windows.Controls;
using ReBackup.App.ViewModels;

namespace ReBackup.App.Views;

public partial class ScheduleView : UserControl
{
    public ScheduleView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => RefreshNextRuns();
        DataContextChanged += (_, _) => RefreshNextRuns();
    }

    /// <summary>The next runs are relative to now: recompute them whenever the tab is shown.</summary>
    private void RefreshNextRuns()
    {
        if (IsVisible && DataContext is PlanEditorViewModel editor)
            editor.RefreshNextRuns();
    }
}
