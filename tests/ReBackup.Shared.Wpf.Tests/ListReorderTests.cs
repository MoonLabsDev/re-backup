using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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

    [Theory]
    [InlineData(0, 0)]      // above the realized items: their top
    [InlineData(1, 0)]
    [InlineData(7, 86)]     // below them: their bottom
    public void Without_realized_neighbours_the_line_sits_at_the_realized_edge(int insertion, double expected)
    {
        ItemSpan[] realized = [new(3, 0, 40), new(4, 46, 40)];

        ListReorder.InsertionLineY(realized, insertion).Should().Be(expected);
    }

    [Fact]
    public void Without_realized_items_the_line_is_at_the_top()
    {
        ListReorder.InsertionLineY([], 2).Should().Be(0);
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

    [Fact]
    public void A_moved_item_keeps_the_keyboard_focus_so_the_next_move_works_too()
    {
        RunOnSta(() =>
        {
            var items = new ObservableCollection<string>(Enumerable.Range(0, 30).Select(i => $"Item {i}"));
            var list = new ListBox { ItemsSource = items };   // a VirtualizingStackPanel regenerates moved containers
            IRelayCommand up = null!;
            up = new RelayCommand(() =>
            {
                var index = items.IndexOf((string)list.SelectedItem);
                items.Move(index, index - 1);
            });
            list.InputBindings.Add(new KeyBinding(up, Key.Up, ModifierKeys.Alt));
            ListReorder.SetMoveCommand(list, new RelayCommand<ListMove>(move => items.Move(move.From, move.To)));
            var window = new Window
            {
                Content = list, Width = 300, Height = 200, Left = -10000, Top = -10000,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            };
            window.Show();
            try
            {
                window.Activate();
                list.SelectedItem = "Item 25";
                list.ScrollIntoView("Item 25");
                Pump();
                ((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem("Item 25")).Focus();
                Pump();

                for (var i = 0; i < 2; i++)
                {
                    // What the Alt+Up key binding does: it runs only while the focus is in the list.
                    list.IsKeyboardFocusWithin.Should().BeTrue("move {0} needs the focus in the list", i + 1);
                    up.Execute(null);
                    Pump();
                }

                items.IndexOf("Item 25").Should().Be(23);
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem("Item 25");
                container.Should().NotBeNull("the moved item is scrolled into view");
                container.IsKeyboardFocused.Should().BeTrue();
                list.SelectedItem.Should().Be("Item 25");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Runs what the dispatcher has queued down to the Loaded priority (layout included).</summary>
    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

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
