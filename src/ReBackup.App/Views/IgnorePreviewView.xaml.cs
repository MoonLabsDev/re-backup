using System.ComponentModel;
using System.Windows.Controls;
using ReBackup.App.ViewModels;

namespace ReBackup.App.Views;

public partial class IgnorePreviewView : UserControl
{
    /// <summary>Width of the theme's thin scroll bar (Theme/Controls.xaml, ScrollBar style), not the system one.</summary>
    private const double ThemeScrollBarWidth = 10;

    /// <summary>The Name column never gets narrower; below that the table scrolls sideways.</summary>
    private const double NameMinWidth = 140;

    private PreviewTreeViewModel? _tree;

    public IgnorePreviewView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => EnsureCompareChoices();
        DataContextChanged += (_, _) =>
        {
            WatchTree((DataContext as PlanEditorViewModel)?.Preview.Tree);
            EnsureCompareChoices();
        };
        WatchTree(null);
    }

    private void EnsureCompareChoices()
    {
        if (IsVisible && DataContext is PlanEditorViewModel editor)
            editor.Preview.EnsureCompareChoices();
    }

    /// <summary>Follows <see cref="PreviewTreeViewModel.IsComparing"/>: the compare columns are shown only while comparing.</summary>
    private void WatchTree(PreviewTreeViewModel? tree)
    {
        if (_tree is not null)
            _tree.PropertyChanged -= OnTreePropertyChanged;
        _tree = tree;
        if (tree is not null)
            tree.PropertyChanged += OnTreePropertyChanged;
        ShowCompareColumns(tree?.IsComparing ?? false);
    }

    private void OnTreePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PreviewTreeViewModel.IsComparing) && sender is PreviewTreeViewModel tree)
            ShowCompareColumns(tree.IsComparing);
    }

    private void ShowCompareColumns(bool show)
    {
        if (RowsList.View is not GridView view || view.Columns.Contains(CompareSizeColumn) == show)
            return;
        if (show)
        {
            var after = view.Columns.IndexOf(SizeColumn) + 1;
            view.Columns.Insert(after, CompareSizeColumn);
            view.Columns.Insert(after + 1, SizeChangeColumn);
        }
        else
        {
            view.Columns.Remove(CompareSizeColumn);
            view.Columns.Remove(SizeChangeColumn);
        }
        FitNameColumn();
    }

    private void OnRowRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Right-click does not select by itself; make the context menu act on the clicked row.
        ((ListViewItem)sender).IsSelected = true;
    }

    private void OnRowsListSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            FitNameColumn();
    }

    /// <summary>The Name column takes the width the fixed columns leave (room kept for the vertical scroll bar).</summary>
    private void FitNameColumn()
    {
        if (RowsList.View is not GridView view || RowsList.ActualWidth <= 0)
            return;
        // A column just inserted has no ActualWidth yet.
        var others = view.Columns.Where(column => column != NameColumn)
            .Sum(column => column.ActualWidth > 0 ? column.ActualWidth : column.Width);
        var free = RowsList.ActualWidth - others - ThemeScrollBarWidth - 8;
        NameColumn.Width = Math.Max(NameMinWidth, free);
    }

    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RowsList.SelectedItem is { } row)
            RowsList.ScrollIntoView(row);
    }
}
