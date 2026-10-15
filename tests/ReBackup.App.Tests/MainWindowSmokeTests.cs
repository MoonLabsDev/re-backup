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

    [Theory]
    [InlineData("Retention")]
    [InlineData("Versions")]
    public void The_retention_and_versions_views_load_with_the_coverage_timeline(string tab)
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fixture = new MainViewModelFixture();
            fixture.AddPlan("a", "Alpha");
            var vm = fixture.Create();
            FrameworkElement view = tab == "Retention"
                ? new ReBackup.App.Views.RetentionView { DataContext = vm.Plans[0] }
                : new ReBackup.App.Views.VersionsView { DataContext = vm.Plans[0] };
            view.Measure(new Size(1280, 800));
            view.Arrange(new Rect(new Size(1280, 800)));
            view.UpdateLayout();

            var prefix = tab == "Retention" ? "RetentionPreview." : "Versions.";
            var control = FindAll<RetentionCoverageControl>(view).Should().ContainSingle().Subject;
            string? PathOf(DependencyProperty property) => BindingOperations.GetBinding(control, property)?.Path.Path;
            PathOf(RetentionCoverageControl.LanesProperty).Should().Be(prefix + "CoverageLanes");
            PathOf(RetentionCoverageControl.LaneLabelsProperty).Should().Be(prefix + "CoverageLabels");
            PathOf(RetentionCoverageControl.SelectCommandProperty).Should().Be(prefix + "SelectCoverageCommand");
            PathOf(RetentionCoverageControl.SlotTextProperty).Should().Be(prefix + "CoverageSlotText");
            PathOf(RetentionCoverageControl.SelectedIdProperty).Should()
                .Be(tab == "Retention" ? "RetentionPreview.SelectedNowRow.Name" : "Versions.SelectedVersion.Name");
            if (tab == "Versions")
                PathOf(RetentionCoverageControl.SelectedDateProperty).Should().Be("Versions.SelectedVersion.Info.LocalTime");
        });
    }

    [Fact]
    public void The_history_view_loads_with_the_coverage_timeline_linked_to_the_runs()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fixture = new MainViewModelFixture();
            fixture.AddPlan("a", "Alpha");
            var vm = fixture.Create();
            var view = new ReBackup.App.Views.HistoryView { DataContext = vm.Plans[0] };
            view.Measure(new Size(1280, 800));
            view.Arrange(new Rect(new Size(1280, 800)));
            view.UpdateLayout();

            var control = FindAll<RetentionCoverageControl>(view).Should().ContainSingle().Subject;
            string? PathOf(DependencyObject target, DependencyProperty property) =>
                BindingOperations.GetBinding(target, property)?.Path.Path;
            PathOf(control, RetentionCoverageControl.LanesProperty).Should().Be("HistoryCoverage.CoverageLanes");
            PathOf(control, RetentionCoverageControl.LaneLabelsProperty).Should().Be("HistoryCoverage.CoverageLabels");
            PathOf(control, RetentionCoverageControl.SelectCommandProperty).Should().Be("HistoryCoverage.SelectCoverageCommand");
            PathOf(control, RetentionCoverageControl.SelectedIdProperty).Should().Be("HistoryCoverage.SelectedId");
            PathOf(control, RetentionCoverageControl.SelectedDateProperty).Should().Be("HistoryCoverage.SelectedDate");
            PathOf(control, RetentionCoverageControl.SlotTextProperty).Should().Be("HistoryCoverage.CoverageSlotText");
            var grid = FindAll<DataGrid>(view).Should().ContainSingle().Subject;
            PathOf(grid, Selector.SelectedItemProperty).Should().Be("HistoryCoverage.SelectedRun");
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
            KindSwitches(view).Should().HaveCount(6, "Folder | S3 | Drive for the source and for the target");
            var freeSpace = FindAll<CheckBox>(view)
                .Single(c => BindingOperations.GetBinding(c, ToggleButton.IsCheckedProperty)?.Path.Path == "FreeSpaceByRetention");
            Shown(freeSpace).Should().BeFalse();

            editor.TargetIsFolder = true;
            view.UpdateLayout();
            Shown(freeSpace).Should().BeTrue();
        });
    }

    [Fact]
    public void Switching_the_plan_view_between_S3_Drive_and_folder_plans_changes_none()
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
            fixture.AddDriveConnection("d1", "Team", "team", "team");
            fixture.Store.Save(new ReBackup.Core.Plans.BackupPlan
            {
                Id = "c",
                Name = "Gamma",
                Source = new ReBackup.Storage.StorageLocation("gdrive", "photos", "d1"),
                Target = new ReBackup.Storage.StorageLocation("gdrive", "backups", "d1"),
            });
            var vm = fixture.Create();
            var (a, b) = (vm.Plans.Single(p => p.Id == "a"), vm.Plans.Single(p => p.Id == "b"));
            var c = vm.Plans.Single(p => p.Id == "c");
            var view = new ReBackup.App.Views.PlanView();
            void Show(PlanEditorViewModel editor)
            {
                view.DataContext = editor;
                view.Measure(new Size(1280, 800));
                view.Arrange(new Rect(new Size(1280, 800)));
                view.UpdateLayout();
            }

            Show(a);
            Show(c);
            Show(b);
            Show(c);
            Show(a);
            Show(c);
            Show(b);

            a.SourceIsS3.Should().BeTrue();
            a.TargetIsS3.Should().BeTrue();
            a.SourceIsDrive.Should().BeFalse();
            a.TargetIsDrive.Should().BeFalse();
            a.SourceS3.ConnectionId.Should().Be("c1");
            a.TargetS3.ConnectionId.Should().Be("c1");
            a.IsDirty.Should().BeFalse();
            c.SourceIsDrive.Should().BeTrue();
            c.TargetIsDrive.Should().BeTrue();
            c.SourceIsS3.Should().BeFalse();
            c.TargetIsS3.Should().BeFalse();
            c.SourceDrive.ConnectionId.Should().Be("d1");
            c.TargetDrive.ConnectionId.Should().Be("d1");
            c.SourceDrive.Path.Should().Be("photos");
            c.TargetDrive.Path.Should().Be("backups");
            c.IsDirty.Should().BeFalse();
            b.SourceIsDrive.Should().BeFalse();
            b.TargetIsDrive.Should().BeFalse();
            b.SourceIsS3.Should().BeFalse();
            b.TargetIsS3.Should().BeFalse();
            b.IsDirty.Should().BeFalse();
            KindSwitches(view).Where(r => r.IsChecked == true).Should().HaveCount(2, "Folder for B's source and target");
        });
    }

    [Fact]
    public void The_plan_view_shows_the_Drive_fields_of_a_Drive_target_and_hides_the_target_full_toggle()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fixture = new MainViewModelFixture();
            fixture.AddDriveConnection("d1", "Team", "team", "team");
            fixture.Store.Save(new ReBackup.Core.Plans.BackupPlan
            {
                Id = "a",
                Name = "Alpha",
                Target = new ReBackup.Storage.StorageLocation("gdrive", "docs", "d1"),
            });
            var vm = fixture.Create();
            var editor = vm.Plans[0];
            var view = new ReBackup.App.Views.PlanView { DataContext = editor };
            view.Measure(new Size(1280, 800));
            view.Arrange(new Rect(new Size(1280, 800)));
            view.UpdateLayout();

            var combos = FindAll<ComboBox>(view).Where(c => ReferenceEquals(c.ItemsSource, editor.TargetDrive.Connections)).ToList();
            combos.Should().ContainSingle(c => Shown(c)).Which.SelectedValue.Should().Be("d1");
            FindAll<TextBox>(view).Where(Shown).Select(t => t.Text).Should().Contain("docs", "the path is shown");
            KindSwitches(view).Where(r => r.IsChecked == true).Select(r => r.CommandParameter)
                .Should().Equal("Folder", "Drive");
            var freeSpace = FindAll<CheckBox>(view)
                .Single(c => BindingOperations.GetBinding(c, ToggleButton.IsCheckedProperty)?.Path.Path == "FreeSpaceByRetention");
            Shown(freeSpace).Should().BeFalse();

            editor.ChooseTargetKindCommand.Execute("Folder");
            view.UpdateLayout();
            Shown(freeSpace).Should().BeTrue();
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
                .Should().Contain([nameof(SettingsViewModel.ManageS3AccountsCommand), nameof(SettingsViewModel.ManageS3ConnectionsCommand),
                    nameof(SettingsViewModel.ManageDriveAccountsCommand), nameof(SettingsViewModel.ManageDriveConnectionsCommand)]);
        });
    }

    [Fact]
    public void The_Drive_windows_and_dialogs_load()
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            using var fixture = new MainViewModelFixture();
            fixture.AddDriveConnection("d1", "Team", "team", "team");
            var vm = fixture.Create();

            var connections = Layout(new DriveConnectionsWindow(
                new DriveConnectionsViewModel(fixture.Drive, fixture.Dialogs, vm.PlansUsingDriveConnection)));
            FindAll<ListView>(connections).Should().ContainSingle().Which.Items.Count.Should().Be(1);

            var accounts = Layout(new DriveAccountsWindow(new DriveAccountsViewModel(fixture.Drive, fixture.Dialogs)));
            FindAll<ListView>(accounts).Should().ContainSingle().Which.Items.Count.Should().Be(1);

            var account = Layout(new DriveAccountDialog(
                new DriveAccountDialogViewModel(fixture.Drive.Accounts[0], _ => false, () => null)));
            FindAll<TextBox>(account).Select(t => t.Text).Should().Contain(MainViewModelFixture.DriveEmail);

            var connection = Layout(new DriveConnectionDialog(new DriveConnectionDialogViewModel(fixture.Drive.Store.TryGet("d1"),
                () => fixture.Drive.Store.LoadAccounts(), _ => false, fixture.Drive.ApiForAccount, () => null)));
            FindAll<ComboBox>(connection).Should().HaveCount(2, "the account and the shared drive");
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

    [Fact]
    public void The_copy_and_verify_dialogs_load_and_the_plan_menu_offers_them()
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
                Incremental = true,
            });
            var vm = fixture.Create();
            var window = new MainWindow { DataContext = vm };
            var list = FindAll<ListBox>(Layout(window)).Single(l => ReferenceEquals(l.ItemsSource, vm.Plans));
            list.ContextMenu!.Items.OfType<MenuItem>()
                .Select(item => BindingOperations.GetBinding(item, MenuItem.CommandProperty)?.Path.Path)
                .Should().Contain([nameof(MainViewModel.CopyPlanCommand), nameof(MainViewModel.VerifyBackupCommand),
                    nameof(MainViewModel.ReleaseLockCommand)]);

            vm.CopyPlanCommand.Execute(vm.Plans[0]);
            var copy = fixture.Dialogs.CopyDialogs.Single();
            copy.ChooseTargetKindCommand.Execute("S3");
            var copyContent = Layout(new ReBackup.App.Views.CopyPlanDialog(copy));
            FindAll<ComboBox>(copyContent).Should().Contain(c => ReferenceEquals(c.ItemsSource, copy.TargetS3.Connections) && Shown(c),
                "the dialog reuses the Plan tab's target picker");
            KindSwitches(copyContent).Should().HaveCount(3);

            vm.VerifyBackupCommand.Execute(vm.Plans[0]);
            var verifyContent = Layout(new ReBackup.App.Views.VerifyDialog(fixture.Dialogs.VerifyDialogs.Single()));
            FindAll<RadioButton>(verifyContent).Select(r => r.CommandParameter).Should().Equal("Quick", "Thorough");
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

    /// <summary>The Folder | S3 | Drive segments of the source and target (not the storage format or compare switches).</summary>
    private static IEnumerable<RadioButton> KindSwitches(DependencyObject root) =>
        FindAll<RadioButton>(root).Where(r => r.CommandParameter is "Folder" or "S3" or "Drive");

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
