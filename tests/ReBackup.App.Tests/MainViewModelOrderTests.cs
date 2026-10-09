using System.IO;
using FluentAssertions;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Core.Plans;
using ReBackup.Shared.Wpf.Controls;

namespace ReBackup.App.Tests;

/// <summary>The order of the plan list: loaded from and saved to settings.json, changed by the move commands.</summary>
public class MainViewModelOrderTests : IDisposable
{
    private readonly MainViewModelFixture _fixture = new();

    public MainViewModelOrderTests()
    {
        _fixture.AddPlan("a", "Alpha");
        _fixture.AddPlan("b", "Beta");
        _fixture.AddPlan("c", "Gamma");
    }

    public void Dispose() => _fixture.Dispose();

    private static IEnumerable<string> Ids(MainViewModel vm) => vm.Plans.Select(p => p.Id);

    [Fact]
    public void Without_a_saved_order_the_plans_are_sorted_by_name()
    {
        Ids(_fixture.Create()).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void The_saved_order_decides_and_unknown_plans_follow_by_name()
    {
        _fixture.AddPlan("d", "Delta");
        _fixture.Settings.PlanOrder = ["c", "gone", "a"];

        Ids(_fixture.Create()).Should().Equal("c", "a", "b", "d");
    }

    [Fact]
    public void MoveTo_moves_keeps_the_selection_and_saves_the_order()
    {
        var vm = _fixture.Create();
        var selected = vm.Plans[0];
        vm.SelectedPlan = selected;
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        vm.MoveTo(0, 2).Should().BeTrue();

        Ids(vm).Should().Equal("b", "c", "a");
        vm.SelectedPlan.Should().BeSameAs(selected);
        changes.Should().NotContain(nameof(MainViewModel.SelectedPlan));
        _fixture.Settings.PlanOrder.Should().Equal("b", "c", "a");
        _fixture.SettingsSaves.Should().Be(1);
        _fixture.SettingsStore.Load().PlanOrder.Should().Equal("b", "c", "a");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(-1, 0)]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    public void MoveTo_without_a_move_changes_and_saves_nothing(int from, int to)
    {
        var vm = _fixture.Create();

        vm.MoveTo(from, to).Should().BeFalse();

        Ids(vm).Should().Equal("a", "b", "c");
        _fixture.SettingsSaves.Should().Be(0);
    }

    [Fact]
    public void MoveUp_and_MoveDown_move_the_selected_plan_and_keep_it_selected()
    {
        var vm = _fixture.Create();
        vm.SelectedPlan = vm.Plans[1];
        var selected = vm.SelectedPlan;

        vm.MoveUpCommand.Execute(null);
        Ids(vm).Should().Equal("b", "a", "c");
        vm.SelectedPlan.Should().BeSameAs(selected);

        vm.MoveDownCommand.Execute(null);
        vm.MoveDownCommand.Execute(null);
        Ids(vm).Should().Equal("a", "c", "b");
        vm.SelectedPlan.Should().BeSameAs(selected);
        _fixture.SettingsSaves.Should().Be(3);
    }

    [Fact]
    public void The_move_commands_take_the_plan_as_parameter()
    {
        var vm = _fixture.Create();
        vm.SelectedPlan = vm.Plans[0];

        vm.MoveUpCommand.Execute(vm.Plans[2]);

        Ids(vm).Should().Equal("a", "c", "b");
        vm.SelectedPlan!.Id.Should().Be("a");
    }

    [Fact]
    public void The_first_plan_cannot_move_up_and_the_last_cannot_move_down()
    {
        var vm = _fixture.Create();

        vm.SelectedPlan = vm.Plans[0];
        vm.MoveUpCommand.CanExecute(null).Should().BeFalse();
        vm.MoveDownCommand.CanExecute(null).Should().BeTrue();

        vm.SelectedPlan = vm.Plans[2];
        vm.MoveUpCommand.CanExecute(null).Should().BeTrue();
        vm.MoveDownCommand.CanExecute(null).Should().BeFalse();

        vm.SelectedPlan = vm.Plans[1];
        vm.MoveUpCommand.CanExecute(null).Should().BeTrue();
        vm.MoveDownCommand.CanExecute(null).Should().BeTrue();

        vm.MoveUpCommand.Execute(null);   // now the first one
        vm.MoveUpCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Moving_at_the_edges_does_nothing()
    {
        var vm = _fixture.Create();
        vm.SelectedPlan = vm.Plans[0];

        vm.MoveUpCommand.Execute(null);
        vm.MoveDownCommand.Execute(vm.Plans[2]);

        Ids(vm).Should().Equal("a", "b", "c");
        _fixture.SettingsSaves.Should().Be(0);
    }

    [Fact]
    public void Without_a_selection_nothing_can_move()
    {
        var vm = _fixture.Create();
        vm.SelectedPlan = null;

        vm.MoveUpCommand.CanExecute(null).Should().BeFalse();
        vm.MoveDownCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Changing_the_selection_updates_the_move_commands()
    {
        var vm = _fixture.Create();
        vm.SelectedPlan = vm.Plans[0];
        var raised = 0;
        vm.MoveUpCommand.CanExecuteChanged += (_, _) => raised++;

        vm.SelectedPlan = vm.Plans[1];

        raised.Should().BeGreaterThan(0);
    }

    [Fact]
    public void The_drop_command_moves_by_index()
    {
        var vm = _fixture.Create();

        vm.MovePlanCommand.CanExecute(new ListMove(2, 0)).Should().BeTrue();
        vm.MovePlanCommand.CanExecute(new ListMove(1, 1)).Should().BeFalse();
        vm.MovePlanCommand.CanExecute(new ListMove(0, 3)).Should().BeFalse();

        vm.MovePlanCommand.Execute(new ListMove(2, 0));

        Ids(vm).Should().Equal("c", "a", "b");
    }

    [Fact]
    public void A_new_plan_goes_to_the_end_and_into_the_order_once_saved()
    {
        var vm = _fixture.Create();
        vm.MoveTo(2, 0);

        vm.NewPlanCommand.Execute(null);
        var created = vm.SelectedPlan!;
        created.Source = Directory.CreateDirectory(Path.Combine(_fixture.Root, "src")).FullName;
        created.Target = Path.Combine(_fixture.Root, "dst");

        vm.Plans.Last().Should().BeSameAs(created);
        vm.SaveCommand.Execute(null);

        created.IsNew.Should().BeFalse();
        _fixture.Settings.PlanOrder.Should().Equal("c", "a", "b", created.Id);
    }

    [Fact]
    public void Deleting_a_plan_drops_it_from_the_order()
    {
        var vm = _fixture.Create();
        vm.MoveTo(2, 0);
        vm.SelectedPlan = vm.Plans.Single(p => p.Id == "a");

        vm.DeletePlanCommand.Execute(null);

        _fixture.Settings.PlanOrder.Should().Equal("c", "b");
    }

    [Fact]
    public void A_reload_from_disk_keeps_the_order_and_appends_new_plans()
    {
        var vm = _fixture.Create();
        vm.MoveTo(2, 0);
        var selected = vm.SelectedPlan = vm.Plans[1];
        _fixture.AddPlan("e", "Epsilon");
        _fixture.AddPlan("d", "Delta");

        vm.ReloadFromDisk();

        Ids(vm).Should().Equal("c", "a", "b", "d", "e");
        vm.SelectedPlan.Should().BeSameAs(selected);
    }

    [Fact]
    public void A_reload_places_plans_the_saved_order_names()
    {
        var vm = _fixture.Create();
        _fixture.Settings.PlanOrder = ["e", "a", "b", "c", "d"];
        _fixture.AddPlan("d", "Delta");
        _fixture.AddPlan("e", "Epsilon");

        vm.ReloadFromDisk();

        Ids(vm).Should().Equal("a", "b", "c", "e", "d");
    }

    [Fact]
    public void A_failed_save_keeps_the_move_and_says_so_in_the_footer()
    {
        var vm = _fixture.Create();
        _fixture.SaveFailure = new IOException("disk full");

        vm.MoveTo(0, 1).Should().BeTrue();

        Ids(vm).Should().Equal("b", "a", "c");
        vm.StatusMessage.Should().Contain("disk full");
    }
}
