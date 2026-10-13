using System.Windows.Controls;
using ReBackup.App.ViewModels;

namespace ReBackup.App.Views;

/// <summary>The History tab. Lists the saved target for the coverage timeline whenever it becomes visible.</summary>
public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => EnsureLoaded();
        DataContextChanged += (_, _) => EnsureLoaded();
    }

    private void EnsureLoaded()
    {
        if (IsVisible && DataContext is PlanEditorViewModel editor)
            editor.HistoryCoverage.EnsureLoaded();
    }

    private void OnRunSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A run picked in the coverage timeline may be far down the table.
        if (RunsGrid.SelectedItem is { } row)
            RunsGrid.ScrollIntoView(row);
    }
}
