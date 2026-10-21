using System.ComponentModel;
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

    private VersionsViewModel? _versions;

    public VersionsView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => EnsureLoaded();
        DataContextChanged += (_, _) =>
        {
            WatchVersions((DataContext as PlanEditorViewModel)?.Versions);
            EnsureLoaded();
        };
        WatchVersions(null);
    }

    /// <summary>Follows <see cref="VersionsViewModel.IsComparing"/>: the compare columns are shown only while comparing.</summary>
    private void WatchVersions(VersionsViewModel? versions)
    {
        if (_versions is not null)
            _versions.PropertyChanged -= OnVersionsPropertyChanged;
        _versions = versions;
        if (versions is not null)
            versions.PropertyChanged += OnVersionsPropertyChanged;
        ShowCompareColumns(versions?.IsComparing ?? false);
    }

    private void OnVersionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VersionsViewModel.IsComparing) && sender is VersionsViewModel versions)
            ShowCompareColumns(versions.IsComparing);
    }

    private void ShowCompareColumns(bool show)
    {
        if (TreeList.View is not GridView view || view.Columns.Contains(CompareSizeColumn) == show)
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

    private void OnVersionRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click on a version outside the marked ones marks only that one, as Explorer does.
        var item = (ListBoxItem)sender;
        if (!item.IsSelected && ItemsControl.ItemsControlFromItemContainer(item) is ListBox list)
        {
            list.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void OnForeignRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click does not select by itself; make the context menu act on the clicked folder.
        ((ListBoxItem)sender).IsSelected = true;
    }

    private void OnVersionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var list = (ListBox)sender;
        if (DataContext is PlanEditorViewModel editor)
            editor.Versions.SetMarkedVersions(list.SelectedItems.OfType<VersionRowViewModel>());
        // A version picked in the coverage timeline may be far down the list.
        if (list.SelectedItems.Count == 1)
            list.ScrollIntoView(list.SelectedItem);
    }

    private void OnTreeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            FitNameColumn();
    }

    /// <summary>The Name column takes the width the fixed columns leave (room kept for the vertical scroll bar).</summary>
    private void FitNameColumn()
    {
        if (TreeList.View is not GridView view || TreeList.ActualWidth <= 0)
            return;
        // A column just inserted has no ActualWidth yet.
        var others = view.Columns.Where(column => column != NameColumn)
            .Sum(column => column.ActualWidth > 0 ? column.ActualWidth : column.Width);
        NameColumn.Width = Math.Max(NameMinWidth, TreeList.ActualWidth - others - ThemeScrollBarWidth - 8);
    }

    private void OnTreeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A revealed search hit may be far down the list.
        if (TreeList.SelectedItem is { } row)
            TreeList.ScrollIntoView(row);
    }
}
