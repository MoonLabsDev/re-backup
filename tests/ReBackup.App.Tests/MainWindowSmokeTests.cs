using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Shared.Wpf.Controls;

namespace ReBackup.App.Tests;

/// <summary>
/// The main window's XAML loads with the app's resources and lays out with plans, without showing it; its plan list
/// is wired to the move commands. The look and the drag itself are checked by hand.
/// </summary>
public class MainWindowSmokeTests
{
    [Fact]
    public void The_window_loads_and_its_plan_list_reorders()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fixture = new MainViewModelFixture();
            fixture.AddPlan("a", "Alpha");
            fixture.AddPlan("b", "Beta");
            fixture.AddPlan("c", "Gamma");
            var vm = fixture.Create();
            var window = new MainWindow { DataContext = vm };
            var content = Layout(window);

            var list = FindAll<ListBox>(content).Should().ContainSingle(l => ReferenceEquals(l.ItemsSource, vm.Plans)).Subject;
            list.Items.Count.Should().Be(3);
            ListReorder.GetMoveCommand(list).Should().BeSameAs(vm.MovePlanCommand);

            var menuCommands = list.ContextMenu!.Items.OfType<MenuItem>()
                .Select(item => BindingOperations.GetBinding(item, MenuItem.CommandProperty)?.Path.Path).ToList();
            menuCommands.Should().Contain([nameof(MainViewModel.MoveUpCommand), nameof(MainViewModel.MoveDownCommand)]);

            var keys = list.InputBindings.OfType<KeyBinding>().ToList();
            keys.Should().Contain(k => k.Key == Key.Up && k.Modifiers == ModifierKeys.Alt && ReferenceEquals(k.Command, vm.MoveUpCommand));
            keys.Should().Contain(k => k.Key == Key.Down && k.Modifiers == ModifierKeys.Alt && ReferenceEquals(k.Command, vm.MoveDownCommand));

            // A move keeps the plan selected in the list as well; the view model's selection never changes.
            var selected = vm.Plans[0];
            vm.SelectedPlan = selected;
            list.SelectedItem.Should().BeSameAs(selected);
            var changes = new List<string?>();
            vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

            vm.MoveTo(0, 2);
            content.UpdateLayout();

            list.SelectedItem.Should().BeSameAs(selected);
            vm.SelectedPlan.Should().BeSameAs(selected);
            changes.Should().NotContain(nameof(MainViewModel.SelectedPlan));
            list.Items.Cast<object>().Last().Should().BeSameAs(selected);
        });
    }

    private static FrameworkElement Layout(Window window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1280, 800));
        content.Arrange(new Rect(new Size(1280, 800)));
        content.UpdateLayout();
        return content;
    }

    /// <summary>The app's own resources (theme dictionaries), with pack URIs resolved against the app's assembly.</summary>
    private static void EnsureApplication()
    {
        if (Application.Current is not null)
            return;
        new App().InitializeComponent();
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in FindAll<T>(child))
                yield return nested;
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }
}
