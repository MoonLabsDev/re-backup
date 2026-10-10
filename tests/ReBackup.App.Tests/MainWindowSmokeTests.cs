using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    [Fact]
    public void The_versions_view_loads_with_the_take_over_menu_for_folders_of_other_computers()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fixture = new MainViewModelFixture();
            fixture.AddPlan("a", "Alpha");
            var vm = fixture.Create();
            var view = new ReBackup.App.Views.VersionsView { DataContext = vm.Plans[0] };
            view.Measure(new Size(1280, 800));
            view.Arrange(new Rect(new Size(1280, 800)));
            view.UpdateLayout();

            var list = FindAll<ListBox>(view).Should()
                .ContainSingle(l => ReferenceEquals(l.ItemsSource, vm.Plans[0].Versions.ForeignRows)).Subject;
            list.ContextMenu!.Items.OfType<MenuItem>()
                .Select(item => BindingOperations.GetBinding(item, MenuItem.CommandProperty)?.Path.Path)
                .Should().Equal("Versions.TakeOverCommand");
        });
    }

    [Fact]
    public void The_plan_view_shows_the_S3_fields_of_an_S3_target_and_hides_the_target_full_toggle()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fixture = new MainViewModelFixture();
            fixture.AddConnection("c1", "Offsite", "offsite");
            fixture.Store.Save(new ReBackup.Core.Plans.BackupPlan
            {
                Id = "a",
                Name = "Alpha",
                Target = new ReBackup.Storage.StorageLocation("s3", "docs", "c1"),
            });
            var vm = fixture.Create();
            var editor = vm.Plans[0];
            var view = new ReBackup.App.Views.PlanView { DataContext = editor };
            view.Measure(new Size(1280, 800));
            view.Arrange(new Rect(new Size(1280, 800)));
            view.UpdateLayout();

            var combos = FindAll<ComboBox>(view).Where(c => ReferenceEquals(c.ItemsSource, editor.TargetS3.Connections)).ToList();
            combos.Should().NotBeEmpty("the target's connection picker shows the saved connections");
            combos.Should().ContainSingle(c => Shown(c)).Which.SelectedValue.Should().Be("c1");
            FindAll<RadioButton>(view).Should().HaveCount(4, "Folder | S3 for the source and for the target");
            var freeSpace = FindAll<CheckBox>(view)
                .Single(c => BindingOperations.GetBinding(c, ToggleButton.IsCheckedProperty)?.Path.Path == "FreeSpaceByRetention");
            Shown(freeSpace).Should().BeFalse();

            editor.TargetIsFolder = true;
            view.UpdateLayout();
            Shown(freeSpace).Should().BeTrue();
        });
    }

    [Fact]
    public void Switching_the_plan_view_between_an_S3_plan_and_a_folder_plan_changes_neither()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fixture = new MainViewModelFixture();
            fixture.AddConnection("c1", "Offsite", "offsite");
            fixture.Store.Save(new ReBackup.Core.Plans.BackupPlan
            {
                Id = "a",
                Name = "Alpha",
                Source = new ReBackup.Storage.StorageLocation("s3", "camera", "c1"),
                Target = new ReBackup.Storage.StorageLocation("s3", "docs", "c1"),
            });
            fixture.AddPlan("b", "Beta");
            var vm = fixture.Create();
            var (a, b) = (vm.Plans.Single(p => p.Id == "a"), vm.Plans.Single(p => p.Id == "b"));
            var view = new ReBackup.App.Views.PlanView();
            void Show(PlanEditorViewModel editor)
            {
                view.DataContext = editor;
                view.Measure(new Size(1280, 800));
                view.Arrange(new Rect(new Size(1280, 800)));
                view.UpdateLayout();
            }

            Show(a);
            Show(b);
            Show(a);
            Show(b);

            a.SourceIsS3.Should().BeTrue();
            a.TargetIsS3.Should().BeTrue();
            a.IsDirty.Should().BeFalse();
            b.SourceIsS3.Should().BeFalse();
            b.TargetIsS3.Should().BeFalse();
            b.IsDirty.Should().BeFalse();
            FindAll<RadioButton>(view).Where(r => r.IsChecked == true).Should().HaveCount(2, "Folder for B's source and target");
        });
    }

    [Fact]
    public void The_settings_window_loads_with_the_S3_entries()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fixture = new MainViewModelFixture();
            var settings = new SettingsViewModel(fixture.SettingsStore, fixture.Settings, fixture.Paths, fixture.Root,
                fixture.Dialogs, () => true, () => false, _ => { }, "en-US", _ => { }, () => { }, () => { });
            var window = new SettingsWindow(settings);
            var content = Layout(window);

            FindAll<Button>(content)
                .Select(b => BindingOperations.GetBinding(b, ButtonBase.CommandProperty)?.Path.Path)
                .Should().Contain([nameof(SettingsViewModel.ManageS3AccountsCommand), nameof(SettingsViewModel.ManageS3ConnectionsCommand)]);
        });
    }

    [Fact]
    public void The_S3_connections_window_loads()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fixture = new MainViewModelFixture();
            fixture.AddConnection("c1", "Offsite", "offsite");
            var vm = fixture.Create();
            var window = new S3ConnectionsWindow(new S3ConnectionsViewModel(fixture.S3, fixture.Dialogs, vm.PlansUsingConnection));
            var content = Layout(window);

            FindAll<ListView>(content).Should().ContainSingle().Which.Items.Count.Should().Be(1);
        });
    }

    /// <summary>Whether the element and all its ancestors are Visible (IsVisible needs a shown window).</summary>
    private static bool Shown(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
                return false;
        }
        return true;
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
