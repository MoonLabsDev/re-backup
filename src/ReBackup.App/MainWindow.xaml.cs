using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ReBackup.Shared.Wpf.Services;

namespace ReBackup.App;

public partial class MainWindow : Window
{
    /// <summary>Below this much room for the plan name the tabs move to a second header row.</summary>
    private const double MinNameRoom = 120;

    /// <summary>Horizontal margins around the tab bar in the one-row header.</summary>
    private const double TabsBarMargins = 24 + 16;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
    }

    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e) => LayoutHeader();

    /// <summary>The rail's language button opens its menu beside it on a left click (a right click opens it anyway).</summary>
    private void OnLanguageButtonClick(object sender, RoutedEventArgs e)
    {
        if (LanguageButton.ContextMenu is not { } menu)
            return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = LanguageButton;
        menu.Placement = PlacementMode.Right;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Name, tabs and actions share one row when the name keeps at least <see cref="MinNameRoom"/>; on narrow windows
    /// the tabs go below the name. The name trims to the room that is left either way.
    /// </summary>
    private void LayoutHeader()
    {
        // The bar's inner width: the grid itself may be wider than the bar while the name is not limited yet.
        var width = HeaderBar.ActualWidth - HeaderBar.Padding.Left - HeaderBar.Padding.Right;
        var actions = HeaderActions.ActualWidth;
        var oneRowRoom = width - actions - MainTabsBar.ActualWidth - TabsBarMargins;
        var oneRow = oneRowRoom >= MinNameRoom;

        Grid.SetRow(MainTabsBar, oneRow ? 0 : 1);
        Grid.SetColumn(MainTabsBar, oneRow ? 1 : 0);
        Grid.SetColumnSpan(MainTabsBar, oneRow ? 1 : 4);
        MainTabsBar.Margin = oneRow ? new Thickness(24, 0, 16, 0) : new Thickness(0, 12, 0, 0);
        HeaderName.MaxWidth = Math.Max(0, oneRow ? oneRowRoom : width - actions - 16);
    }
}
