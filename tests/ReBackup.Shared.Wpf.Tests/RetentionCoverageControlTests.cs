using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using ReBackup.Shared.Retention;
using ReBackup.Shared.Wpf.Controls;

namespace ReBackup.Shared.Wpf.Tests;

public class RetentionCoverageControlTests
{
    private static readonly RetentionRule Daily = new() { Period = RetentionPeriod.Daily, Keep = 7 };
    private static readonly RetentionRule Weekly = new() { Period = RetentionPeriod.Weekly, Anchor = "Sunday", Keep = 4 };

    private sealed class RecordingCommand : ICommand
    {
        public List<object?> Executed { get; } = [];
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => Executed.Add(parameter);
    }

    private static CoverageVersion Version(string id, int day) =>
        new(id, new DateTime(2026, 10, day, 12, 0, 0), CoverageQuality.Ok, true, null);

    private static CoverageSlot Slot(int day, SlotState state, CoverageVersion? version = null) =>
        new(new DateOnly(2026, 10, day), new DateOnly(2026, 10, day + 1), state, version);

    private static IReadOnlyList<CoverageLane> TwoLanes() =>
    [
        new CoverageLane(0, Daily,
        [
            Slot(1, SlotState.BeforeFirst), Slot(2, SlotState.Present, Version("v2", 2)), Slot(3, SlotState.Missing),
            Slot(4, SlotState.PresentWithWarnings, Version("v4", 4)), Slot(5, SlotState.Pending),
        ]),
        new CoverageLane(1, Weekly, [Slot(1, SlotState.Present, Version("w1", 1)), Slot(8, SlotState.Missing)]),
    ];

