using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReBackup.App.ViewModels;

namespace ReBackup.App.Views;

/// <summary>The Versions tab. Reads the target and syncs the index whenever it becomes visible.</summary>
public partial class VersionsView : UserControl
{
    /// <summary>Width of the theme's thin scroll bar (Theme/Controls.xaml, ScrollBar style), not the system one.</summary>
    private const double ThemeScrollBarWidth = 10;

    /// <summary>The Name column never gets narrower; below that the table scrolls sideways.</summary>
    private const double NameMinWidth = 160;

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

    private void OnRowRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click does not select by itself; make the context menu act on the clicked row.
        ((ListViewItem)sender).IsSelected = true;
    }

    private void OnTreeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The Name column takes the width the fixed columns leave (room kept for the vertical scroll bar).
        if (!e.WidthChanged || TreeList.View is not GridView view)
            return;
        var others = view.Columns.Where(column => column != NameColumn).Sum(column => column.ActualWidth);
        NameColumn.Width = Math.Max(NameMinWidth, TreeList.ActualWidth - others - ThemeScrollBarWidth - 8);
    }

    private void OnTreeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A revealed search hit may be far down the list.
        if (TreeList.SelectedItem is { } row)
            TreeList.ScrollIntoView(row);
    }
}
