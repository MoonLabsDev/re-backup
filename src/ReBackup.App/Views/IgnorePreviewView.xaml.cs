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

    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RowsList.SelectedItem is { } row)
            RowsList.ScrollIntoView(row);
    }
}
