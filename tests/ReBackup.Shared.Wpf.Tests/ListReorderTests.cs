using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using ReBackup.Shared.Wpf.Controls;

namespace ReBackup.Shared.Wpf.Tests;

public class ListReorderTests
{
    // Three items of height 40 with a 6 px gap: 0–40, 46–86, 92–132.
    private static readonly ItemSpan[] Items = [new(0, 0, 40), new(1, 46, 40), new(2, 92, 40)];

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(10, 0)]
    [InlineData(19.9, 0)]
    [InlineData(20, 1)]
    [InlineData(43, 1)]
    [InlineData(66, 2)]
    [InlineData(111.9, 2)]
    [InlineData(112, 3)]
    [InlineData(500, 3)]
    public void The_insertion_index_is_the_gap_nearest_to_the_pointer(double y, int expected)
    {
        ListReorder.InsertionIndex(Items, y, count: 3).Should().Be(expected);
    }

    [Fact]
    public void Without_realized_items_the_insertion_index_is_the_end()
    {
        ListReorder.InsertionIndex([], 10, count: 4).Should().Be(4);
    }

    [Fact]
    public void Below_the_last_realized_item_the_insertion_index_follows_it()
    {
        ListReorder.InsertionIndex([new(3, 0, 40), new(4, 46, 40)], 200, count: 9).Should().Be(5);
    }

    [Theory]
    [InlineData(0, 2, 1)]   // down: the gap after the next item
    [InlineData(0, 3, 2)]   // to the end
    [InlineData(2, 0, 0)]   // to the top
    [InlineData(2, 1, 1)]
    [InlineData(1, 3, 2)]
    public void The_drop_index_is_the_index_after_the_move(int from, int insertion, int expected)
    {
        ListReorder.DropIndex(from, insertion, count: 3).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, 1)]   // the gap above the item itself
    [InlineData(1, 2)]   // the gap below the item itself
    [InlineData(0, 0)]
    [InlineData(2, 3)]
    public void Dropping_next_to_itself_is_no_move(int from, int insertion)
    {
        ListReorder.DropIndex(from, insertion, count: 3).Should().BeNull();
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(3, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 4)]
    public void Out_of_range_indexes_are_no_move(int from, int insertion)
    {
        ListReorder.DropIndex(from, insertion, count: 3).Should().BeNull();
    }

    [Theory]
    [InlineData(0, 0)]      // above the first item, at its top edge
    [InlineData(1, 43)]     // in the middle of the gap
    [InlineData(2, 89)]
    [InlineData(3, 132)]    // below the last item, at its bottom edge
    public void The_insertion_line_sits_in_the_gap(int insertion, double expected)
    {
        ListReorder.InsertionLineY(Items, insertion).Should().Be(expected);
    }

    [Fact]
    public void Setting_a_move_command_lets_the_list_accept_drops()
    {
        RunOnSta(() =>
        {
            var list = new ListBox();
            var command = new RelayCommand<ListMove>(_ => { });

            ListReorder.SetMoveCommand(list, command);

            list.AllowDrop.Should().BeTrue();
            ListReorder.GetMoveCommand(list).Should().BeSameAs(command);

            ListReorder.SetMoveCommand(list, null);
            list.AllowDrop.Should().BeFalse();
        });
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
