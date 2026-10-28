using System.Windows;
using FluentAssertions;
using ReBackup.Shared.Wpf.Services;

namespace ReBackup.Shared.Wpf.Tests;

/// <summary>A dialog opened over its owner is moved, and if need be shrunk, into the work area of its monitor.</summary>
public class WindowPlacementTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1032);

    [Fact]
    public void A_window_inside_the_work_area_stays_as_it_is()
    {
        WindowPlacement.FitInto(new Rect(100, 10, 600, 1010), WorkArea, minHeight: 820)
            .Should().Be(new Rect(100, 10, 600, 1010));
    }

    [Fact]
    public void A_window_reaching_below_the_work_area_is_moved_up()
    {
        WindowPlacement.FitInto(new Rect(100, 300, 600, 1010), WorkArea, minHeight: 820)
            .Should().Be(new Rect(100, 22, 600, 1010));
    }

    [Fact]
    public void A_window_taller_than_the_work_area_is_shrunk_to_it()
    {
        WindowPlacement.FitInto(new Rect(100, -50, 600, 1200), WorkArea, minHeight: 820)
            .Should().Be(new Rect(100, 0, 600, 1032));
    }

    [Fact]
    public void A_window_is_never_shrunk_below_its_minimum_and_then_starts_at_the_top()
    {
        WindowPlacement.FitInto(new Rect(100, 200, 600, 1010), new Rect(0, 0, 1366, 728), minHeight: 820)
            .Should().Be(new Rect(100, 0, 600, 820));
    }

    [Fact]
    public void A_window_left_or_right_of_the_work_area_is_moved_in()
    {
        var workArea = new Rect(-1920, -5, 1920, 1032);   // a monitor left of the primary one
        WindowPlacement.FitInto(new Rect(-300, 100, 600, 900), workArea, minHeight: 820)
            .Should().Be(new Rect(-600, 100, 600, 900));
        WindowPlacement.FitInto(new Rect(-2000, 100, 600, 900), workArea, minHeight: 820)
            .Should().Be(new Rect(-1920, 100, 600, 900));
    }
}