    private static RetentionCoverageControl Create(IReadOnlyList<CoverageLane> lanes, double width = 600, string theme = "Dark")
    {
        var control = new RetentionCoverageControl { Lanes = lanes, LaneLabels = ["Daily 7", "Weekly 4"] };
        control.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/ReBackup.Shared.Wpf;component/Theme/Colors.{theme}.xaml", UriKind.Absolute),
        });
        control.Measure(new Size(width, double.PositiveInfinity));
        control.Arrange(new Rect(control.DesiredSize));
        control.UpdateLayout();
        return control;
    }

    private static void Render(FrameworkElement control)
    {
        var bitmap = new RenderTargetBitmap((int)control.ActualWidth, Math.Max(1, (int)control.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void It_renders_in_both_themes(string theme)
    {
        Sta.Run(() =>
        {
            var control = Create(TwoLanes(), theme: theme);
            control.SelectedId = "v2";
            control.ActualHeight.Should().Be(52);
            var render = () => Render(control);
            render.Should().NotThrow();
        });
    }

    [Fact]
    public void HitTest_maps_a_point_to_the_slot_of_the_lane()
    {
        var lanes = TwoLanes();
        var size = new Size(600, 52);
        // 5 slots fill the 422 px plot (170 to 592): pitch 84.4.
        RetentionCoverageControl.HitTest(size, lanes, new Point(172, 5), out var first).Should().Be(0);
        first!.Start.Day.Should().Be(1);
        RetentionCoverageControl.HitTest(size, lanes, new Point(590, 11), out var last).Should().Be(0);
        last!.State.Should().Be(SlotState.Pending);
        RetentionCoverageControl.HitTest(size, lanes, new Point(300, 5), out var second).Should().Be(0);
        second!.Version!.Id.Should().Be("v2");
        // second lane: 2 slots of 211 px
        RetentionCoverageControl.HitTest(size, lanes, new Point(590, 30), out var week).Should().Be(1);
        week!.Start.Day.Should().Be(8);
        RetentionCoverageControl.HitTest(size, lanes, new Point(180, 30), out var week1).Should().Be(1);
        week1!.Version!.Id.Should().Be("w1");
    }

    [Fact]
    public void HitTest_outside_the_cells_hits_nothing()
    {
        var lanes = TwoLanes();
        var size = new Size(600, 52);
        RetentionCoverageControl.HitTest(size, lanes, new Point(50, 5), out var slot).Should().BeNull();
        slot.Should().BeNull();
        RetentionCoverageControl.HitTest(size, lanes, new Point(300, 60), out _).Should().BeNull();
        RetentionCoverageControl.HitTest(size, lanes, new Point(599, 5), out _).Should().BeNull();
    }

    [Fact]
    public void Clicking_a_present_cell_selects_its_version_and_other_cells_do_nothing()
    {
        Sta.Run(() =>
        {
            var command = new RecordingCommand();
            var control = Create(TwoLanes());
            control.SelectCommand = command;

            control.Activate(new Point(256, 5)).Should().BeTrue();                 // Present (day 2)
            control.Activate(new Point(425, 5)).Should().BeTrue();                 // PresentWithWarnings (day 4)
            command.Executed.Should().Equal("v2", "v4");

            control.Activate(new Point(172, 5)).Should().BeFalse();                // BeforeFirst
            control.Activate(new Point(340, 5)).Should().BeFalse();                // Missing
            control.Activate(new Point(590, 5)).Should().BeFalse();                // Pending
            control.Activate(new Point(590, 30)).Should().BeFalse();               // Missing, other lane
            command.Executed.Should().HaveCount(2);
        });
    }

    [Fact]
    public void The_tooltip_text_comes_from_SlotText()
    {
        Sta.Run(() =>
        {
            var control = Create(TwoLanes());
            control.SlotText = slot => $"{slot.State} {slot.Start:dd.MM.}";

            control.UpdateToolTip(new Point(340, 5));
            control.ToolTip.Should().Be("Missing 03.10.");
            control.UpdateToolTip(new Point(50, 5));
            control.ToolTip.Should().BeNull();
        });
    }

    [Fact]
    public void Many_slots_draw_newest_and_count_the_rest()
    {
        Sta.Run(() =>
        {
            var start = new DateOnly(2000, 1, 1);
            var slots = Enumerable.Range(0, 9999)
                .Select(i => new CoverageSlot(start.AddDays(i), start.AddDays(i + 1), SlotState.Missing, null)).ToArray();
            var lanes = new[] { new CoverageLane(0, Daily, slots) };
            var control = Create(lanes, width: 600);
            Render(control);   // warm up fonts and JIT

            var watch = Stopwatch.StartNew();
            Render(control);
            watch.Stop();
            watch.ElapsedMilliseconds.Should().BeLessThan(200);

            var visible = RetentionCoverageControl.VisibleSlots(new Size(600, 26), slots.Length);
            visible.Should().Be(211);                                  // (600 - 170 - 8) / 2 px
            (slots.Length - visible).Should().Be(9788);                // the "+9788" label
            RetentionCoverageControl.HitTest(new Size(600, 26), lanes, new Point(591, 5), out var newest).Should().Be(0);
            newest.Should().BeSameAs(slots[^1]);
            RetentionCoverageControl.HitTest(new Size(600, 26), lanes, new Point(171, 5), out var oldest).Should().Be(0);
            oldest.Should().BeSameAs(slots[^211]);
        });
    }

    /// <summary>A daily lane of 5 days and a weekly lane of 2 weeks (Oct 1-8, Oct 8-15).</summary>
    private static IReadOnlyList<CoverageLane> DailyAndWeekly() =>
    [
        new CoverageLane(0, Daily,
        [
            Slot(1, SlotState.BeforeFirst), Slot(2, SlotState.Present, Version("v2", 2)), Slot(3, SlotState.Missing),
            Slot(4, SlotState.PresentWithWarnings, Version("v4", 4)), Slot(5, SlotState.Pending),
        ]),
        new CoverageLane(1, Weekly,
        [
            new CoverageSlot(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8), SlotState.Present, Version("v2", 2)),
            new CoverageSlot(new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 15), SlotState.Pending, null),
        ]),
    ];

    [Fact]
    public void The_selected_version_is_ringed_with_a_gap_in_every_lane_that_draws_it()
    {
        var layout = RetentionCoverageControl.SelectionLayout(new Size(600, 52), DailyAndWeekly(), "v2");

        // Daily: cell 1 of 5 (pitch 84.4 from x 170, 1 px gap), 18 px high from y 4; the 2 px pen's centre line lies
        // 2 px gap + 1 px half pen outside it.
        layout.Rings.Should().HaveCount(2);
        layout.Rings[0].X.Should().BeApproximately(254.4 - 3, 1e-9);
        layout.Rings[0].Y.Should().BeApproximately(1, 1e-9);
        layout.Rings[0].Width.Should().BeApproximately(83.4 + 6, 1e-9);
        layout.Rings[0].Height.Should().BeApproximately(24, 1e-9);
        // Weekly: cell 0 of 2 (pitch 211) in the second lane (from y 26).
        layout.Rings[1].Should().Be(new Rect(167, 27, 216, 24));
        layout.HiddenLanes.Should().BeEmpty();
    }

    [Fact]
    public void The_column_of_the_selected_date_is_highlighted_in_every_lane()
    {
        // v4 (Oct 4) is kept by the daily rule only; the weekly lane still marks its week.
        var layout = RetentionCoverageControl.SelectionLayout(new Size(600, 52), DailyAndWeekly(), "v4");

        layout.Rings.Should().ContainSingle();
        layout.Bands.Should().HaveCount(2);
        layout.Bands[0].X.Should().BeApproximately(170 + 3 * 84.4, 1e-9);
        layout.Bands[0].Y.Should().Be(0);
        layout.Bands[0].Height.Should().Be(26);                     // the full lane, not just the cell
        layout.Bands[0].Width.Should().BeApproximately(83.4, 1e-9);
        layout.Bands[1].Should().Be(new Rect(170, 26, 210, 26));   // Oct 1-8 in the weekly lane
    }

    [Fact]
    public void Nothing_is_highlighted_without_a_selection_or_for_an_unknown_id()
    {
        RetentionCoverageControl.SelectionLayout(new Size(600, 52), DailyAndWeekly(), null)
            .Should().BeSameAs(CoverageSelectionLayout.None);
        RetentionCoverageControl.SelectionLayout(new Size(600, 52), DailyAndWeekly(), "gone")
            .Should().BeSameAs(CoverageSelectionLayout.None);
    }

    [Fact]
    public void A_selected_version_behind_the_more_label_rings_the_label()
    {
        var start = new DateOnly(2000, 1, 1);
        var oldest = new CoverageVersion("old", new DateTime(2000, 1, 1, 12, 0, 0), CoverageQuality.Ok, true, null);
        var slots = Enumerable.Range(0, 500)
            .Select(i => new CoverageSlot(start.AddDays(i), start.AddDays(i + 1),
                i == 0 ? SlotState.Present : SlotState.Missing, i == 0 ? oldest : null)).ToArray();
        var lanes = new[] { new CoverageLane(0, Daily, slots) };

        var layout = RetentionCoverageControl.SelectionLayout(new Size(600, 26), lanes, "old");

        layout.HiddenLanes.Should().Equal(0);
        layout.Rings.Should().BeEmpty();
        layout.Bands.Should().BeEmpty();   // its slot is not drawn

        Sta.Run(() =>
        {
            var control = Create(lanes);
            control.SelectedId = "old";
            var render = () => Render(control);
            render.Should().NotThrow();
        });
    }

    [Fact]
    public void Status_colouring_fills_present_cells_with_the_accent()
    {
        Sta.Run(() =>
        {
            var control = Create(DailyAndWeekly(), theme: "Light");
            control.PresentColoring.Should().Be(CoveragePresentColoring.Status);

            Color(control.CellFill(SlotState.Present, RetentionPeriod.Weekly)).Should().Be(Hex("#1B796F"));
            Color(control.CellFill(SlotState.PresentWithWarnings, RetentionPeriod.Weekly)).Should().Be(Hex("#B45309"));
            control.CellFill(SlotState.Pending, RetentionPeriod.Weekly).Should().BeNull();
        });
    }

    [Fact]
    public void Period_colouring_fills_present_cells_with_the_lane_colour_and_keeps_warnings()
    {
        Sta.Run(() =>
        {
            var control = Create(DailyAndWeekly(), theme: "Light");
            control.PresentColoring = CoveragePresentColoring.Period;

            Color(control.CellFill(SlotState.Present, RetentionPeriod.Weekly)).Should().Be(Hex("#4A8BE6"));   // Chart.BlueLight
            Color(control.CellFill(SlotState.PresentWithWarnings, RetentionPeriod.Weekly)).Should().Be(Hex("#B45309"));
            var render = () => Render(control);
            render.Should().NotThrow();
        });
    }

    [Fact]
    public void PresentBrush_picks_the_period_colour_only_for_Period()
    {
        var status = Brushes.Teal;
        Brush[] periods = [Brushes.Red, Brushes.Green, Brushes.Blue, Brushes.Yellow];
        RetentionCoverageControl.PresentBrush(RetentionPeriod.Monthly, CoveragePresentColoring.Period, status, periods)
            .Should().BeSameAs(Brushes.Blue);
        RetentionCoverageControl.PresentBrush(RetentionPeriod.Monthly, CoveragePresentColoring.Status, status, periods)
            .Should().BeSameAs(status);
    }

    private static Color Color(Brush? brush) => ((SolidColorBrush)brush!).Color;

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}

internal static class Sta
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
