using System.Windows.Controls;

namespace ReBackup.App.Views;

public partial class IgnorePreviewView : UserControl
{
    public IgnorePreviewView()
    {
        InitializeComponent();
    }

    private void OnRowRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Right-click does not select by itself; make the context menu act on the clicked row.
        ((ListViewItem)sender).IsSelected = true;
    }

    private void OnRowsListSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        // The Name column takes the width the fixed columns leave (room kept for the vertical scroll bar).
        if (!e.WidthChanged || RowsList.View is not GridView view)
            return;
        var others = view.Columns.Where(column => column != NameColumn).Sum(column => column.ActualWidth);
        var free = RowsList.ActualWidth - others - System.Windows.SystemParameters.VerticalScrollBarWidth - 8;
        NameColumn.Width = Math.Max(200, free);
    }

    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RowsList.SelectedItem is { } row)
            RowsList.ScrollIntoView(row);
    }
}
