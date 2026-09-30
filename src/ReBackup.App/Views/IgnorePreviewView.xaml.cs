using System.Windows.Controls;

namespace ReBackup.App.Views;

public partial class IgnorePreviewView : UserControl
{
    public IgnorePreviewView()
    {
        InitializeComponent();
    }

    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RowsList.SelectedItem is { } row)
            RowsList.ScrollIntoView(row);
    }
}
